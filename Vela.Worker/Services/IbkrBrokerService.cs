using IBApi;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// IBKR broker implementation using the TWS API via IB Gateway.
/// Requires IB Gateway running on localhost:4002 (paper) or 4001 (live trading).
/// Swap <see cref="NullBrokerService"/> for this in Program.cs when ready for paper trading.
/// </summary>
public class IbkrBrokerService : IBrokerService
{
    private readonly IbkrConnectionService _connection;
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrBrokerService> _logger;

    private int _nextReqId = 1;
    private int _nextOrderId = 1;

    // Maps stop/target orderId to the parent entry orderId so broker-side fills can be routed
    private readonly Dictionary<int, (string EntryOrderId, TradeOutcome Outcome)> _stopOrderMap = new();
    private readonly Lock _stopMapLock = new();

    // Subscribed by PositionMonitorService to handle broker-side stop and target fills
    private Action<string, decimal, TradeOutcome>? _brokerFillHandler;

    // Fill window for limit orders before the unfilled portion is cancelled
    private const int LimitOrderFillWindowSeconds = 10;

    // How long to wait on execDetails after the fill window expires before falling back
    // to reqPositions. ExecDetails fires within seconds of a fill even when the IBKR
    // position book (reqPositions) takes 3-25 minutes to propagate the same fill.
    private const int ExecDetailsPostCancelWaitSeconds = 60;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public IbkrBrokerService(
        IbkrConnectionService connection,
        IOptions<IbkrOptions> options,
        ILogger<IbkrBrokerService> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes a handler that fires when a broker-side stop or target order fills.
    /// The handler receives the entry order ID, fill price, and trade outcome.
    /// </summary>
    public void RegisterBrokerFillHandler(Action<string, decimal, TradeOutcome> handler)
    {
        _brokerFillHandler = handler;
    }

    /// <summary>
    /// Re-registers stop and target order callbacks for positions restored from the database on restart.
    /// Without this, broker-side trail stop or target fills after a restart are silently ignored
    /// because _stopOrderMap is empty and execDetails callbacks are never wired up.
    /// Positions with no TargetOrderId (0DTE) only register the trail stop callback.
    /// Called from Program.cs after LoadFromDatabase.
    /// </summary>
    public void ReRegisterStopCallbacks(IEnumerable<OpenPosition> positions)
    {
        var count = 0;
        foreach (var p in positions)
        {
            if (!int.TryParse(p.StopOrderId, out var stopId) ||
                !int.TryParse(p.OrderId, out _))
                continue;

            var hasTarget = p.TargetOrderId is not null &&
                            int.TryParse(p.TargetOrderId, out var _);
            int.TryParse(p.TargetOrderId, out var targetId);

            var entryOrderId = p.OrderId;

            lock (_stopMapLock)
            {
                _stopOrderMap[stopId] = (entryOrderId, TradeOutcome.StoppedOut);
                if (hasTarget)
                    _stopOrderMap[targetId] = (entryOrderId, TradeOutcome.TargetHit);
            }

            _connection.Wrapper.RegisterExecDetailsCallback(stopId, fillPrice =>
                OnStopOrderFilled(stopId, fillPrice));

            if (hasTarget)
                _connection.Wrapper.RegisterExecDetailsCallback(targetId, fillPrice =>
                    OnStopOrderFilled(targetId, fillPrice));

            count++;

            _logger.LogDebug(
                "IBKR re-registered stop callbacks for {Symbol} — StopOrderId: {StopId} TargetOrderId: {TargetId}",
                p.Symbol, stopId, hasTarget ? targetId : null);
        }

        _logger.LogInformation(
            "IBKR stop callback re-registration complete — {Count} position(s) re-wired", count);
    }

    /// <summary>
    /// Returns the net liquidation value of the IBKR account.
    /// Used by <see cref="TradeGuard"/> to verify available capital before placing orders.
    /// </summary>
    public async Task<decimal> GetAccountBalanceAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return 0m;

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterAccountCallback(reqId);
        _connection.Client.reqAccountSummary(reqId, "All", "NetLiquidation");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var balance))
            {
                _logger.LogDebug("IBKR account balance: ${Balance:F2}", balance);
                return balance;
            }

            _logger.LogWarning("IBKR GetAccountBalance could not parse value: {Value}", valueStr);
            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAccountBalance timed out");
            _connection.Wrapper.UnregisterAccountCallback(reqId);
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Returns the current average cost and actual held quantity of an open position from IBKR.
    /// Quantity will be negative for short positions, BrokerExecutionService guards against this.
    /// Returns (0, 0) if the position is not found or the request times out.
    /// </summary>
    public async Task<(decimal Price, int Quantity)> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default)
    {
        if (!EnsureConnected()) return (0m, 0);

        var key = trade.TradeType == TradeType.Options
            ? $"{trade.Symbol}::{trade.OptionsContract}"
            : $"{trade.Symbol}::STK";

        var tcs = _connection.Wrapper.RegisterPositionCallback(key);
        _connection.Client.reqPositions();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var (price, qty) = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogDebug(
                "IBKR position for {Symbol}: avgCost ${Price:F2} qty {Qty}",
                trade.Symbol, price, qty);

            return (price, qty);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR GetCurrentPositionPrice timed out for {Symbol}.", trade.Symbol);
            _connection.Wrapper.UnregisterPositionCallback(key);
            return (0m, 0);
        }
        finally
        {
            _connection.Client.cancelPositions();
        }
    }

    public async Task<PositionsSnapshot> GetAllPositionsAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return new PositionsSnapshot([], false);

        var tcs = _connection.Wrapper.RegisterAllPositionsCallback();
        _connection.Client.reqPositions();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var positions = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogDebug(
                "IBKR GetAllPositions — {Count} positions received", positions.Count);

            return new PositionsSnapshot(positions, false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAllPositions timed out.");
            _connection.Wrapper.UnregisterAllPositionsCallback();
            return new PositionsSnapshot([], true);
        }
        finally
        {
            _connection.Client.cancelPositions();
        }
    }

    public async Task<OrdersSnapshot> GetAllOpenOrdersAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return new OrdersSnapshot([], false);

        var tcs = _connection.Wrapper.RegisterAllOpenOrdersCallback();

        // reqAllOpenOrders returns orders from all API sessions, not just the current one.
        // Trail stop orders placed in previous sessions appear here, which is what we need
        // to correctly classify managed vs unknown orders at startup.
        _connection.Client.reqAllOpenOrders();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var orders = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogDebug(
                "IBKR GetAllOpenOrders — {Count} orders received", orders.Count);

            return new OrdersSnapshot(orders, false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAllOpenOrders timed out.");
            _connection.Wrapper.UnregisterAllOpenOrdersCallback();
            return new OrdersSnapshot([], true);
        }
    }

    /// <summary>
    /// Returns true if the given order ID is tracked in the stop/target order map,
    /// meaning Vela placed it and is managing it this session.
    /// </summary>
    public bool IsKnownOrder(int orderId)
    {
        lock (_stopMapLock) { return _stopOrderMap.ContainsKey(orderId); }
    }

    /// <summary>
    /// Returns the current market price for any symbol using streaming market data.
    /// Resolves on LAST price immediately, or on bid/ask midpoint when no recent trade exists
    /// (premarket, after-hours). Used for pre-trade slippage checks before placing orders.
    /// Returns 0 if the quote cannot be retrieved within the timeout.
    /// </summary>
    public async Task<decimal> GetCurrentMarketPriceAsync(
        string symbol,
        TradeType tradeType,
        string? direction = null,
        decimal? strike = null,
        string? expiration = null,
        CancellationToken ct = default)
    {
        if (!EnsureConnected()) return 0m;

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterMarketDataCallback(reqId);

        var contract = tradeType == TradeType.Options
            ? new Contract
            {
                Symbol = symbol,
                SecType = "OPT",
                Exchange = "SMART",
                Currency = "USD",
                Right = direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike = (double)(strike ?? 0),
                LastTradeDateOrContractMonth =
                    expiration is not null
                        ? DateTimeOffset.Parse(expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier = "100",
            }
            : new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD",
            };

        // snapshot=false (streaming) so BID/ASK ticks fire even when no recent LAST trade exists.
        // cancelMktData in the finally block stops the stream once the price resolves or times out.
        _connection.Client.reqMktData(reqId, contract, "", false, false, null);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var price = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogDebug("IBKR market price for {Symbol}: ${Price:F2}", symbol, price);
            return price;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetCurrentMarketPrice timed out for {Symbol}.", symbol);
            _connection.Wrapper.UnregisterMarketDataCallback(reqId);
            return 0m;
        }
        finally
        {
            _connection.Client.cancelMktData(reqId);
        }
    }

    /// <summary>
    /// Returns the total market value of all open positions.
    /// Used by <see cref="TradeGuard"/> to verify current exposure before new orders.
    /// </summary>
    public async Task<decimal> GetOpenPositionsValueAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return 0m;

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterAccountCallback(reqId);
        _connection.Client.reqAccountSummary(reqId, "All", "GrossPositionValue");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var value))
            {
                _logger.LogDebug("IBKR open positions value: ${Value:F2}", value);
                return value;
            }

            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetOpenPositionsValue timed out");
            _connection.Wrapper.UnregisterAccountCallback(reqId);
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Returns a live snapshot of NetLiquidation, TotalCashValue, and BuyingPower via a
    /// batch reqAccountSummary call, plus TodayPnL via a separate single-shot reqPnL call.
    /// Always issues fresh requests to IB Gateway, never cached or DB-backed.
    /// </summary>
    public async Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return new AccountSnapshot(0m, 0m, 0m, 0m, false);

        var summaryReqId = NextReqId();
        var summaryTcs = _connection.Wrapper.RegisterAccountSnapshotCallback(summaryReqId);
        _connection.Client.reqAccountSummary(summaryReqId, "All", "NetLiquidation,TotalCashValue,BuyingPower");

        var pnlReqId = NextReqId();
        var pnlTcs = _connection.Wrapper.RegisterPnLCallback(pnlReqId);
        _connection.Client.reqPnL(pnlReqId, _options.AccountId, "");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var summaryTask = summaryTcs.Task.WaitAsync(cts.Token);
            var pnlTask = pnlTcs.Task.WaitAsync(cts.Token);
            await Task.WhenAll(summaryTask, pnlTask);

            var values = summaryTask.Result;

            decimal Parse(string tag) =>
                values.TryGetValue(tag, out var raw) && decimal.TryParse(raw, out var parsed) ? parsed : 0m;

            _logger.LogDebug(
                "IBKR GetAccountSnapshot — NetLiq: {NetLiq} Cash: {Cash} BP: {BP} PnL: {PnL}",
                Parse("NetLiquidation"), Parse("TotalCashValue"), Parse("BuyingPower"), pnlTask.Result);

            return new AccountSnapshot(
                NetLiquidation: Parse("NetLiquidation"),
                TotalCash:      Parse("TotalCashValue"),
                BuyingPower:    Parse("BuyingPower"),
                TodayPnL:       (decimal)pnlTask.Result,
                TimedOut:       false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAccountSnapshot timed out");
            _connection.Wrapper.UnregisterAccountSnapshotCallback(summaryReqId);
            _connection.Wrapper.UnregisterPnLCallback(pnlReqId);
            return new AccountSnapshot(0m, 0m, 0m, 0m, true);
        }
        finally
        {
            _connection.Client.cancelAccountSummary(summaryReqId);
            _connection.Client.cancelPnL(pnlReqId);
        }
    }

    /// <summary>
    /// Places a bracket entry order then, once confirmed filled, replaces the fixed stop with
    /// an OCA group containing a TRAIL stop and optionally a LMT profit target.
    /// Spyglass stock entries with a computed price target receive trail + target in OCA;
    /// all other entries receive trail-only. When the order has a LimitPrice set, a LMT order
    /// is placed with a shorter fill window (10s); unfilled remainder is cancelled before
    /// BrokerExecutionService verifies the actual fill via execDetails. ExecDetails fires within
    /// seconds of a fill even when the IBKR position book (reqPositions) takes minutes to
    /// propagate the same fill — this prevents the MANUAL tracking record fallback and ensures
    /// the trail stop is always placed by the normal path. Market orders retain the 15s window
    /// with the same execDetails late-fill detection.
    /// </summary>
    public async Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var orderId = GetNextOrderId();
        var tcs = _connection.Wrapper.RegisterOrderCallback(orderId);
        var contract = BuildContract(order);

        // Compute minimum price tick once, applied to both the entry limit and the temp stop
        // so neither triggers a [110] price variation rejection from IBKR.
        var minTick = GetOptionsMinTick(order);
        var entryLimitPrice = order.LimitPrice.HasValue
            ? (decimal)Math.Round(Math.Round((double)order.LimitPrice.Value / minTick) * minTick, 2)
            : (decimal?)null;

        var entryOrder = entryLimitPrice.HasValue
            ? BuildLimitOrder(orderId, order.Quantity, "BUY", entryLimitPrice.Value)
            : BuildMarketOrder(orderId, order.Quantity, "BUY");

        var fillWindowSeconds = order.LimitPrice.HasValue ? LimitOrderFillWindowSeconds : 15;

        // Register exec details TCS before placing. For the normal fill path this is cleaned up
        // immediately. For the fill-window-timeout path it becomes the primary fill detector:
        // execDetails fires within seconds of a fill while reqPositions can take 3-25 minutes.
        var lateExecTcs = _connection.Wrapper.RegisterExecDetailsTcsCallback(orderId);

        // Place entry with a temporary fixed STP as bracket child so there is always
        // stop protection in place while we wait for the actual fill confirmation.
        var tempStopId = orderId + 1;
        var roundedStop = Math.Round(Math.Round((double)order.StopPrice / minTick) * minTick, 2);
        var tempStopOrder = BuildStopOrder(tempStopId, orderId, order.Quantity, roundedStop);

        entryOrder.Transmit = false;
        tempStopOrder.Transmit = true;

        try
        {
            _connection.Client.placeOrder(orderId, contract, entryOrder);
            _connection.Client.placeOrder(tempStopId, contract, tempStopOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(fillWindowSeconds));

            // Only resolves when IBKR confirms status "Filled".
            var state = await tcs.Task.WaitAsync(cts.Token);

            // IBKR cancelled the order before the fill window expired (e.g. price-protection
            // rejection). Return without placing trail stop or OCA group.
            // Small delay before reading the rejection reason: orderStatus("Cancelled") fires
            // slightly before the [202] error callback that carries the market price.
            if (state.Status is not "Filled")
            {
                _connection.Client.cancelOrder(tempStopId);
                _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);

                await Task.Delay(600, ct);

                var priceProtectionReason = _connection.Wrapper.TakeRejectionReason(orderId);

                _logger.LogWarning(
                    "IBKR order cancelled before fill for {Symbol} — Reason: {Reason}",
                    order.Symbol, priceProtectionReason ?? "no reason provided");

                return new BrokerOrderResult(
                    OrderId:         orderId.ToString(),
                    StopOrderId:     null,
                    TargetOrderId:   null,
                    FillPrice:       0m,
                    FillQuantity:    0,
                    FillAmount:      0m,
                    Status:          OrderStatus.Cancelled,
                    FilledAt:        DateTimeOffset.UtcNow,
                    RejectionReason: priceProtectionReason);
            }

            // Normal fill, exec details TCS no longer needed
            _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);

            _logger.LogDebug(
                "IBKR entry filled. OrderId: {OrderId} Status: {Status} — replacing stop with OCA trail",
                orderId, state.Status);

            _connection.Client.cancelOrder(tempStopId);
            await Task.Delay(600, ct);

            // Use FilledQuantity from orderStatus — may be less than order.Quantity on a partial
            // fill. Trail stop must match actual position size to avoid overselling into a short.
            var fillPrice = state.AvgFillPrice > 0 ? state.AvgFillPrice : order.EstimatedEntryPrice;
            var fillQty = state.FilledQuantity > 0 ? state.FilledQuantity : order.Quantity;
            var multiplier = order.TradeType == TradeType.Options ? 100m : 1m;

            var trailStopId = orderId + 2;
            string? finalStopId;
            string? finalTargetId;

            if (ShouldPlaceTargetOrder(order))
            {
                var targetOrderId = orderId + 3;
                (finalStopId, finalTargetId) = await PlaceTrailWithTargetAsync(
                    order.Symbol, orderId.ToString(), trailStopId, targetOrderId, contract,
                    fillQty, order.TrailPercent, order.TargetPrice, ct);

                _logger.LogDebug(
                    "IBKR trail+target placed for Spyglass {Symbol} — Qty: {Qty} Trail: {Trail}% Target: ${Target:F2} StopId: {StopId} TargetId: {TargetId}",
                    order.Symbol, fillQty, order.TrailPercent, order.TargetPrice,
                    finalStopId ?? "NONE", finalTargetId ?? "NONE");
            }
            else
            {
                finalStopId = await PlaceTrailWithFallbackAsync(
                    order.Symbol, orderId.ToString(), trailStopId, contract,
                    fillQty, order.TrailPercent, ct);
                finalTargetId = null;

                _logger.LogDebug(
                    "IBKR trail placed — Qty: {Qty} Trail: {TrailPct}% StopId: {StopId}",
                    fillQty, order.TrailPercent, finalStopId ?? "NONE");
            }

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   finalStopId,
                TargetOrderId: finalTargetId,
                FillPrice:     fillPrice,
                FillQuantity:  fillQty,
                FillAmount:    fillPrice * fillQty * multiplier,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            if (order.LimitPrice.HasValue)
            {
                _connection.Client.cancelOrder(orderId);
                _connection.Client.cancelOrder(tempStopId);
                _connection.Wrapper.UnregisterOrderCallback(orderId);

                // A [202] rejection racing with the fill window may arrive 1-3 seconds after the
                // OCE fires. Poll with fixed delays rather than a single check so a delayed
                // rejection is caught before the execDetails wait. Fixed delays (no ct) ensure
                // the loop completes even when the outer token is concurrently cancelled.
                string? rejectionReason = null;
                for (var attempt = 0; attempt < 6 && rejectionReason is null; attempt++)
                {
                    await Task.Delay(500);
                    rejectionReason = _connection.Wrapper.TakeRejectionReason(orderId);
                }

                if (rejectionReason is not null)
                {
                    _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);
                    _logger.LogWarning(
                        "IBKR limit order cancelled via price protection during fill window for {Symbol} — " +
                        "skipping position verification. Reason: {Reason}",
                        order.Symbol, rejectionReason);

                    return new BrokerOrderResult(
                        OrderId:         orderId.ToString(),
                        StopOrderId:     null,
                        TargetOrderId:   null,
                        FillPrice:       0m,
                        FillQuantity:    0,
                        FillAmount:      0m,
                        Status:          OrderStatus.Rejected,
                        FilledAt:        DateTimeOffset.UtcNow,
                        RejectionReason: rejectionReason);
                }

                // No rejection found, the fill window expired while the order may have been
                // working. Use execDetails as the primary fill detector: it fires within seconds
                // of an exchange fill even when reqPositions takes 3-25 minutes to propagate
                // the same event. Waiting here catches fills that the fill window just missed
                // and avoids creating a MANUAL tracking record via the periodic reconciler.
                _logger.LogDebug(
                    "IBKR fill window expired for {Symbol} — awaiting execDetails for up to {Wait}s.",
                    order.Symbol, ExecDetailsPostCancelWaitSeconds);

                try
                {
                    using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    execCts.CancelAfter(TimeSpan.FromSeconds(ExecDetailsPostCancelWaitSeconds));
                    var execFillPrice = await lateExecTcs.Task.WaitAsync(execCts.Token);

                    _logger.LogInformation(
                        "IBKR execDetails confirmed fill for {Symbol} @ ${Price:F2} after fill window — " +
                        "placing trail stop via normal path.",
                        order.Symbol, execFillPrice);

                    await Task.Delay(600, ct);

                    // Verify actual quantity in case of partial fill before placing stop.
                    var posKey = order.TradeType == TradeType.Options
                        ? $"{order.Symbol}::{order.OptionsContractSymbol}"
                        : $"{order.Symbol}::STK";

                    var posTcs = _connection.Wrapper.RegisterPositionCallback(posKey);
                    _connection.Client.reqPositions();

                    int execFillQty;
                    try
                    {
                        using var posCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        posCts.CancelAfter(_options.TimeoutMs);
                        var (_, qty) = await posTcs.Task.WaitAsync(posCts.Token);
                        execFillQty = qty > 0 ? qty : order.Quantity;
                    }
                    catch (OperationCanceledException)
                    {
                        _connection.Wrapper.UnregisterPositionCallback(posKey);
                        execFillQty = order.Quantity;
                        _logger.LogWarning(
                            "IBKR qty verification timed out for {Symbol} after execDetails — using ordered qty {Qty}.",
                            order.Symbol, order.Quantity);
                    }
                    finally
                    {
                        _connection.Client.cancelPositions();
                    }

                    var trailStopId = orderId + 2;
                    string? finalStopId;
                    string? finalTargetId;

                    if (ShouldPlaceTargetOrder(order))
                    {
                        var targetOrderId = orderId + 3;
                        (finalStopId, finalTargetId) = await PlaceTrailWithTargetAsync(
                            order.Symbol, orderId.ToString(), trailStopId, targetOrderId, contract,
                            execFillQty, order.TrailPercent, order.TargetPrice, ct);
                    }
                    else
                    {
                        finalStopId = await PlaceTrailWithFallbackAsync(
                            order.Symbol, orderId.ToString(), trailStopId, contract,
                            execFillQty, order.TrailPercent, ct);
                        finalTargetId = null;
                    }

                    var multiplier = order.TradeType == TradeType.Options ? 100m : 1m;

                    return new BrokerOrderResult(
                        OrderId:       orderId.ToString(),
                        StopOrderId:   finalStopId,
                        TargetOrderId: finalTargetId,
                        FillPrice:     execFillPrice,
                        FillQuantity:  execFillQty,
                        FillAmount:    execFillPrice * execFillQty * multiplier,
                        Status:        OrderStatus.Filled,
                        FilledAt:      DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    // execDetails did not fire within the wait window, genuine non-fill or
                    // extremely slow propagation. Fall back to reqPositions as last resort.
                    _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);
                    _logger.LogDebug(
                        "IBKR execDetails timed out for {Symbol} — falling back to reqPositions check.",
                        order.Symbol);
                }

                // reqPositions fallback: two checks separated by 30s. This path is now only
                // reached when execDetails also timed out, making it genuinely rare.
                var fallbackPosKey = order.TradeType == TradeType.Options
                    ? $"{order.Symbol}::{order.OptionsContractSymbol}"
                    : $"{order.Symbol}::STK";

                int verifiedQty = 0;

                var fallbackTcs = _connection.Wrapper.RegisterPositionCallback(fallbackPosKey);
                _connection.Client.reqPositions();
                try
                {
                    using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    checkCts.CancelAfter(_options.TimeoutMs);
                    var (_, qty) = await fallbackTcs.Task.WaitAsync(checkCts.Token);
                    verifiedQty = qty;
                }
                catch (OperationCanceledException)
                {
                    _connection.Wrapper.UnregisterPositionCallback(fallbackPosKey);
                }
                finally
                {
                    _connection.Client.cancelPositions();
                }

                if (verifiedQty <= 0)
                {
                    _logger.LogDebug(
                        "No position found for {Symbol} on reqPositions check — waiting 30s before final check.",
                        order.Symbol);

                    await Task.Delay(TimeSpan.FromSeconds(30), ct);

                    var retryPosTcs = _connection.Wrapper.RegisterPositionCallback(fallbackPosKey);
                    _connection.Client.reqPositions();
                    try
                    {
                        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        retryCts.CancelAfter(_options.TimeoutMs);
                        var (_, retryQty) = await retryPosTcs.Task.WaitAsync(retryCts.Token);
                        verifiedQty = retryQty;
                    }
                    catch (OperationCanceledException)
                    {
                        _connection.Wrapper.UnregisterPositionCallback(fallbackPosKey);
                    }
                    finally
                    {
                        _connection.Client.cancelPositions();
                    }
                }

                if (verifiedQty > 0)
                {
                    _logger.LogInformation(
                        "IBKR position confirmed for {Symbol} via reqPositions fallback — qty {Qty}. " +
                        "Entering pending verification.",
                        order.Symbol, verifiedQty);

                    return new BrokerOrderResult(
                        OrderId:       orderId.ToString(),
                        StopOrderId:   null,
                        TargetOrderId: null,
                        FillPrice:     0m,
                        FillQuantity:  0,
                        FillAmount:    0m,
                        Status:        OrderStatus.Pending,
                        FilledAt:      DateTimeOffset.UtcNow);
                }

                _logger.LogWarning(
                    "IBKR limit order for {Symbol} cancelled — no fill confirmed via execDetails or reqPositions. " +
                    "Treating as rejected to prevent ghost recording.",
                    order.Symbol);

                return new BrokerOrderResult(
                    OrderId:         orderId.ToString(),
                    StopOrderId:     null,
                    TargetOrderId:   null,
                    FillPrice:       0m,
                    FillQuantity:    0,
                    FillAmount:      0m,
                    Status:          OrderStatus.Rejected,
                    FilledAt:        DateTimeOffset.UtcNow,
                    RejectionReason: "Cancelled — no fill confirmed after fill window");
            }

            // Market order timeout: wait for a late ExecDetails callback before giving up.
            _logger.LogDebug(
                "IBKR PlaceOrder timed out for {Symbol} after 15s — sending cancel, checking for late fill.",
                order.Symbol);

            _connection.Client.cancelOrder(orderId);
            _connection.Client.cancelOrder(tempStopId);
            _connection.Wrapper.UnregisterOrderCallback(orderId);

            try
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(TimeSpan.FromSeconds(10));
                var lateFillPrice = await lateExecTcs.Task.WaitAsync(execCts.Token);

                _logger.LogInformation(
                    "IBKR PlaceOrder late fill detected for {Symbol} @ ${Price:F2} — verifying actual position qty.",
                    order.Symbol, lateFillPrice);

                await Task.Delay(600, ct);

                var posKey = order.TradeType == TradeType.Options
                    ? $"{order.Symbol}::{order.OptionsContractSymbol}"
                    : $"{order.Symbol}::STK";

                var posTcs = _connection.Wrapper.RegisterPositionCallback(posKey);
                _connection.Client.reqPositions();

                int actualLateFillQty;
                try
                {
                    using var posCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    posCts.CancelAfter(_options.TimeoutMs);
                    var (_, lateFillQty) = await posTcs.Task.WaitAsync(posCts.Token);
                    actualLateFillQty = lateFillQty > 0 ? lateFillQty : order.Quantity;
                }
                catch (OperationCanceledException)
                {
                    _connection.Wrapper.UnregisterPositionCallback(posKey);
                    actualLateFillQty = order.Quantity;
                    _logger.LogWarning(
                        "IBKR late fill qty verification timed out for {Symbol} — using ordered qty {Qty}.",
                        order.Symbol, order.Quantity);
                }
                finally
                {
                    _connection.Client.cancelPositions();
                }

                var trailStopId = orderId + 2;
                string? lateFinalStopId;
                string? lateFinalTargetId;

                if (ShouldPlaceTargetOrder(order))
                {
                    var targetOrderId = orderId + 3;
                    (lateFinalStopId, lateFinalTargetId) = await PlaceTrailWithTargetAsync(
                        order.Symbol, orderId.ToString(), trailStopId, targetOrderId, contract,
                        actualLateFillQty, order.TrailPercent, order.TargetPrice, ct);

                    _logger.LogDebug(
                        "IBKR trail+target placed for Spyglass {Symbol} late fill — Qty: {Qty} Trail: {Trail}% Target: ${Target:F2} StopId: {StopId} TargetId: {TargetId}",
                        order.Symbol, actualLateFillQty, order.TrailPercent, order.TargetPrice,
                        lateFinalStopId ?? "NONE", lateFinalTargetId ?? "NONE");
                }
                else
                {
                    lateFinalStopId = await PlaceTrailWithFallbackAsync(
                        order.Symbol, orderId.ToString(), trailStopId, contract,
                        actualLateFillQty, order.TrailPercent, ct);
                    lateFinalTargetId = null;

                    _logger.LogDebug(
                        "IBKR trail placed for late fill — Qty: {Qty} Trail: {TrailPct}% StopId: {StopId}",
                        actualLateFillQty, order.TrailPercent, lateFinalStopId ?? "NONE");
                }

                var multiplier = order.TradeType == TradeType.Options ? 100m : 1m;

                return new BrokerOrderResult(
                    OrderId:       orderId.ToString(),
                    StopOrderId:   lateFinalStopId,
                    TargetOrderId: lateFinalTargetId,
                    FillPrice:     lateFillPrice,
                    FillQuantity:  actualLateFillQty,
                    FillAmount:    lateFillPrice * actualLateFillQty * multiplier,
                    Status:        OrderStatus.Filled,
                    FilledAt:      DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);

                _logger.LogWarning(
                    "IBKR PlaceOrder confirmed no fill for {Symbol} — order cancelled.",
                    order.Symbol);

                var rejectionReason = _connection.Wrapper.TakeRejectionReason(orderId);
                if (rejectionReason is not null)
                {
                    _logger.LogWarning(
                        "IBKR order rejected for {Symbol}: {Reason}", order.Symbol, rejectionReason);

                    return new BrokerOrderResult(
                        OrderId:         orderId.ToString(),
                        StopOrderId:     null,
                        TargetOrderId:   null,
                        FillPrice:       0m,
                        FillQuantity:    0,
                        FillAmount:      0m,
                        Status:          OrderStatus.Rejected,
                        FilledAt:        DateTimeOffset.UtcNow,
                        RejectionReason: rejectionReason);
                }

                return new BrokerOrderResult(
                    OrderId:         orderId.ToString(),
                    StopOrderId:     null,
                    TargetOrderId:   null,
                    FillPrice:       0m,
                    FillQuantity:    0,
                    FillAmount:      0m,
                    Status:          OrderStatus.Rejected,
                    FilledAt:        DateTimeOffset.UtcNow,
                    RejectionReason: "Entry timed out after 15s — order cancelled");
            }
        }
    }

    /// <summary>
    /// Places a market sell order for a specific quantity of an existing position.
    /// Used for partial profit taking on 1DTE positions at 3pm ET.
    /// The remaining position stays open, caller must handle stop management separately.
    /// </summary>
    public async Task<BrokerOrderResult> PartialCloseAsync(
        TradeRecord trade,
        int quantityToClose,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var closeOrderId = GetNextOrderId();
        var tcs = _connection.Wrapper.RegisterOrderCallback(closeOrderId);
        var execTcs = _connection.Wrapper.RegisterExecDetailsTcsCallback(closeOrderId);
        var contract = BuildCloseContract(trade);

        var partialCloseOrder = new Order
        {
            OrderId = closeOrderId,
            Action = "SELL",
            OrderType = "MKT",
            TotalQuantity = quantityToClose,
            Transmit = true,
        };

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, partialCloseOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var fill = await tcs.Task.WaitAsync(cts.Token);
            var fillPrice = fill.AvgFillPrice > 0 ? fill.AvgFillPrice : trade.EntryPrice;
            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

            _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);

            _logger.LogDebug(
                "IBKR partial close filled. OrderId: {OrderId} Symbol: {Symbol} Qty: {Qty} FillPrice: {Price:F2}",
                closeOrderId, trade.Symbol, quantityToClose, fillPrice);

            return new BrokerOrderResult(
                OrderId:       closeOrderId.ToString(),
                StopOrderId:   null,
                TargetOrderId: null,
                FillPrice:     fillPrice,
                FillQuantity:  quantityToClose,
                FillAmount:    fillPrice * quantityToClose * multiplier,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR PartialClose timed out for {Symbol} — waiting for execDetails callback.",
                trade.Symbol);

            _connection.Wrapper.UnregisterOrderCallback(closeOrderId);

            try
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(TimeSpan.FromSeconds(30));
                var execFillPrice = await execTcs.Task.WaitAsync(execCts.Token);
                var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

                return new BrokerOrderResult(
                    OrderId:       closeOrderId.ToString(),
                    StopOrderId:   null,
                    TargetOrderId: null,
                    FillPrice:     execFillPrice,
                    FillQuantity:  quantityToClose,
                    FillAmount:    execFillPrice * quantityToClose * multiplier,
                    Status:        OrderStatus.Filled,
                    FilledAt:      DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);
                return FailedResult("Partial close timed out");
            }
        }
    }

    /// <summary>
    /// Cancels a specific order by ID and removes it from internal tracking.
    /// Used to cancel trail stops on positions converted to lotto overnight holds.
    /// </summary>
    public Task CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        if (!EnsureConnected()) return Task.CompletedTask;

        _connection.Client.cancelOrder(orderId);
        _connection.Wrapper.UnregisterExecDetailsCallback(orderId);
        RemoveStopOrderMapping(orderId);

        _logger.LogDebug(
            "IBKR order {OrderId} cancelled — removed from stop tracking.", orderId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels any active stop and target orders then places a market close order.
    /// Uses the actual average fill price from the IBKR callback for accurate P&amp;L calculation.
    /// On timeout, waits an additional 30 seconds for the execDetails callback before
    /// falling back to entry price — prevents $0.00 P&amp;L from slow fills.
    /// </summary>
    public async Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        // Unregister exec callbacks since we are closing manually — avoids double-close
        if (int.TryParse(trade.StopOrderId, out var stopIdToUnregister))
        {
            _connection.Wrapper.UnregisterExecDetailsCallback(stopIdToUnregister);
            RemoveStopOrderMapping(stopIdToUnregister);
        }

        if (int.TryParse(trade.TargetOrderId, out var targetIdToUnregister))
        {
            _connection.Wrapper.UnregisterExecDetailsCallback(targetIdToUnregister);
            RemoveStopOrderMapping(targetIdToUnregister);
        }

        if (trade.StopOrderId is not null && int.TryParse(trade.StopOrderId, out var stopId))
        {
            _connection.Client.cancelOrder(stopId);
            _logger.LogDebug(
                "IBKR cancelled stop order {OrderId} for {Symbol}", stopId, trade.Symbol);
        }

        if (trade.TargetOrderId is not null && int.TryParse(trade.TargetOrderId, out var targetId))
        {
            _connection.Client.cancelOrder(targetId);
            _logger.LogDebug(
                "IBKR cancelled target order {OrderId} for {Symbol}", targetId, trade.Symbol);
        }

        await Task.Delay(500, ct);

        // Verify the actual held quantity before closing so we never sell more than IBKR holds.
        // A stale or estimated record quantity (e.g. from a verification-timeout entry) would
        // otherwise oversell into a short. When the quantity cannot be confirmed (a timeout, or
        // the position is not returned) we fall back to the recorded quantity, as before.
        var (_, heldQty) = await GetCurrentPositionPriceAsync(trade, ct);
        var closeQty = heldQty > 0 ? Math.Min(trade.Quantity, heldQty) : trade.Quantity;

        if (heldQty > 0 && closeQty < trade.Quantity)
            _logger.LogWarning(
                "IBKR close quantity clamped for {Symbol} — record holds {Recorded} but IBKR holds {Held}. " +
                "Closing {CloseQty} to avoid overselling into a short.",
                trade.Symbol, trade.Quantity, heldQty, closeQty);
        else if (heldQty <= 0)
            _logger.LogWarning(
                "IBKR could not confirm held quantity for {Symbol} before close — using recorded quantity {Qty}.",
                trade.Symbol, trade.Quantity);

        var closeOrderId = GetNextOrderId();
        var tcs = _connection.Wrapper.RegisterOrderCallback(closeOrderId);
        var execTcs = _connection.Wrapper.RegisterExecDetailsTcsCallback(closeOrderId);
        var contract = BuildCloseContract(trade);
        var closeOrder = BuildCloseOrder(closeOrderId, closeQty);

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, closeOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var fill = await tcs.Task.WaitAsync(cts.Token);
            var fillPrice = fill.AvgFillPrice > 0 ? fill.AvgFillPrice : trade.EntryPrice;
            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

            _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);

            _logger.LogDebug(
                "IBKR position closed. OrderId: {OrderId} Symbol: {Symbol} Status: {Status} FillPrice: {Price:F2}",
                closeOrderId, trade.Symbol, fill.Status, fillPrice);

            return new BrokerOrderResult(
                OrderId:       closeOrderId.ToString(),
                StopOrderId:   null,
                TargetOrderId: null,
                FillPrice:     fillPrice,
                FillQuantity:  closeQty,
                FillAmount:    fillPrice * closeQty * multiplier,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR ClosePosition timed out for {Symbol} — waiting for execDetails callback.",
                trade.Symbol);

            _connection.Wrapper.UnregisterOrderCallback(closeOrderId);

            try
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(TimeSpan.FromSeconds(30));
                var execFillPrice = await execTcs.Task.WaitAsync(execCts.Token);
                var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

                _logger.LogDebug(
                    "IBKR ClosePosition recovered fill via execDetails — Symbol: {Symbol} FillPrice: {Price:F2}",
                    trade.Symbol, execFillPrice);

                return new BrokerOrderResult(
                    OrderId:       closeOrderId.ToString(),
                    StopOrderId:   null,
                    TargetOrderId: null,
                    FillPrice:     execFillPrice,
                    FillQuantity:  closeQty,
                    FillAmount:    execFillPrice * closeQty * multiplier,
                    Status:        OrderStatus.Filled,
                    FilledAt:      DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "IBKR ClosePosition execDetails also timed out for {Symbol} — using entry price fallback.",
                    trade.Symbol);

                _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);

                return new BrokerOrderResult(
                    OrderId:       closeOrderId.ToString(),
                    StopOrderId:   null,
                    TargetOrderId: null,
                    FillPrice:     trade.EntryPrice,
                    FillQuantity:  trade.Quantity,
                    FillAmount:    trade.EntryAmount,
                    Status:        OrderStatus.Pending,
                    FilledAt:      DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>
    /// Syncs the internal order ID counter from Gateway's next valid order ID,
    /// also accounting for the highest ID already stored in trade_metrics.
    /// Prevents duplicate key errors when Gateway resets its weekly counter to
    /// values that overlap with existing DB rows.
    /// Called on startup after the nextValidId callback is received.
    /// </summary>
    public void SyncOrderId(int maxDbOrderId = 0)
    {
        var gatewayId = _connection.Wrapper.NextValidOrderId;
        var target = Math.Max(gatewayId - 10, maxDbOrderId);
        Interlocked.Exchange(ref _nextOrderId, target);

        _logger.LogInformation(
            "IBKR order ID synced — Gateway: {GatewayId}, MaxDB: {MaxDbId}, next order will use: {Next}",
            gatewayId, maxDbOrderId, target + 10);
    }

    /// <summary>
    /// Fetches daily OHLCV bars for a stock symbol via Gateway's reqHistoricalData.
    /// Used by MarketConditionsLogger to compute moving averages and ADX.
    /// Requests barCount + 28 extra bars so ADX(14) has enough warmup data.
    /// Returns an empty list if Gateway is unavailable or the request times out.
    /// </summary>
    public async Task<List<HistoricalBar>> GetHistoricalBarsAsync(
        string symbol,
        int barCount,
        CancellationToken ct = default)
    {
        if (!EnsureConnected()) return [];

        var reqId = NextReqId();
        var tcs = _connection.Wrapper.RegisterHistoricalDataCallback(reqId);
        var contract = symbol == "VIX"
            ? new Contract
            {
                Symbol = "VIX",
                SecType = "IND",
                Exchange = "CBOE",
                Currency = "USD",
            }
            : new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD",
            };

        var durationStr = $"{barCount + 28} D";

        _connection.Client.reqHistoricalData(
            reqId,
            contract,
            string.Empty,
            durationStr,
            "1 day",
            "TRADES",
            1,
            1,
            false,
            null);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var bars = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogDebug(
                "IBKR historical data received — {Symbol} {Count} bars", symbol, bars.Count);

            return bars;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR GetHistoricalBars timed out for {Symbol} — falling back to Yahoo Finance.", symbol);
            _connection.Wrapper.UnregisterHistoricalDataCallback(reqId);
            return [];
        }
    }

    /// <summary>
    /// Cancels an existing trail stop and places a new one with a tighter trail percentage.
    /// Looks up the entry order mapping before removing so exec callbacks are re-wired correctly.
    /// Returns the new stop order ID, or null if the broker is unavailable or parsing fails.
    /// </summary>
    public async Task<string?> ReplaceTrailStopAsync(
        string existingStopOrderId,
        int quantity,
        TradeOrder order,
        double newTrailPercent,
        CancellationToken ct = default)
    {
        if (!EnsureConnected()) return null;

        if (!int.TryParse(existingStopOrderId, out var existingId))
        {
            _logger.LogWarning(
                "IBKR ReplaceTrailStop — cannot parse stop order ID: {Id}", existingStopOrderId);
            return null;
        }

        // Look up the parent entry order ID before removing from the map so callbacks
        // can be re-wired to the same entry after the replacement is placed.
        string entryOrderId;
        lock (_stopMapLock)
        {
            entryOrderId = _stopOrderMap.TryGetValue(existingId, out var mapping)
                ? mapping.EntryOrderId
                : string.Empty;
        }

        _connection.Client.cancelOrder(existingId);
        _connection.Wrapper.UnregisterExecDetailsCallback(existingId);
        RemoveStopOrderMapping(existingId);

        await Task.Delay(600, ct);

        var contract = BuildContract(order);
        var newStopId = GetNextOrderId();

        var replacedStopId = await PlaceTrailWithFallbackAsync(
            order.Symbol, entryOrderId, newStopId, contract, quantity, newTrailPercent, ct);

        if (replacedStopId is null)
        {
            _logger.LogError(
                "Trail stop replacement failed for {Symbol} — all attempts rejected. " +
                "Position is unprotected. Manual stop required.",
                order.Symbol);
            return null;
        }

        _logger.LogDebug(
            "IBKR trail stop replaced for {Symbol} — {OldId} -> {NewId} trail: {Trail}%",
            order.Symbol, existingStopOrderId, replacedStopId, newTrailPercent);

        return replacedStopId;
    }

    // -- Helpers --

    // Returns true for stock entries with a computed price target above entry price.
    // Only these entries receive the OCA trail+target group; all other entries use trail-only.
    private static bool ShouldPlaceTargetOrder(TradeOrder order) =>
        order.HasComputedTarget &&
        order.TradeType == TradeType.Stock &&
        order.TargetPrice > order.EstimatedEntryPrice;

    private void RegisterStopOrderCallbacks(int entryOrderId, int trailStopId, int? targetOrderId)
    {
        var entryIdStr = entryOrderId.ToString();

        lock (_stopMapLock)
        {
            _stopOrderMap[trailStopId] = (entryIdStr, TradeOutcome.StoppedOut);
            if (targetOrderId.HasValue)
                _stopOrderMap[targetOrderId.Value] = (entryIdStr, TradeOutcome.TargetHit);
        }

        _connection.Wrapper.RegisterExecDetailsCallback(trailStopId, fillPrice =>
            OnStopOrderFilled(trailStopId, fillPrice));

        if (targetOrderId.HasValue)
            _connection.Wrapper.RegisterExecDetailsCallback(targetOrderId.Value, fillPrice =>
                OnStopOrderFilled(targetOrderId.Value, fillPrice));
    }

    private void OnStopOrderFilled(int stopOrderId, decimal fillPrice)
    {
        (string EntryOrderId, TradeOutcome Outcome) mapping;
        lock (_stopMapLock)
        {
            if (!_stopOrderMap.TryGetValue(stopOrderId, out mapping))
                return;
            _stopOrderMap.Remove(stopOrderId);
        }

        _logger.LogDebug(
            "IBKR broker-side fill detected — StopOrderId: {StopId} EntryOrderId: {EntryId} " +
            "Outcome: {Outcome} FillPrice: ${Price:F2}",
            stopOrderId, mapping.EntryOrderId, mapping.Outcome, fillPrice);

        _ = Task.Run(() => _brokerFillHandler?.Invoke(
            mapping.EntryOrderId, fillPrice, mapping.Outcome));
    }

    private void RemoveStopOrderMapping(int orderId)
    {
        lock (_stopMapLock) { _stopOrderMap.Remove(orderId); }
    }

    private bool EnsureConnected()
    {
        if (_connection.IsConnected) return true;

        var connected = _connection.Connect();
        if (!connected)
            _logger.LogError(
                "Cannot reach IB Gateway. Is it running on {Host}:{Port}?",
                _options.Host, _options.Port);

        return connected;
    }

    private int NextReqId() => Interlocked.Increment(ref _nextReqId);

    // Leaves gaps of 10 between parent IDs to accommodate stop, target, and OCA child order IDs
    private int GetNextOrderId() => Interlocked.Add(ref _nextOrderId, 10);

    // Returns the minimum price increment for the given order's instrument.
    // Index options (SPX, NDX, RUT, XSP) use $0.10 increments regardless of price.
    // All other options use $0.05, correct for most equity and ETF option contracts.
    private static double GetOptionsMinTick(TradeOrder order)
    {
        if (order.TradeType != TradeType.Options) return 0.01;

        return order.Symbol is "SPX" or "SPXW" or "NDX" or "NDXW" or "RUT" or "XSP"
            ? 0.10
            : 0.05;
    }

    private static Contract BuildContract(TradeOrder order)
    {
        if (order.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol = order.Symbol,
                SecType = "OPT",
                Exchange = "SMART",
                Currency = "USD",
                Right = order.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike = (double)(order.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    order.Expiration is not null
                        ? DateTimeOffset.Parse(order.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier = "100",
            };
        }

        return new Contract
        {
            Symbol = order.Symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD",
        };
    }

    private static Contract BuildCloseContract(TradeRecord trade)
    {
        if (trade.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol = trade.Symbol,
                SecType = "OPT",
                Exchange = "SMART",
                Currency = "USD",
                Right = trade.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike = (double)(trade.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    trade.Expiration is not null
                        ? DateTimeOffset.Parse(trade.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier = "100",
            };
        }

        return new Contract
        {
            Symbol = trade.Symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD",
        };
    }

    private static Order BuildMarketOrder(int orderId, int quantity, string action) =>
        new()
        {
            OrderId = orderId,
            Action = action,
            OrderType = "MKT",
            TotalQuantity = quantity,
            Transmit = false,
        };

    private static Order BuildLimitOrder(int orderId, int quantity, string action, decimal limitPrice) =>
        new()
        {
            OrderId = orderId,
            Action = action,
            OrderType = "LMT",
            LmtPrice = (double)limitPrice,
            TotalQuantity = quantity,
            Transmit = false,
        };

    // Temporary bracket stop — placed atomically with the entry to ensure immediate protection.
    // Cancelled and replaced with OCA trail stop once the entry fill is confirmed.
    private static Order BuildStopOrder(int orderId, int parentId, int quantity, double stopPrice) =>
        new()
        {
            OrderId = orderId,
            ParentId = parentId,
            Action = "SELL",
            OrderType = "STP",
            AuxPrice = stopPrice,
            TotalQuantity = quantity,
            Transmit = false,
        };

    // Standalone trailing stop in an OCA group — no ParentId so IBKR accepts TRAIL type.
    private static Order BuildOcaTrailOrder(int orderId, int quantity, double trailPercent, string ocaGroup) =>
        new()
        {
            OrderId = orderId,
            Action = "SELL",
            OrderType = "TRAIL",
            TrailingPercent = trailPercent,
            TotalQuantity = quantity,
            OcaGroup = ocaGroup,
            OcaType = 1,
            Tif = "GTC",
            Transmit = true,
        };

    // Limit sell order in an OCA group. Used as profit target alongside an OCA trail stop.
    // Transmit=true so placing this order transmits both this and a held trail stop atomically.
    private static Order BuildOcaLimitOrder(int orderId, int quantity, decimal limitPrice, string ocaGroup) =>
        new()
        {
            OrderId = orderId,
            Action = "SELL",
            OrderType = "LMT",
            LmtPrice = (double)limitPrice,
            TotalQuantity = quantity,
            OcaGroup = ocaGroup,
            OcaType = 1,
            Tif = "GTC",
            Transmit = true,
        };

    // Standalone trailing stop without an OCA group. Used as fallback when the OCA trail
    // is rejected for cash-settled index options (e.g. SPX) that have different margin rules.
    private static Order BuildStandaloneTrailOrder(int orderId, int quantity, double trailPercent) =>
        new()
        {
            OrderId = orderId,
            Action = "SELL",
            OrderType = "TRAIL",
            TrailingPercent = trailPercent,
            TotalQuantity = quantity,
            Tif = "GTC",
            Transmit = true,
        };

    // Places OCA trail stop + limit target for stock entries with a computed price target.
    // The trail order is held (Transmit=false) until the target order transmits both atomically.
    // Both orders share an OCA group so whichever fills first cancels the other.
    // Falls back to trail-only via PlaceTrailWithFallbackAsync if IBKR rejects the OCA group.
    // Returns (StopId, TargetId); TargetId is null when the fallback path is used.
    private async Task<(string? StopId, string? TargetId)> PlaceTrailWithTargetAsync(
        string symbol,
        string entryOrderId,
        int trailStopId,
        int targetOrderId,
        Contract contract,
        int quantity,
        double trailPercent,
        decimal targetPrice,
        CancellationToken ct)
    {
        var ocaGroup = $"OCA_{trailStopId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var trailOrder = BuildOcaTrailOrder(trailStopId, quantity, trailPercent, ocaGroup);
        trailOrder.Transmit = false; // held until target order triggers transmission

        var targetOrder = BuildOcaLimitOrder(targetOrderId, quantity, targetPrice, ocaGroup);

        var rejectionTcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Wrapper.RegisterStopRejectionCallback(trailStopId, rejectionTcs);

        _connection.Client.placeOrder(trailStopId, contract, trailOrder);
        _connection.Client.placeOrder(targetOrderId, contract, targetOrder);

        string? rejection = null;
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromMilliseconds(600));
            rejection = await rejectionTcs.Task.WaitAsync(checkCts.Token);
        }
        catch (OperationCanceledException)
        {
            _connection.Wrapper.UnregisterStopRejectionCallback(trailStopId);
        }

        if (rejection is null)
        {
            RegisterStopOrderCallbacks(int.Parse(entryOrderId), trailStopId, targetOrderId);
            return (trailStopId.ToString(), targetOrderId.ToString());
        }

        // OCA rejected, cancel the target and fall back to trail-only
        _connection.Client.cancelOrder(targetOrderId);
        _logger.LogWarning(
            "OCA trail+target rejected for Spyglass {Symbol} — falling back to trail-only. Reason: {Reason}",
            symbol, rejection);

        var fallbackStopId = await PlaceTrailWithFallbackAsync(
            symbol, entryOrderId, GetNextOrderId(), contract, quantity, trailPercent, ct);

        return (fallbackStopId, null);
    }

    // Places an OCA trail stop and waits up to 600ms for an immediate 201 rejection.
    // If rejected, retries as a standalone trail stop (no OCA group), the fallback path
    // for cash-settled index options whose sell-side OCA orders are rejected by IBKR.
    // Returns the accepted stop order ID, or null if both attempts are rejected.
    private async Task<string?> PlaceTrailWithFallbackAsync(
        string symbol,
        string entryOrderId,
        int trailStopId,
        Contract contract,
        int quantity,
        double trailPercent,
        CancellationToken ct)
    {
        var ocaGroup = $"OCA_{trailStopId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var trailOrder = BuildOcaTrailOrder(trailStopId, quantity, trailPercent, ocaGroup);

        var rejectionTcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Wrapper.RegisterStopRejectionCallback(trailStopId, rejectionTcs);
        _connection.Client.placeOrder(trailStopId, contract, trailOrder);

        string? rejection = null;
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromMilliseconds(600));
            rejection = await rejectionTcs.Task.WaitAsync(checkCts.Token);
        }
        catch (OperationCanceledException)
        {
            // No rejection, OCA trail accepted
            _connection.Wrapper.UnregisterStopRejectionCallback(trailStopId);
        }

        if (rejection is null)
        {
            RegisterTrailCallbacks(entryOrderId, trailStopId);
            return trailStopId.ToString();
        }

        // "both sides" is a timing race, IBKR hasn't fully cleared its open-order
        // state after the temp bracket stop was cancelled. A short pause before
        // retrying as standalone gives IBKR time to reconcile.
        if (rejection.Contains("both sides", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(500, ct);

        _logger.LogDebug(
            "OCA trail stop rejected for {Symbol} StopId {StopId} — retrying as standalone trail. Reason: {Reason}",
            symbol, trailStopId, rejection);

        var standaloneId = GetNextOrderId();
        var standalone = BuildStandaloneTrailOrder(standaloneId, quantity, trailPercent);
        var standaloneTcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Wrapper.RegisterStopRejectionCallback(standaloneId, standaloneTcs);
        _connection.Client.placeOrder(standaloneId, contract, standalone);

        string? standaloneRejection = null;
        try
        {
            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            retryCts.CancelAfter(TimeSpan.FromMilliseconds(600));
            standaloneRejection = await standaloneTcs.Task.WaitAsync(retryCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Standalone accepted
            _connection.Wrapper.UnregisterStopRejectionCallback(standaloneId);
        }

        if (standaloneRejection is null)
        {
            RegisterTrailCallbacks(entryOrderId, standaloneId);
            _logger.LogDebug(
                "Standalone trail stop placed for {Symbol} StopId {StopId} (OCA fallback).",
                symbol, standaloneId);
            return standaloneId.ToString();
        }

        _logger.LogError(
            "All trail stop attempts rejected for {Symbol} — position is unprotected. " +
            "Reason: {Reason}",
            symbol, standaloneRejection);

        return null;
    }

    // Registers exec callbacks for a trail stop using a string entry order ID.
    // String version required for ReplaceTrailStopAsync where entryOrderId is a string.
    private void RegisterTrailCallbacks(string entryOrderId, int stopId)
    {
        lock (_stopMapLock)
        {
            _stopOrderMap[stopId] = (entryOrderId, TradeOutcome.StoppedOut);
        }

        _connection.Wrapper.RegisterExecDetailsCallback(stopId, fillPrice =>
            OnStopOrderFilled(stopId, fillPrice));
    }

    private static Order BuildCloseOrder(int orderId, int quantity) =>
        new()
        {
            OrderId = orderId,
            Action = "SELL",
            OrderType = "MKT",
            TotalQuantity = quantity,
            Transmit = true,
        };

    private static BrokerOrderResult FailedResult(string reason) =>
        new(
            OrderId:       "FAILED",
            StopOrderId:   null,
            TargetOrderId: null,
            FillPrice:     0m,
            FillQuantity:  0,
            FillAmount:    0m,
            Status:        OrderStatus.Rejected,
            FilledAt:      DateTimeOffset.UtcNow);
}