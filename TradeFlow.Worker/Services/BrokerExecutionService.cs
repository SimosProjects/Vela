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
    public bool IsPaused { get; set; } = false;

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

        // Stock entries use a tighter staleness threshold, the intent is to enter near
        // the alerted price or not at all. Options tolerate a wider window since they
        // move faster and the limit order provides the real price protection.
        var isStockEntry = classification.Category == AlertCategory.StockEntry;
        var maxStaleness = isStockEntry && _riskOptions.StockAlertStalenessMaxSlippagePct > 0
            ? _riskOptions.StockAlertStalenessMaxSlippagePct
            : _riskOptions.AlertStalenessMaxSlippagePct;

        if (maxStaleness > 0 && alertedPrice > 0)
        {
            var priceAtAlertTime = alert.ActualPriceAtTimeOfAlert ?? 0m;
            if (priceAtAlertTime > 0)
            {
                var staleness = (alertedPrice - priceAtAlertTime) / priceAtAlertTime * 100;
                if (staleness > maxStaleness)
                {
                    _logger.LogWarning(
                        "Alert staleness check failed for {Symbol} — PricePaid ${Paid:F2} " +
                        "vs alert price ${AlertPrice:F2} ({Staleness:F1}%) exceeds {TradeType} max {Max:F1}%",
                        alert.Symbol, alertedPrice, priceAtAlertTime,
                        staleness, isStockEntry ? "stock" : "options", maxStaleness);
                    return;
                }

                _logger.LogDebug(
                    "Alert staleness check passed for {Symbol} — {Staleness:F1}% within {TradeType} limit {Max:F1}%",
                    alert.Symbol, staleness, isStockEntry ? "stock" : "options", maxStaleness);
            }
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
            if (blocked.IsRoutine)
                _logger.LogDebug(
                    "TradeGuard: {Reason}", blocked.Reason);
            else
                _logger.LogWarning(
                    "TradeGuard blocked order for {Symbol}: {Reason}",
                    alert.Symbol, blocked.Reason);
            return;
        }

        // CheckAsync reserved a slot for this symbol. Every exit path before RegisterOpen
        // must release it so the cap remains accurate for subsequent alerts.
        var reservationActive = true;
        try
        {
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
                // IBKR price-protection rejection on a stock limit order — retry once with a
                // limit anchored to the market price IBKR reported in the rejection message.
                // No extra roundtrip: the market price is extracted from the rejection itself.
                if (result.Status == OrderStatus.Cancelled &&
                    order.LimitPrice.HasValue &&
                    TryParsePriceProtectionMarketPrice(result.RejectionReason, out var marketPrice))
                {
                    result = await RetryWithMarketAnchoredLimitAsync(
                        order, result, alertedPrice, marketPrice, ct);

                    if (result.Status == OrderStatus.Rejected || result.Status == OrderStatus.Cancelled)
                    {
                        _logger.LogWarning(
                            "Broker rejected retry order for {Symbol} — {Reason}",
                            alert.Symbol, result.RejectionReason ?? result.Status.ToString());
                        return;
                    }
                    // Fall through to normal fill recording
                }
                else
                {
                    _logger.LogWarning(
                        "Broker rejected order for {Symbol} — {Reason}",
                        alert.Symbol, result.RejectionReason ?? result.Status.ToString());
                    return;
                }
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
                    XScore          = (decimal)(alert.XScore ?? 0),
                    DiscordRank     = alert.DiscordRank,
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

                var (positionPrice, positionQty) = await _broker.GetCurrentPositionPriceAsync(verifyRecord, ct);
 
                if (positionPrice <= 0 || positionQty <= 0)
                {
                    // First check returned nothing, IBKR may not have propagated the fill yet.
                    // Retry once after a short delay before concluding no fill.
                    _logger.LogWarning(
                        "Position not confirmed for {Symbol} on first check — waiting 5s and retrying.",
                        alert.Symbol);
 
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    (positionPrice, positionQty) = await _broker.GetCurrentPositionPriceAsync(verifyRecord, ct);
                }
 
                if (positionPrice <= 0 || positionQty <= 0)
                {
                    _logger.LogWarning(
                        "Position not confirmed in Gateway for {Symbol} after timeout — " +
                        "order not recorded to prevent ghost position.",
                        alert.Symbol);
                    return;
                }

                if (positionQty != order.Quantity)
                {
                    _logger.LogWarning(
                        "Gateway qty mismatch for {Symbol} — ordered {Ordered} but IBKR holds {Actual}. " +
                        "Recording actual qty to prevent ghost short on close.",
                        alert.Symbol, order.Quantity, positionQty);
                }
                else
                {
                    _logger.LogInformation(
                        "Gateway confirmed position for {Symbol} — qty {Qty} @ ${Price:F2}.",
                        alert.Symbol, positionQty, positionPrice);
                }

                var pendingMultiplier = order.TradeType == TradeType.Options ? 100m : 1m;
                result = result with
                {
                    FillPrice    = positionPrice,
                    FillQuantity = positionQty,
                    FillAmount   = positionPrice * positionQty * pendingMultiplier,
                    Status       = OrderStatus.Filled,
                };
            }

            // Tighten trail if post-fill slippage exceeds the configured warning threshold.
            // Must run before RegisterOpen so the updated StopOrderId is stored in TradeGuard and DB.
            result = await TightenTrailOnElevatedSlippageAsync(order, result, alertedPrice, ct);

            _guard.RegisterOpen(order, result);
            reservationActive = false;

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

                if (isAverage)
                {
                    // Find the existing position and update it rather than inserting a second row.
                    // Recalculates weighted average entry price and combined quantity to match IBKR,
                    // which merges repeated buys of the same symbol into one position.
                    var existing = await repo.GetBySymbolAndUserAsync(trade.Symbol, trade.UserName, ct);

                    if (existing is not null)
                    {
                        var combinedQty        = existing.Quantity + trade.Quantity;
                        var weightedEntryPrice = (existing.EntryPrice * existing.Quantity +
                                                 trade.EntryPrice * trade.Quantity) / combinedQty;
                        var combinedAmount     = existing.EntryAmount + trade.EntryAmount;

                        await repo.UpdateAverageAsync(
                            existing.OrderId,
                            combinedQty,
                            weightedEntryPrice,
                            combinedAmount,
                            trade.StopOrderId,
                            ct);

                        _logger.LogInformation(
                            "Average entry merged — {Symbol} qty {OldQty}+{NewQty}={CombinedQty} " +
                            "avg price ${AvgPrice:F2}",
                            trade.Symbol, existing.Quantity, trade.Quantity,
                            combinedQty, weightedEntryPrice);
                    }
                    else
                    {
                        // No existing row found — fall back to insert
                        _logger.LogWarning(
                            "Average entry for {Symbol} — no existing position found, inserting as new.",
                            trade.Symbol);
                        await repo.SaveAsync(BuildOpenPosition(trade), ct);
                    }
                }
                else
                {
                    await repo.SaveAsync(BuildOpenPosition(trade), ct);
                }
            }

            await _csv.OpenTradeAsync(trade, ct);

            _logger.LogInformation(
                "ORDER PLACED — {Type} {Symbol} {Direction} × {Qty} @ ${Price:F2} | " +
                "Stop: ${Stop:F2} | Target: ${Target:F2} | OrderId: {OrderId}",
                order.TradeType, order.Symbol, order.Direction ?? "—",
                result.FillQuantity, result.FillPrice,
                order.StopPrice, order.TargetPrice, result.OrderId);

            await _discord.NotifyOrderPlacedAsync(trade, ct);

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
                XScore                    = (decimal?)alert.XScore,
                DiscordRank               = alert.DiscordRank,
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
        finally
        {
            if (reservationActive)
                _guard.ReleaseReservation(order);
        }
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

        var remainingQty = trade.Quantity - quantityToClose;
        _guard.UpdateAfterPartialClose(
            trade.UserName,
            trade.OptionsContract,
            trade.Symbol,
            remainingQty);

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.UpdateQuantityAsync(trade.OrderId, remainingQty, ct);
        }

        var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;
        var partialPnl = (closeResult.FillPrice - trade.EntryPrice) * quantityToClose * multiplier;

        _logger.LogInformation(
            "PARTIAL CLOSE — {Symbol} × {QtyClose} @ ${Price:F2} | " +
            "Partial P&L: {PnL:+$#,##0.00;-$#,##0.00} | Remaining: {RemainingQty} contracts (lotto, no stop)",
            trade.Symbol, quantityToClose, closeResult.FillPrice, partialPnl, remainingQty);

        await _discord.NotifyPartialCloseAsync(trade, quantityToClose, closeResult.FillPrice, partialPnl, remainingQty, ct);
    }

    // -- Helpers --

    // Parses the market price stored by IbkrEWrapper when IBKR fires a price-protection [202]
    // rejection. Format: "PRICE_PROTECTION:267.2"
    private static bool TryParsePriceProtectionMarketPrice(string? reason, out decimal marketPrice)
    {
        marketPrice = 0m;
        const string prefix = "PRICE_PROTECTION:";
        if (reason is null || !reason.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return decimal.TryParse(reason[prefix.Length..], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out marketPrice) && marketPrice > 0;
    }

    // Recalculates the stock limit relative to the market price IBKR reported in its rejection
    // and retries PlaceOrderAsync once. The new limit is capped at the original alert-price
    // ceiling so we still reject if the stock has moved too far from the alerted level.
    private async Task<BrokerOrderResult> RetryWithMarketAnchoredLimitAsync(
        TradeOrder order,
        BrokerOrderResult originalResult,
        decimal alertedPrice,
        decimal marketPrice,
        CancellationToken ct)
    {
        // Back-calculate effective slippage pct from the original limit so the retry works
        // correctly for all risk tiers (stock, options standard/high/lotto) without needing
        // to know which tier's config value to apply.
        var effectiveSlippagePct = alertedPrice > 0
            ? (order.LimitPrice!.Value - alertedPrice) / alertedPrice * 100m
            : 0m;
 
        var adjustedLimit = Math.Round(
            marketPrice * (1m + effectiveSlippagePct / 100m), 2);
 
        // The original limit IS the alert ceiling — it was set as alertPrice × (1 + slippage%).
        // If the market moved up, adjustedLimit > original limit, meaning price ran away.
        // Only retry when the market dropped and we can get a tighter, acceptable fill.
        var alertCeiling = order.LimitPrice!.Value;
 
        if (adjustedLimit > alertCeiling)
        {
            _logger.LogWarning(
                "Price-protection retry skipped for {Symbol} — adjusted limit ${AdjLimit:F2} " +
                "exceeds alert ceiling ${Ceiling:F2}, price moved too far from alert.",
                order.Symbol, adjustedLimit, alertCeiling);
            return originalResult;
        }

        _logger.LogInformation(
            "Retrying {Symbol} with market-anchored limit ${NewLimit:F2} " +
            "(was ${OldLimit:F2}, IBKR market ${Market:F2})",
            order.Symbol, adjustedLimit, order.LimitPrice!.Value, marketPrice);

        try
        {
            return await _broker.PlaceOrderAsync(order with { LimitPrice = adjustedLimit }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Price-protection retry failed for {Symbol}", order.Symbol);
            return originalResult;
        }
    }

    private async Task<BrokerOrderResult> TightenTrailOnElevatedSlippageAsync(
        TradeOrder order,
        BrokerOrderResult result,
        decimal alertedPrice,
        CancellationToken ct)
    {
        if (_riskOptions.PostFillSlippageWarningPct <= 0
            || _riskOptions.HighSlippageTrailPct <= 0
            || alertedPrice <= 0
            || result.StopOrderId is null)
            return result;

        var slippagePct = (result.FillPrice - alertedPrice) / alertedPrice * 100;

        if (slippagePct <= (decimal)_riskOptions.PostFillSlippageWarningPct)
            return result;

        // Take the tighter of the configured high-slippage trail and the current trail.
        // HighSlippageTrailPct targets options (e.g. 25%) but a stock position may already
        // carry a trail of 10-20%. Replacing with 25% would loosen the stop rather than
        // tighten it, so we cap at whichever percentage is already more protective.
        var newTrailPct = Math.Min(order.TrailPercent, _riskOptions.HighSlippageTrailPct);

        if (newTrailPct >= order.TrailPercent)
        {
            _logger.LogDebug(
                "Elevated slippage for {Symbol} — current trail {Trail}% is already tighter " +
                "than high-slippage trail {HighTrail}%, no replacement needed.",
                order.Symbol, order.TrailPercent, _riskOptions.HighSlippageTrailPct);
            return result;
        }

        _logger.LogWarning(
            "Elevated post-fill slippage for {Symbol} — {Slippage:F1}% above alert price. " +
            "Tightening trail from {OldTrail}% to {NewTrail}%.",
            order.Symbol, slippagePct, order.TrailPercent, newTrailPct);

        var newStopId = await _broker.ReplaceTrailStopAsync(
            result.StopOrderId,
            result.FillQuantity,
            order,
            newTrailPct,
            ct);

        if (newStopId is null)
        {
            _logger.LogWarning(
                "Trail stop replacement failed for {Symbol} — original trail remains active.",
                order.Symbol);
            return result;
        }

        return result with { StopOrderId = newStopId };
    }

    private static OpenPosition BuildOpenPosition(TradeRecord trade) => new()
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
    };

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