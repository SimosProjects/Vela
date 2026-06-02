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
    private readonly RiskEngineOptions _riskOptions;

    // Set by MarketSchedulerService to pause new entries without requiring a restart
    public static bool IsPaused { get; set; } = false;

    public BrokerExecutionService(
        IBrokerService broker,
        PositionSizer sizer,
        TradeGuard guard,
        CsvTradeLogger csv,
        DiscordNotificationService discord,
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerExecutionService> logger,
        IOptions<RiskEngineOptions> riskOptions,
        Func<bool>? isMarketOpen = null)
    {
        _broker       = broker;
        _sizer        = sizer;
        _guard        = guard;
        _csv          = csv;
        _discord      = discord;
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _riskOptions  = riskOptions.Value;
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
        var alertedPrice    = alert.PricePaid ?? 0m;

        if (!_isMarketOpen())
        {
            _logger.LogDebug("Market closed, skipping order for {Symbol}", alert.Symbol);
            return;
        }

        if (_riskOptions.TradingPaused || IsPaused)
        {
            _logger.LogWarning(
                "Trading is paused — skipping new entry for {Symbol}", alert.Symbol);
            return;
        }

        var order = _sizer.Size(alert, classification, isAverage);
        if (order is null)
        {
            _logger.LogWarning(
                "PositionSizer returned null for {Symbol} — PricePaid: {Price} ActualPrice: {Actual} Risk: {Risk} Type: {Type}",
                alert.Symbol, alert.PricePaid, alert.ActualPriceAtTimeOfAlert, alert.Risk, alert.Type);
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

        // Pre-trade slippage check — compares current market price against alerted price.
        // Skipped if MaxEntrySlippagePct is 0 (disabled) or market data returns 0.
        if (_riskOptions.MaxEntrySlippagePct > 0)
        {
            var currentPrice = await _broker.GetCurrentMarketPriceAsync(
                order.Symbol,
                order.TradeType,
                order.Direction,
                order.Strike,
                order.Expiration,
                ct);

            if (currentPrice <= 0)
            {
                if (_riskOptions.SkipTradeOnSlippageTimeout)
                {
                    _logger.LogWarning(
                        "Slippage check timed out for {Symbol} — skipping trade (SkipTradeOnSlippageTimeout=true)",
                        alert.Symbol);
                    return;
                }

                _logger.LogWarning(
                    "Slippage check timed out for {Symbol} — proceeding without price check",
                    alert.Symbol);
            }
            else if (alertedPrice <= 0)
            {
                _logger.LogWarning(
                    "Slippage check skipped for {Symbol} — alerted price is 0 (PricePaid missing from alert)",
                    alert.Symbol);
                return;
            }
            else
            {
                var slippage = Math.Abs(currentPrice - alertedPrice)
                    / alertedPrice * 100;

                if (slippage > _riskOptions.MaxEntrySlippagePct)
                {
                    _logger.LogWarning(
                        "Slippage check failed for {Symbol} — alerted ${Alerted:F2} " +
                        "current ${Current:F2} slippage {Slippage:F1}% exceeds max {Max:F1}%",
                        alert.Symbol, alertedPrice,
                        currentPrice, slippage, _riskOptions.MaxEntrySlippagePct);
                    return;
                }

                _logger.LogDebug(
                    "Slippage check passed for {Symbol} — {Slippage:F1}% within {Max:F1}% limit",
                    alert.Symbol, slippage, _riskOptions.MaxEntrySlippagePct);
            }
        }

        var orderSubmittedAt = DateTimeOffset.UtcNow;

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

        _guard.RegisterOpen(order, result);
        var trade = _guard.FindOpenTrade(
            order.UserName, order.OptionsContractSymbol, order.Symbol)!;

        // Populate entry execution quality metrics for CSV logging
        trade.LatencyMs   = (int)(result.FilledAt - alertReceivedAt).TotalMilliseconds;
        trade.SlippagePct = alertedPrice > 0
            ? (result.FillPrice - alertedPrice) / alertedPrice * 100
            : null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.SaveAsync(new OpenPosition
            {
                OrderId         = trade.OrderId,
                StopOrderId     = trade.StopOrderId,
                TargetOrderId   = trade.TargetOrderId,
                AlertId         = trade.AlertId,
                UserName        = trade.UserName,
                Symbol          = trade.Symbol,
                TradeType       = trade.TradeType.ToString(),
                OptionsContract = trade.OptionsContract,
                Direction       = trade.Direction,
                Strike          = trade.Strike,
                Expiration      = trade.Expiration,
                Quantity        = trade.Quantity,
                EntryPrice      = trade.EntryPrice,
                EntryAmount     = trade.EntryAmount,
                StopPrice       = trade.StopPrice,
                TargetPrice     = trade.TargetPrice,
                OpenedAt        = trade.OpenedAt,
                IsAverage       = trade.IsAverage,
                HasAveraged     = trade.HasAveraged,
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

        // Fetch balance and exposure after the fill — used for analytics only, not blocking execution
        var accountBalance     = await _broker.GetAccountBalanceAsync(ct);
        var openPositionsValue = await _broker.GetOpenPositionsValueAsync(ct);

        var slippagePct = alertedPrice > 0
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
        var alertReceivedAt = DateTimeOffset.UtcNow;

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

        var alertedExitPrice = alert.PriceAtExit ?? alert.ActualPriceAtTimeOfExit ?? 0m;
        var exitLatencyMs    = (int)(closeResult.FilledAt - alertReceivedAt).TotalMilliseconds;
        var exitSlippagePct  = alertedExitPrice > 0
            ? (closeResult.FillPrice - alertedExitPrice) / alertedExitPrice * 100
            : (decimal?)null;

        var closedTrade = _guard.RegisterClose(
            alert.UserName ?? "",
            alert.OptionsContractSymbol,
            alert.Symbol ?? "",
            closeResult.FillPrice,
            TradeOutcome.XtradesExit);

        if (closedTrade is null) return;

        // Populate exit execution quality metrics for CSV logging
        closedTrade.ExitLatencyMs   = exitLatencyMs;
        closedTrade.ExitSlippagePct = exitSlippagePct;

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.DeleteAsync(trade.OrderId, ct);
        }

        await _csv.CloseTradeAsync(closedTrade, ct);
        _guard.LogExposureUpdate();

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
                orderId:         trade.OrderId,
                exitPrice:       closeResult.FillPrice,
                exitAmount:      closeResult.FillAmount,
                pnl:             closedTrade.PnL.Value,
                pnlPct:          closedTrade.PnLPercent.Value,
                outcome:         closedTrade.Result.ToString(),
                closedAt:        closedTrade.ClosedAt ?? DateTimeOffset.UtcNow,
                exitLatencyMs:   exitLatencyMs,
                exitSlippagePct: exitSlippagePct,
                ct:              ct);
        }
    }

    /// <summary>
    /// Force-closes an open position regardless of any exit alert.
    /// Used by MarketSchedulerService to close same-day expiry options before liquidity dries up.
    /// Writes to CSV, Discord, and trade_metrics identically to a normal exit.
    /// </summary>
    public async Task ForceCloseAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Force closing {Symbol} — Outcome: {Outcome}", trade.Symbol, outcome);

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker ClosePositionAsync failed during force close for {Symbol}", trade.Symbol);
            return;
        }

        var closedTrade = _guard.RegisterClose(
            trade.UserName,
            trade.OptionsContract,
            trade.Symbol,
            closeResult.FillPrice,
            outcome);

        if (closedTrade is null) return;

        closedTrade.ExitLatencyMs   = null;
        closedTrade.ExitSlippagePct = null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.DeleteAsync(trade.OrderId, ct);
        }

        await _csv.CloseTradeAsync(closedTrade, ct);
        _guard.LogExposureUpdate();

        _logger.LogInformation(
            "FORCE CLOSED — {Symbol} × {Qty} @ ${Price:F2} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
            closedTrade.Symbol, closedTrade.Quantity, closeResult.FillPrice,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0, outcome);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);

        if (closedTrade.PnL.HasValue && closedTrade.PnLPercent.HasValue)
        {
            using var metricScope = _scopeFactory.CreateScope();
            var metrics = metricScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
            await metrics.CloseAsync(
                orderId:         trade.OrderId,
                exitPrice:       closeResult.FillPrice,
                exitAmount:      closeResult.FillAmount,
                pnl:             closedTrade.PnL.Value,
                pnlPct:          closedTrade.PnLPercent.Value,
                outcome:         outcome.ToString(),
                closedAt:        closedTrade.ClosedAt ?? DateTimeOffset.UtcNow,
                exitLatencyMs:   null,
                exitSlippagePct: null,
                ct:              ct);
        }
    }

    /// <summary>
    /// Partially closes a 1DTE options position at 3pm ET when profit exceeds the threshold.
    /// Sells a portion of the position, cancels the trail stop, and lets the remainder
    /// ride overnight as a lotto play with no stop protection.
    /// </summary>
    public async Task PartialCloseAndRemoveStopAsync(
        TradeRecord trade,
        int quantityToClose,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Partial close — {Symbol} selling {QtyToClose} of {TotalQty} contracts, removing stop",
            trade.Symbol, quantityToClose, trade.Quantity);

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.PartialCloseAsync(trade, quantityToClose, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Broker PartialCloseAsync failed for {Symbol}", trade.Symbol);
            return;
        }

        if (closeResult.Status != OrderStatus.Filled)
        {
            _logger.LogWarning(
                "Partial close did not fill for {Symbol} — stop not cancelled", trade.Symbol);
            return;
        }

        // Cancel the trail stop — remaining position is a pure lotto overnight hold
        if (trade.StopOrderId is not null && int.TryParse(trade.StopOrderId, out var stopId))
        {
            await _broker.CancelOrderAsync(stopId, ct);
            _logger.LogInformation(
                "Trail stop cancelled for {Symbol} — remaining {RemainingQty} contracts riding overnight",
                trade.Symbol, trade.Quantity - quantityToClose);
        }

        // Update TradeGuard with new quantity and cleared stop
        var remainingQty = trade.Quantity - quantityToClose;
        _guard.UpdateAfterPartialClose(
            trade.UserName,
            trade.OptionsContract,
            trade.Symbol,
            remainingQty);

        // Update the database open position quantity
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.UpdateQuantityAsync(trade.OrderId, remainingQty, ct);
        }

        var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;
        var partialPnl = (closeResult.FillPrice - trade.EntryPrice) * quantityToClose * multiplier;
        var pnlSign    = partialPnl >= 0 ? "+" : "";

        _logger.LogInformation(
            "PARTIAL CLOSE — {Symbol} × {QtyClose} @ ${Price:F2} | " +
            "Partial P&L: {PnL:+$#,##0.00;-$#,##0.00} | Remaining: {RemainingQty} contracts (lotto, no stop)",
            trade.Symbol, quantityToClose, closeResult.FillPrice, partialPnl, remainingQty);

        await _discord.NotifyPartialCloseAsync(trade, quantityToClose, closeResult.FillPrice, partialPnl, remainingQty, ct);
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