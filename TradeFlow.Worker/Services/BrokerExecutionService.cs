using TradeFlow.Worker.Data;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Shared broker execution logic called by both AlertPollingService and SignalRListenerService.
/// Handles order placement for approved BTO entries and position closing for STC/BTC exits.
/// Also writes trade analytics to the trade_metrics table for performance reporting.
/// </summary>
public class BrokerExecutionService
{
    private readonly IBrokerService _broker;
    private readonly PositionSizer _sizer;
    private readonly TradeGuard _guard;
    private readonly CsvTradeLogger _csv;
    private readonly DiscordNotificationService _discord;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrokerExecutionService> _logger;
    private readonly Func<bool> _isMarketOpen;

    public BrokerExecutionService(
        IBrokerService broker,
        PositionSizer sizer,
        TradeGuard guard,
        CsvTradeLogger csv,
        DiscordNotificationService discord,
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerExecutionService> logger,
        Func<bool>? isMarketOpen = null)
    {
        _broker       = broker;
        _sizer        = sizer;
        _guard        = guard;
        _csv          = csv;
        _discord      = discord;
        _scopeFactory = scopeFactory;
        _logger       = logger;
        // Injectable for testing; defaults to real ET market hours check
        _isMarketOpen = isMarketOpen ?? IsMarketOpenDefault;
    }

    /// <summary>
    /// Handles a BTO or averaging entry alert by sizing the order, running safety checks,
    /// placing the order with the broker, and logging the result to CSV and Discord.
    /// Skips silently if the market is closed or any safety check fails.
    /// </summary>
    public async Task HandleEntryAsync(
        Alert alert,
        AlertClassification classification,
        bool isAverage = false,
        CancellationToken ct = default)
    {
        var alertReceivedAt = DateTimeOffset.UtcNow;

        if (!_isMarketOpen())
        {
            _logger.LogDebug("Market closed, skipping order for {Symbol}", alert.Symbol);
            return;
        }

        var order = _sizer.Size(alert, classification, isAverage);
        if (order is null)
        {
            _logger.LogWarning(
                "PositionSizer returned null for {Symbol}, price may be missing or quantity < 1",
                alert.Symbol);
            return;
        }

        var blocked = await _guard.CheckAsync(order, ct);
        if (blocked is not null)
        {
            _logger.LogWarning(
                "TradeGuard blocked order for {Symbol}: {Reason}",
                alert.Symbol, blocked);
            return;
        }

        var accountBalance     = await _broker.GetAccountBalanceAsync(ct);
        var openPositionsValue = await _broker.GetOpenPositionsValueAsync(ct);
        var orderSubmittedAt   = DateTimeOffset.UtcNow;

        BrokerOrderResult result;
        try
        {
            result = await _broker.PlaceOrderAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker PlaceOrderAsync failed for {Symbol}, skipping", alert.Symbol);
            return;
        }

        if (result.Status == OrderStatus.Rejected || result.Status == OrderStatus.Cancelled)
        {
            _logger.LogWarning(
                "Broker rejected order for {Symbol} — {Reason}",
                alert.Symbol,
                result.RejectionReason ?? result.Status.ToString());
            return;
        }

        // If the fill confirmation timed out, verify the position actually exists in Gateway
        // before recording it. A pending status does not guarantee the order was filled —
        // it may have been rejected or expired with no fill. Querying GrossPositionValue
        // confirms capital was actually deployed before we open the TradeGuard position.
        if (result.Status == OrderStatus.Pending)
        {
            _logger.LogWarning(
                "Order timed out for {Symbol} — verifying position exists in Gateway before recording.",
                alert.Symbol);

            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            var verifyRecord = new TradeRecord
            {
                AlertId         = string.Empty,
                OrderId         = string.Empty,
                StopOrderId     = null,
                TargetOrderId   = null,
                UserName        = order.UserName,
                Symbol          = order.Symbol,
                TradeType       = order.TradeType,
                OptionsContract = order.OptionsContractSymbol,
                Direction       = order.Direction,
                Strike          = order.Strike,
                Expiration      = order.Expiration,
                Quantity        = order.Quantity,
                EntryPrice      = order.EstimatedEntryPrice,
                EntryAmount     = order.BudgetUsed,
                StopPrice       = order.StopPrice,
                TargetPrice     = order.TargetPrice,
                OpenedAt        = DateTimeOffset.UtcNow,
            };

            var positionPrice = await _broker.GetCurrentPositionPriceAsync(verifyRecord, ct);
            if (positionPrice <= 0)
            {
                _logger.LogWarning(
                    "Gateway shows no position for {Symbol} after timeout — order not recorded. " +
                    "The order was likely rejected or unfilled.",
                    alert.Symbol);
                return;
            }

            _logger.LogInformation(
                "Gateway confirmed position for {Symbol} at ${Price:F2} after timeout — recording trade.",
                alert.Symbol, positionPrice);
        }

        // Register position in TradeGuard memory and persist to database
        _guard.RegisterOpen(order, result);
        var trade = _guard.FindOpenTrade(
            order.UserName, order.OptionsContractSymbol, order.Symbol)!;

        // Persist open position to DB so TradeGuard can reload it after a restart
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.SaveAsync(new OpenPosition
            {
                OrderId        = trade.OrderId,
                StopOrderId    = trade.StopOrderId,
                TargetOrderId  = trade.TargetOrderId,
                AlertId        = trade.AlertId,
                UserName       = trade.UserName,
                Symbol         = trade.Symbol,
                TradeType      = trade.TradeType.ToString(),
                OptionsContract = trade.OptionsContract,
                Direction      = trade.Direction,
                Strike         = trade.Strike,
                Expiration     = trade.Expiration,
                Quantity       = trade.Quantity,
                EntryPrice     = trade.EntryPrice,
                EntryAmount    = trade.EntryAmount,
                StopPrice      = trade.StopPrice,
                TargetPrice    = trade.TargetPrice,
                OpenedAt       = trade.OpenedAt,
                IsAverage      = trade.IsAverage,
                HasAveraged    = trade.HasAveraged,
            }, ct);
        }

        await _csv.OpenTradeAsync(trade, ct);

        _logger.LogInformation(
            "ORDER PLACED — {Type} {Symbol} {Direction} × {Qty} @ ${Price:F2} | " +
            "Stop: ${Stop:F2} | Target: ${Target:F2} | OrderId: {OrderId}",
            order.TradeType, order.Symbol, order.Direction ?? "—",
            result.FillQuantity, result.FillPrice,
            order.StopPrice, order.TargetPrice, result.OrderId);

        await _discord.NotifyOrderPlacedAsync(trade, ct);

        var alertedPrice = alert.PricePaid ?? 0m;
        var slippagePct  = alertedPrice > 0
            ? (result.FillPrice - alertedPrice) / alertedPrice * 100
            : 0m;

        var exposurePct = accountBalance > 0
            ? (openPositionsValue + order.BudgetUsed) / accountBalance * 100
            : 0m;

        using var metricScope = _scopeFactory.CreateScope();
        var metrics = metricScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
        await metrics.OpenAsync(new TradeMetric
        {
            Id                        = result.OrderId,
            AlertId                   = alert.Id,
            TraderName                = order.UserName,
            Symbol                    = order.Symbol,
            TradeType                 = order.TradeType.ToString(),
            Direction                 = order.Direction,
            OptionsContract           = order.OptionsContractSymbol,
            IsAverage                 = isAverage,
            AlertReceivedAt           = alertReceivedAt,
            OrderSubmittedAt          = orderSubmittedAt,
            OrderFilledAt             = result.FilledAt,
            LatencyMs                 = (int)(result.FilledAt - alertReceivedAt).TotalMilliseconds,
            AlertedPrice              = alertedPrice,
            FillPrice                 = result.FillPrice,
            SlippagePct               = slippagePct,
            Quantity                  = result.FillQuantity,
            EntryAmount               = result.FillAmount,
            StopPrice                 = order.StopPrice,
            TargetPrice               = order.TargetPrice,
            AccountBalanceAtEntry     = accountBalance,
            OpenPositionsValueAtEntry = openPositionsValue,
            ExposurePct               = exposurePct,
        }, ct);
    }

    /// <summary>
    /// Handles a STC or BTC exit alert by matching it to an open position, closing
    /// the position with the broker, and updating the CSV and Discord with the P&amp;L result.
    /// Skips silently if no matching open position is found.
    /// </summary>
    public async Task HandleExitAsync(
        Alert alert,
        CancellationToken ct = default)
    {
        var trade = _guard.FindOpenTrade(
            alert.UserName ?? "",
            alert.OptionsContractSymbol,
            alert.Symbol ?? "");

        if (trade is null)
        {
            _logger.LogDebug(
                "Exit alert for {Symbol}, no matching open position found",
                alert.Symbol);
            return;
        }

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, TradeOutcome.XtradesExit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker ClosePositionAsync failed for {Symbol}, skipping", alert.Symbol);
            return;
        }

        var closedTrade = _guard.RegisterClose(
            alert.UserName ?? "",
            alert.OptionsContractSymbol,
            alert.Symbol ?? "",
            closeResult.FillPrice,
            TradeOutcome.XtradesExit);

        if (closedTrade is null) return;

        // Remove persisted open position now that it is closed
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.DeleteAsync(trade.OrderId, ct);
        }

        await _csv.CloseTradeAsync(closedTrade, ct);

        _logger.LogInformation(
            "POSITION CLOSED — {Symbol} × {Qty} @ ${Price:F2} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
            closedTrade.Symbol, closedTrade.Quantity, closeResult.FillPrice,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0, closedTrade.Result);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);

        if (closedTrade.PnL.HasValue && closedTrade.PnLPercent.HasValue)
        {
            using var metricScope = _scopeFactory.CreateScope();
            var metrics = metricScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
            await metrics.CloseAsync(
                orderId:    trade.OrderId,
                exitPrice:  closeResult.FillPrice,
                exitAmount: closeResult.FillAmount,
                pnl:        closedTrade.PnL.Value,
                pnlPct:     closedTrade.PnLPercent.Value,
                outcome:    closedTrade.Result.ToString(),
                closedAt:   closedTrade.ClosedAt ?? DateTimeOffset.UtcNow,
                ct:         ct);
        }
    }

    // Returns true if the current time falls within regular market hours (9:30am to 4:00pm ET, Mon-Fri)
    private static bool IsMarketOpenDefault()
    {
        var et = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var timeOfDay = et.TimeOfDay;
        return timeOfDay >= new TimeSpan(9, 30, 0)
            && timeOfDay < new TimeSpan(16, 0, 0);
    }
}