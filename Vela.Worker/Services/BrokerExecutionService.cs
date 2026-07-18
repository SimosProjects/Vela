using Vela.Worker.Engine;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

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
    /// Delegates to <see cref="PassesPreEntryChecks"/>, <see cref="ExecuteBrokerEntryAsync"/>,
    /// <see cref="TightenTrailOnElevatedSlippageAsync"/>, <see cref="AdjustOrderPricesForFill"/>,
    /// and <see cref="PersistEntryAsync"/>.
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

        if (!PassesPreEntryChecks(alert, classification, alertedPrice))
            return;

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
                _logger.LogDebug("TradeGuard: {Reason}", blocked.Reason);
            else
                _logger.LogWarning("TradeGuard blocked order for {Symbol}: {Reason}", alert.Symbol, blocked.Reason);
            return;
        }

        // CheckAsync reserved a slot for this symbol. Every exit path before RegisterOpen
        // must release it so the cap remains accurate for subsequent alerts.
        var reservationActive = true;
        try
        {
            var orderSubmittedAt = DateTimeOffset.UtcNow;
            var result = await ExecuteBrokerEntryAsync(alert, order, alertedPrice, ct);
            if (result is null) return;

            // Must run before RegisterOpen so the updated StopOrderId is stored in TradeGuard and DB.
            result = await TightenTrailOnElevatedSlippageAsync(order, result, alertedPrice, ct);

            // When slippage is elevated the order's stop and target prices were derived from the
            // alert price, not the fill price. Rebase them so the persisted record reflects the
            // actual position risk rather than showing a near-zero target or a massive stop gap.
            order = AdjustOrderPricesForFill(order, result, alertedPrice);

            // Both OCA and standalone trail attempts were rejected at IBKR (e.g. SPX cash-settled
            // margin rules). The fill happened and will be tracked, but the position has no stop.
            if (result.StopOrderId is null)
            {
                _logger.LogError(
                    "No stop protection for {Symbol} OrderId {OrderId} — all trail stop attempts " +
                    "rejected by IBKR. Position is open and unprotected. Manual stop required.",
                    order.Symbol, result.OrderId);
                await _discord.NotifyCriticalAsync(
                    $"⚠️ No Stop Protection — {order.Symbol}",
                    $"**{order.Symbol}** filled at ${result.FillPrice:F2} × {result.FillQuantity} " +
                    "but all trail stop attempts were rejected by IBKR.\n" +
                    "Position is open without stop protection.\n" +
                    "Place a manual stop in IBKR immediately.",
                    ct);
            }

            _guard.RegisterOpen(order, result);
            reservationActive = false;

            var trade = _guard.FindOpenTrade(order.UserName, order.OptionsContractSymbol, order.Symbol)!;
            await PersistEntryAsync(alert, order, trade, result, alertedPrice, alertReceivedAt, orderSubmittedAt, isAverage, ct);
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
    /// Returns true if a matching open position was found and processing proceeded,
    /// false if it exited early for any reason.
    /// </summary>
    public async Task<bool> HandleExitAsync(
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
            return false;
        }

        // Single-closer election: mark the position as closing before touching the broker.
        // If another concurrent path (polling + SignalR race) already marked it, abort here
        // to prevent both submitting market sells and creating a ghost short on the second fill.
        if (!_guard.TryMarkClosing(alert.UserName ?? "", alert.OptionsContractSymbol, alert.Symbol ?? ""))
        {
            _logger.LogInformation(
                "Exit for {Symbol} already in progress on a concurrent path — skipping duplicate.",
                alert.Symbol);
            return false;
        }

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, TradeOutcome.XtradesExit, ct);
        }
        catch (Exception ex)
        {
            _guard.RevertClosing(alert.UserName ?? "", alert.OptionsContractSymbol, alert.Symbol ?? "");
            _logger.LogError(ex,
                "Broker ClosePositionAsync failed for {Symbol}, skipping", alert.Symbol);
            return false;
        }

        // Both fill and execDetails callbacks timed out. Verify whether the close actually
        // executed at IBKR before deciding to leave the position open for reconciliation.
        if (closeResult.Status == OrderStatus.Pending)
        {
            closeResult = await VerifyCloseExecutedAsync(trade, closeResult, ct);

            if (closeResult.Status == OrderStatus.Pending)
            {
                _guard.RevertClosing(alert.UserName ?? "", alert.OptionsContractSymbol, alert.Symbol ?? "");
                _logger.LogWarning(
                    "Close order timed out for {Symbol} — position may still be open at IBKR. " +
                    "Not recording close to prevent data loss. Manual reconciliation required.",
                    alert.Symbol);
                return false;
            }

            // Position confirmed absent via reqPositions — close executed despite callback failure.
            await _discord.NotifyCriticalAsync(
                $"⚠️ Close Verified via reqPositions — {trade.Symbol}",
                $"Close for **{trade.Symbol}** timed out on both callbacks but position was " +
                $"confirmed absent from IBKR. Recorded at entry price ${trade.EntryPrice:F2} " +
                "as fill price fallback. Actual P&L requires manual verification.",
                ct);
        }

        // Confirmed only a partial fill within the bounded wait — must not be recorded as a
        // full close (the 2026-07-17 UBER incident). Correct quantity to the confirmed
        // remainder and leave trade_metrics/open_positions untouched; a later exit alert or
        // reconciliation can act on what's left.
        if (closeResult.Status == OrderStatus.PartialFill)
        {
            var remainingQty = trade.Quantity - closeResult.FillQuantity;

            _guard.UpdatePositionQuantity(trade.OrderId, remainingQty);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.UpdateQuantityAsync(trade.OrderId, remainingQty, ct);
            }

            _logger.LogError(
                "PARTIAL CLOSE ONLY — {Symbol} confirmed {SoldQty} of {TotalQty} sold @ ${Price:F2}. " +
                "{RemainingQty} contracts remain open at IBKR and UNPROTECTED — the original stop " +
                "was cancelled before this close attempt. Manual stop placement required immediately.",
                trade.Symbol, closeResult.FillQuantity, trade.Quantity, closeResult.FillPrice, remainingQty);

            await _discord.NotifyCriticalAsync(
                $"🚨 Partial Close Only — {trade.Symbol} UNPROTECTED",
                $"Close order for **{trade.Symbol}** only filled {closeResult.FillQuantity} of " +
                $"{trade.Quantity} contracts @ ${closeResult.FillPrice:F2} within the fill-confirmation " +
                $"window. {remainingQty} contracts remain open at IBKR with NO stop protection — " +
                "the original stop was cancelled before this close attempt. " +
                "Manual stop placement required immediately.\n" +
                $"Position quantity has been corrected to {remainingQty} in Vela — " +
                "trade_metrics was NOT closed.",
                ct);

            _guard.RevertClosing(alert.UserName ?? "", alert.OptionsContractSymbol, alert.Symbol ?? "");
            return false;
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

        if (closedTrade is null) return false;

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

        return true;
    }

    /// <summary>
    /// Force-closes an open position regardless of any exit alert.
    /// Used by MarketSchedulerService to close same-day expiry options before liquidity dries up,
    /// and by ForceCloseConsumerService for dashboard-initiated closes.
    /// Writes to CSV, Discord, and trade_metrics identically to a normal exit.
    /// Returns the outcome so a caller can distinguish a clean close from one needing reconciliation.
    /// </summary>
    public async Task<ForceCloseOutcome> ForceCloseAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Force closing {Symbol} — Outcome: {Outcome}", trade.Symbol, outcome);

        // Single-closer election: mark before broker call to prevent a concurrent exit alert
        // from also closing this position while the force-close market sell is in flight.
        if (!_guard.TryMarkClosing(trade.UserName, trade.OptionsContract, trade.Symbol))
        {
            _logger.LogInformation(
                "Force close for {Symbol} skipped — already being closed on a concurrent path.",
                trade.Symbol);
            return ForceCloseOutcome.AlreadyClosing;
        }

        BrokerOrderResult closeResult;
        try
        {
            closeResult = await _broker.ClosePositionAsync(trade, outcome, ct);
        }
        catch (Exception ex)
        {
            _guard.RevertClosing(trade.UserName, trade.OptionsContract, trade.Symbol);
            _logger.LogError(ex,
                "Broker ClosePositionAsync failed during force close for {Symbol}", trade.Symbol);
            return ForceCloseOutcome.Failed;
        }

        // Both fill and execDetails callbacks timed out. Verify whether the close actually
        // executed at IBKR before deciding to leave the position open for reconciliation.
        if (closeResult.Status == OrderStatus.Pending)
        {
            closeResult = await VerifyCloseExecutedAsync(trade, closeResult, ct);

            if (closeResult.Status == OrderStatus.Pending)
            {
                _guard.RevertClosing(trade.UserName, trade.OptionsContract, trade.Symbol);
                _logger.LogWarning(
                    "Force close timed out for {Symbol} — position may still be open at IBKR. " +
                    "Not recording close to prevent data loss. Manual reconciliation required.",
                    trade.Symbol);
                return ForceCloseOutcome.Pending;
            }

            // Position confirmed absent via reqPositions — close executed despite callback failure.
            await _discord.NotifyCriticalAsync(
                $"⚠️ Force Close Verified via reqPositions — {trade.Symbol}",
                $"Force close for **{trade.Symbol}** timed out on both callbacks but position was " +
                $"confirmed absent from IBKR. Recorded at entry price ${trade.EntryPrice:F2} " +
                "as fill price fallback. Actual P&L requires manual verification.",
                ct);
        }

        // Confirmed only a partial fill within the bounded wait — must not be recorded as a
        // full close (the 2026-07-17 UBER incident). Correct quantity to the confirmed
        // remainder; leave trade_metrics/open_positions untouched. Discord alert is left to the
        // caller (ForceCloseConsumerService), matching the existing Pending/Failed convention.
        if (closeResult.Status == OrderStatus.PartialFill)
        {
            var remainingQty = trade.Quantity - closeResult.FillQuantity;

            _guard.UpdatePositionQuantity(trade.OrderId, remainingQty);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.UpdateQuantityAsync(trade.OrderId, remainingQty, ct);
            }

            _logger.LogError(
                "PARTIAL FORCE CLOSE ONLY — {Symbol} confirmed {SoldQty} of {TotalQty} sold @ " +
                "${Price:F2}. {RemainingQty} contracts remain open at IBKR and UNPROTECTED — the " +
                "original stop was cancelled before this close attempt.",
                trade.Symbol, closeResult.FillQuantity, trade.Quantity, closeResult.FillPrice, remainingQty);

            _guard.RevertClosing(trade.UserName, trade.OptionsContract, trade.Symbol);
            return ForceCloseOutcome.PartialFill;
        }

        var closedTrade = _guard.RegisterClose(
            trade.UserName,
            trade.OptionsContract,
            trade.Symbol,
            closeResult.FillPrice,
            outcome);

        if (closedTrade is null) return ForceCloseOutcome.NotFound;

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

        return ForceCloseOutcome.Closed;
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

        // Confirmed less than the intended quantityToClose within the bounded wait — must not
        // be recorded as if the full intended amount sold (the 2026-07-17 UBER incident).
        // PartialCloseAsync never touches the original stop before placing its sell order, so
        // — unlike a full ClosePositionAsync attempt — the remainder here is still protected by
        // the existing stop; do not cancel it.
        if (closeResult.Status == OrderStatus.PartialFill)
        {
            var confirmedRemainingQty = trade.Quantity - closeResult.FillQuantity;

            _guard.UpdatePositionQuantity(trade.OrderId, confirmedRemainingQty);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.UpdateQuantityAsync(trade.OrderId, confirmedRemainingQty, ct);
            }

            _logger.LogError(
                "PARTIAL CLOSE (intentional 3pm reduction) ONLY PARTIALLY FILLED — {Symbol} confirmed " +
                "{SoldQty} of {Requested} requested sold @ ${Price:F2}. Existing stop was not touched " +
                "and continues protecting the remaining {RemainingQty} contracts.",
                trade.Symbol, closeResult.FillQuantity, quantityToClose, closeResult.FillPrice, confirmedRemainingQty);

            await _discord.NotifyCriticalAsync(
                $"⚠️ Partial Close Incomplete — {trade.Symbol}",
                $"3pm partial-close for **{trade.Symbol}** only sold {closeResult.FillQuantity} of the " +
                $"{quantityToClose} contracts requested @ ${closeResult.FillPrice:F2}. Position quantity " +
                $"corrected to {confirmedRemainingQty} in Vela — trade_metrics was NOT closed. " +
                "The existing stop was not modified and continues protecting the remainder.",
                ct);
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

    // Runs market-hours, pause-state, and alert-staleness checks before any broker interaction.
    // Stock entries use a tighter staleness threshold, the intent is to enter near the alerted
    // price or not at all. Options tolerate a wider window since the limit order provides the
    // real price protection.
    private bool PassesPreEntryChecks(Alert alert, AlertClassification classification, decimal alertedPrice)
    {
        if (!_isMarketOpen())
        {
            _logger.LogDebug("Market closed, skipping order for {Symbol}", alert.Symbol);
            return false;
        }

        if (_riskOptions.TradingPaused || IsPaused)
        {
            _logger.LogWarning("Trading is paused — skipping new entry for {Symbol}", alert.Symbol);
            return false;
        }

        var isStockEntry = classification.Category == AlertCategory.StockEntry;

        // Select the most specific staleness threshold for the trade type.
        // Stock uses StockAlertStalenessMaxSlippagePct; options uses OptionsAlertStalenessMaxSlippagePct.
        // Both fall back to the general AlertStalenessMaxSlippagePct when their type-specific value is 0.
        decimal maxStaleness;
        if (isStockEntry && _riskOptions.StockAlertStalenessMaxSlippagePct > 0)
            maxStaleness = _riskOptions.StockAlertStalenessMaxSlippagePct;
        else if (!isStockEntry && _riskOptions.OptionsAlertStalenessMaxSlippagePct > 0)
            maxStaleness = _riskOptions.OptionsAlertStalenessMaxSlippagePct;
        else
            maxStaleness = _riskOptions.AlertStalenessMaxSlippagePct;

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
                    return false;
                }

                _logger.LogDebug(
                    "Alert staleness check passed for {Symbol} — {Staleness:F1}% within {TradeType} limit {Max:F1}%",
                    alert.Symbol, staleness, isStockEntry ? "stock" : "options", maxStaleness);
            }
            else
            {
                // ActualPriceAtTimeOfAlert was not provided by Xtrades for this alert.
                // The staleness check is skipped, the limit order ceiling provides entry protection.
                _logger.LogDebug(
                    "Staleness check skipped for {Symbol} — ActualPriceAtTimeOfAlert not provided by Xtrades.",
                    alert.Symbol);
            }
        }

        return true;
    }

    // Places the entry order and handles all broker-side outcomes: exceptions, Rejected/Cancelled
    // responses (including the price-protection retry path), and Pending (limit timeout).
    // Returns null on any non-recoverable failure so HandleEntryAsync can exit cleanly
    // while the finally block releases the TradeGuard reservation.
    private async Task<BrokerOrderResult?> ExecuteBrokerEntryAsync(
        Alert alert,
        TradeOrder order,
        decimal alertedPrice,
        CancellationToken ct)
    {
        BrokerOrderResult result;
        try
        {
            result = await _broker.PlaceOrderAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broker PlaceOrderAsync failed for {Symbol}, skipping", alert.Symbol);
            return null;
        }

        if (result.Status == OrderStatus.Rejected || result.Status == OrderStatus.Cancelled)
        {
            // IBKR price-protection rejection on a stock limit order, retry once with a
            // limit anchored to the market price IBKR reported in the rejection message.
            // No extra roundtrip: the market price is extracted from the rejection itself.
            if (result.Status == OrderStatus.Cancelled &&
                order.LimitPrice.HasValue &&
                TryParsePriceProtectionMarketPrice(result.RejectionReason, out var marketPrice))
            {
                result = await RetryWithMarketAnchoredLimitAsync(order, result, alertedPrice, marketPrice, ct);
                if (result.Status == OrderStatus.Rejected || result.Status == OrderStatus.Cancelled)
                {
                    _logger.LogWarning(
                        "Broker rejected retry order for {Symbol} — {Reason}",
                        alert.Symbol, result.RejectionReason ?? result.Status.ToString());
                    return null;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Broker rejected order for {Symbol} — {Reason}",
                    alert.Symbol, result.RejectionReason ?? result.Status.ToString());
                return null;
            }
        }

        if (result.Status == OrderStatus.Pending)
        {
            _logger.LogDebug(
                "Order timed out for {Symbol} — verifying position exists in Gateway before recording.",
                alert.Symbol);
            result = await VerifyPendingFillAsync(alert, order, result, ct);
        }

        return result;
    }

    // Verifies whether a timed-out limit order actually filled by querying the Gateway position.
    // Retries once after a short delay to allow IBKR propagation. If both checks return nothing,
    // records an estimated fill rather than silently dropping the position, an unrecorded open
    // position at IBKR is more dangerous than a ghost that reconciliation can clean up.
    // StopOrderId stays null from the Pending result, triggering the no-stop Discord critical.
    private async Task<BrokerOrderResult> VerifyPendingFillAsync(
        Alert alert,
        TradeOrder order,
        BrokerOrderResult pending,
        CancellationToken ct)
    {
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
            _logger.LogDebug(
                "Position not confirmed for {Symbol} on first check — waiting 5s and retrying.",
                order.Symbol);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            (positionPrice, positionQty) = await _broker.GetCurrentPositionPriceAsync(verifyRecord, ct);
        }

        if (positionPrice <= 0 || positionQty <= 0)
        {
            _logger.LogError(
                "Position verification timed out twice for {Symbol} — recording with " +
                "estimated fill to prevent untracked IBKR position. " +
                "Verify actual fill in IBKR. OrderId: {OrderId}",
                order.Symbol, pending.OrderId);

            var estimatedMultiplier = order.TradeType == TradeType.Options ? 100m : 1m;
            return pending with
            {
                FillPrice    = order.EstimatedEntryPrice,
                FillQuantity = order.Quantity,
                FillAmount   = order.EstimatedEntryPrice * order.Quantity * estimatedMultiplier,
                Status       = OrderStatus.Filled,
            };
        }

        if (positionQty != order.Quantity)
            _logger.LogWarning(
                "Gateway qty mismatch for {Symbol} — ordered {Ordered} but IBKR holds {Actual}. " +
                "Recording actual qty to prevent ghost short on close.",
                order.Symbol, order.Quantity, positionQty);
        else
            _logger.LogInformation(
                "Gateway confirmed position for {Symbol} — qty {Qty} @ ${Price:F2}.",
                order.Symbol, positionQty, positionPrice);

        var pendingMultiplier = order.TradeType == TradeType.Options ? 100m : 1m;
        return pending with
        {
            FillPrice    = positionPrice,
            FillQuantity = positionQty,
            FillAmount   = positionPrice * positionQty * pendingMultiplier,
            Status       = OrderStatus.Filled,
        };
    }

    // Called when ClosePositionAsync returns Pending (both fill and execDetails callbacks timed out).
    // Uses GetAllPositionsAsync, which distinguishes a confirmed empty list from a Gateway timeout
    // via PositionsSnapshot.TimedOut, to determine whether the close actually executed at IBKR.
    // Returns the original Pending result unchanged when the outcome cannot be confirmed, so callers
    // preserve the position for reconciliation rather than recording an incorrect close.
    private async Task<BrokerOrderResult> VerifyCloseExecutedAsync(
        TradeRecord trade,
        BrokerOrderResult pending,
        CancellationToken ct)
    {
        // Brief pause to allow IBKR to propagate the close before querying positions
        await Task.Delay(TimeSpan.FromSeconds(3));

        using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkCts.CancelAfter(TimeSpan.FromSeconds(15));
        var snapshot = await _broker.GetAllPositionsAsync(checkCts.Token);

        if (snapshot.TimedOut)
        {
            // Gateway did not respond, cannot distinguish a closed position from a still-open one.
            // Return Pending so the caller leaves the position open for the reconciler.
            _logger.LogWarning(
                "Close verification for {Symbol} — reqPositions timed out. " +
                "Cannot confirm close status. Leaving open for reconciliation.",
                trade.Symbol);
            return pending;
        }

        var contractKey = trade.TradeType == TradeType.Options
            ? trade.OptionsContract?.Replace(" ", "") ?? ""
            : trade.Symbol;

        var stillOpen = snapshot.Positions.Any(p =>
            trade.TradeType == TradeType.Options
                ? string.Equals(
                    p.LocalSymbol?.Replace(" ", ""),
                    contractKey,
                    StringComparison.OrdinalIgnoreCase) && p.Quantity > 0
                : string.Equals(p.Symbol, contractKey, StringComparison.OrdinalIgnoreCase)
                  && p.SecType == "STK"
                  && p.Quantity > 0);

        if (stillOpen)
        {
            _logger.LogWarning(
                "Close verification for {Symbol} — position still present in IBKR. " +
                "Close did not execute. Leaving open for reconciliation.",
                trade.Symbol);
            return pending;
        }

        // Position confirmed absent from a complete snapshot, the close executed.
        // Use entry price as fill price since the actual fill price is unknown.
        _logger.LogWarning(
            "Close verification for {Symbol} — position absent from IBKR snapshot. " +
            "Close executed despite callback timeout. Recording with entry price as fill price fallback.",
            trade.Symbol);

        var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;
        return pending with
        {
            FillPrice    = trade.EntryPrice,
            FillQuantity = trade.Quantity,
            FillAmount   = trade.EntryAmount,
            Status       = OrderStatus.Filled,
            FilledAt     = DateTimeOffset.UtcNow,
        };
    }

    // Writes a confirmed fill to open_positions, CSV, Discord, and trade_metrics.
    // On DB write failure, reverts TradeGuard state and fires a Discord critical.
    // Remaining writes (CSV, Discord, metrics) are skipped when the DB write fails.
    private async Task PersistEntryAsync(
        Alert alert,
        TradeOrder order,
        TradeRecord trade,
        BrokerOrderResult result,
        decimal alertedPrice,
        DateTimeOffset alertReceivedAt,
        DateTimeOffset orderSubmittedAt,
        bool isAverage,
        CancellationToken ct)
    {
        trade.LatencyMs = (int)(result.FilledAt - alertReceivedAt).TotalMilliseconds;
        trade.SlippagePct = alertedPrice > 0
            ? (result.FillPrice - alertedPrice) / alertedPrice * 100
            : null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();

            if (isAverage)
            {
                var existing = await repo.GetBySymbolAndUserAsync(trade.Symbol, trade.UserName, ct);
                if (existing is not null)
                {
                    var combinedQty        = existing.Quantity + trade.Quantity;
                    var weightedEntryPrice = (existing.EntryPrice * existing.Quantity +
                                             trade.EntryPrice * trade.Quantity) / combinedQty;
                    var combinedAmount     = existing.EntryAmount + trade.EntryAmount;

                    await repo.UpdateAverageAsync(
                        existing.OrderId, combinedQty, weightedEntryPrice, combinedAmount,
                        trade.StopOrderId, ct);

                    _logger.LogInformation(
                        "Average entry merged — {Symbol} qty {OldQty}+{NewQty}={CombinedQty} " +
                        "avg price ${AvgPrice:F2}",
                        trade.Symbol, existing.Quantity, trade.Quantity, combinedQty, weightedEntryPrice);
                }
                else
                {
                    // No existing row found, fall back to insert
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
        catch (Exception ex)
        {
            if (isAverage)
            {
                // The original position is still valid, only the average failed to persist.
                // Revert HasAveraged so the position can be averaged again on the next alert.
                _guard.RevertAverage(order.UserName, order.OptionsContractSymbol, order.Symbol);
                _logger.LogError(ex,
                    "open_positions write FAILED on average entry for {Symbol}. " +
                    "HasAveraged reverted. The average fill occurred at IBKR but is not tracked.",
                    trade.Symbol);
                await _discord.NotifyCriticalAsync(
                    $"⚠️ Average Entry Write Failed — {trade.Symbol}",
                    $"Average fill for **{trade.Symbol}** succeeded at IBKR " +
                    $"(${result.FillPrice:F2} × {result.FillQuantity}) " +
                    $"but could not be saved to the database. " +
                    $"The original position remains tracked. Error: {ex.Message}",
                    ct);
            }
            else
            {
                // Position is in TradeGuard but not DB. Remove it now, if we don't, the next
                // restart will reload from DB (empty), TradeGuard will have no record of it,
                // and the position will be open at IBKR without any stop or exit handling.
                _guard.RemovePosition(trade.OrderId);
                _logger.LogError(ex,
                    "CRITICAL: open_positions write FAILED for {Symbol} OrderId {OrderId}. " +
                    "Position removed from TradeGuard. Fill occurred at IBKR but is now untracked.",
                    trade.Symbol, trade.OrderId);
                await _discord.NotifyCriticalAsync(
                    $"🚨 Position Write Failed — {trade.Symbol}",
                    $"**{trade.Symbol}** filled at IBKR " +
                    $"(${result.FillPrice:F2} × {result.FillQuantity}) " +
                    $"but could not be saved to the database.\n" +
                    $"The position has been removed from Vela tracking.\n" +
                    $"Error: {ex.Message}\n" +
                    "Immediate manual review required — " +
                    "position may be open at IBKR without Vela stop protection.",
                    ct);
            }
            return;
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
            SessionLocation           = Environment.GetEnvironmentVariable("TRADING_LOCATION"),
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
        // correctly for all risk tiers without needing to know which tier's config value to apply.
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

    // When post-fill slippage is elevated the order's stop and target prices were derived from
    // the alert price and no longer reflect actual position risk. Rebase both from the confirmed
    // fill price so open_positions, CSV, and trade_metrics show meaningful values.
    // The trail stop at IBKR already trails from the fill price, this only affects persistence.
    private TradeOrder AdjustOrderPricesForFill(
        TradeOrder order,
        BrokerOrderResult result,
        decimal alertedPrice)
    {
        if (_riskOptions.PostFillSlippageWarningPct <= 0 || alertedPrice <= 0 || result.FillPrice <= 0)
            return order;

        var slippagePct = (result.FillPrice - alertedPrice) / alertedPrice * 100;
        if (slippagePct <= (decimal)_riskOptions.PostFillSlippageWarningPct)
            return order;

        // Use the effective (possibly tightened) trail to derive the new stop price floor.
        var effectiveTrailPct = _riskOptions.HighSlippageTrailPct > 0
            ? (decimal)Math.Min(order.TrailPercent, _riskOptions.HighSlippageTrailPct)
            : (decimal)order.TrailPercent;

        // Derive the target multiple from the original alert-based prices, then rebase on fill.
        var targetMultiple = order.EstimatedEntryPrice > 0
            ? order.TargetPrice / order.EstimatedEntryPrice
            : 3.0m;

        var newStop   = Math.Round(result.FillPrice * (1m - effectiveTrailPct / 100m), 2);
        var newTarget = Math.Round(result.FillPrice * targetMultiple, 2);

        _logger.LogInformation(
            "Rebasing stop and target for {Symbol} from alert price ${Alert:F2} to fill price ${Fill:F2}. " +
            "Stop: ${OldStop:F2} -> ${NewStop:F2} | Target: ${OldTarget:F2} -> ${NewTarget:F2}",
            order.Symbol, alertedPrice, result.FillPrice,
            order.StopPrice, newStop, order.TargetPrice, newTarget);

        return order with { StopPrice = newStop, TargetPrice = newTarget };
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