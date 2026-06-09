using IBApi;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

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

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public IbkrBrokerService(
        IbkrConnectionService connection,
        IOptions<IbkrOptions> options,
        ILogger<IbkrBrokerService> logger)
    {
        _connection = connection;
        _options    = options.Value;
        _logger     = logger;
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

            _logger.LogInformation(
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
        var tcs   = _connection.Wrapper.RegisterAccountCallback(reqId);
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
        var tcs   = _connection.Wrapper.RegisterMarketDataCallback(reqId);

        var contract = tradeType == TradeType.Options
            ? new Contract
            {
                Symbol      = symbol,
                SecType     = "OPT",
                Exchange    = "SMART",
                Currency    = "USD",
                Right       = direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike      = (double)(strike ?? 0),
                LastTradeDateOrContractMonth =
                    expiration is not null
                        ? DateTimeOffset.Parse(expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier  = "100",
            }
            : new Contract
            {
                Symbol   = symbol,
                SecType  = "STK",
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
    /// Used by <see cref="TradeGuard"/> to calculate current exposure before new orders.
    /// </summary>
    public async Task<decimal> GetOpenPositionsValueAsync(CancellationToken ct = default)
    {
        if (!EnsureConnected()) return 0m;

        var reqId = NextReqId();
        var tcs   = _connection.Wrapper.RegisterAccountCallback(reqId);
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
    /// Places a bracket entry order then, once confirmed filled, replaces the fixed stop with
    /// an OCA group containing a TRAIL stop. The LMT target order is omitted until IBKR Level 3
    /// options approval is granted, Level 2 rejects LMT sell orders in OCA groups.
    /// On timeout, waits an additional 10 seconds for a late ExecDetails callback before
    /// giving up, prevents ghost trades when the fill arrives after the timeout fires.
    /// Partial fills are handled by reading FilledQuantity from the orderStatus callback (normal
    /// path) or reqPositions (late fill path) so the trail stop always matches the actual position.
    /// </summary>
    public async Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var orderId    = GetNextOrderId();
        var tcs        = _connection.Wrapper.RegisterOrderCallback(orderId);
        var contract   = BuildContract(order);
        var entryOrder = BuildMarketOrder(orderId, order.Quantity, "BUY");

        // Register exec details TCS before placing, catches late fills that arrive
        // after the 15s timeout fires and the order cancel is sent to IBKR.
        var lateExecTcs = _connection.Wrapper.RegisterExecDetailsTcsCallback(orderId);

        // Place entry with a temporary fixed STP as bracket child so there is always
        // stop protection in place while we wait for the actual fill confirmation.
        var tempStopId    = orderId + 1;
        var minTick       = order.TradeType == TradeType.Options ? 0.05 : 0.01;
        var roundedStop   = Math.Round(Math.Round((double)order.StopPrice / minTick) * minTick, 2);
        var tempStopOrder = BuildStopOrder(tempStopId, orderId, order.Quantity, roundedStop);

        entryOrder.Transmit    = false;
        tempStopOrder.Transmit = true;

        try
        {
            _connection.Client.placeOrder(orderId, contract, entryOrder);
            _connection.Client.placeOrder(tempStopId, contract, tempStopOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            // Only resolves when IBKR confirms status "Filled".
            var state = await tcs.Task.WaitAsync(cts.Token);

            // Normal fill
            _connection.Wrapper.UnregisterExecDetailsTcsCallback(orderId);

            _logger.LogInformation(
                "IBKR entry filled. OrderId: {OrderId} Status: {Status} — replacing stop with OCA trail",
                orderId, state.Status);

            _connection.Client.cancelOrder(tempStopId);
            await Task.Delay(300, ct);

            // Use FilledQuantity from orderStatus, may be less than order.Quantity on a partial
            // fill. Trail stop must match actual position size to avoid overselling into a short.
            var fillPrice  = state.AvgFillPrice > 0 ? state.AvgFillPrice : order.EstimatedEntryPrice;
            var fillQty    = state.FilledQuantity > 0 ? state.FilledQuantity : order.Quantity;
            var multiplier = order.TradeType == TradeType.Options ? 100m : 1m;

            var ocaGroup    = $"OCA_{orderId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var trailStopId = orderId + 2;
            var trailOrder  = BuildOcaTrailOrder(trailStopId, fillQty, order.TrailPercent, ocaGroup);

            _connection.Client.placeOrder(trailStopId, contract, trailOrder);

            // TODO: re-add LMT target order to OCA group once IBKR Level 3 options
            // approval is granted (~July 2026). Removed due to Level 2 restriction.
            // var targetOrderId = orderId + 3;
            // var targetOrder   = BuildOcaLimitOrder(targetOrderId, fillQty,
            //                         Math.Round((double)order.TargetPrice, 2), ocaGroup);
            // _connection.Client.placeOrder(targetOrderId, contract, targetOrder);

            _logger.LogInformation(
                "IBKR OCA group placed — Qty: {Qty} Trail: {TrailPct}% | Target: none (Level 2 restriction) | OCA: {Oca}",
                fillQty, order.TrailPercent, ocaGroup);

            RegisterStopOrderCallbacks(orderId, trailStopId, null);

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   trailStopId.ToString(),
                TargetOrderId: null,
                FillPrice:     fillPrice,
                FillQuantity:  fillQty,
                FillAmount:    fillPrice * fillQty * multiplier,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR PlaceOrder timed out for {Symbol} after 15s — sending cancel, checking for late fill.",
                order.Symbol);

            _connection.Client.cancelOrder(orderId);
            _connection.Client.cancelOrder(tempStopId);
            _connection.Wrapper.UnregisterOrderCallback(orderId);

            // Wait up to 10 seconds for a late ExecDetails callback, the fill may have
            // already reached IBKR before the cancel arrived, creating a ghost trade.
            try
            {
                using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execCts.CancelAfter(TimeSpan.FromSeconds(10));

                var lateFillPrice = await lateExecTcs.Task.WaitAsync(execCts.Token);

                _logger.LogInformation(
                    "IBKR PlaceOrder late fill detected for {Symbol} @ ${Price:F2} — verifying actual position qty.",
                    order.Symbol, lateFillPrice);

                await Task.Delay(300, ct);

                // ExecDetails only provides price, use reqPositions to verify actual filled
                // quantity since late fills may be partial and trail stop must match position size.
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

                var ocaGroup    = $"OCA_{orderId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var trailStopId = orderId + 2;
                var trailOrder  = BuildOcaTrailOrder(trailStopId, actualLateFillQty, order.TrailPercent, ocaGroup);

                _connection.Client.placeOrder(trailStopId, contract, trailOrder);

                // TODO: re-add LMT target order to OCA group once IBKR Level 3 options
                // approval is granted (~July 2026). Removed due to Level 2 restriction.
                // var targetOrderId = orderId + 3;
                // var targetOrder   = BuildOcaLimitOrder(targetOrderId, actualLateFillQty,
                //                         Math.Round((double)order.TargetPrice, 2), ocaGroup);
                // _connection.Client.placeOrder(targetOrderId, contract, targetOrder);

                _logger.LogInformation(
                    "IBKR OCA group placed for late fill — Qty: {Qty} Trail: {TrailPct}% | Target: none (Level 2 restriction) | OCA: {Oca}",
                    actualLateFillQty, order.TrailPercent, ocaGroup);

                RegisterStopOrderCallbacks(orderId, trailStopId, null);

                var multiplier = order.TradeType == TradeType.Options ? 100m : 1m;

                return new BrokerOrderResult(
                    OrderId:       orderId.ToString(),
                    StopOrderId:   trailStopId.ToString(),
                    TargetOrderId: null,
                    FillPrice:     lateFillPrice,
                    FillQuantity:  actualLateFillQty,
                    FillAmount:    lateFillPrice * actualLateFillQty * multiplier,
                    Status:        OrderStatus.Filled,
                    FilledAt:      DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                // No late fill arrived within 10s — order was truly cancelled
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
        var tcs          = _connection.Wrapper.RegisterOrderCallback(closeOrderId);
        var execTcs      = _connection.Wrapper.RegisterExecDetailsTcsCallback(closeOrderId);
        var contract     = BuildCloseContract(trade);

        var partialCloseOrder = new Order
        {
            OrderId       = closeOrderId,
            Action        = "SELL",
            OrderType     = "MKT",
            TotalQuantity = quantityToClose,
            Transmit      = true,
        };

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, partialCloseOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var fill       = await tcs.Task.WaitAsync(cts.Token);
            var fillPrice  = fill.AvgFillPrice > 0 ? fill.AvgFillPrice : trade.EntryPrice;
            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

            _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);

            _logger.LogInformation(
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
                var multiplier    = trade.TradeType == TradeType.Options ? 100m : 1m;

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

        _logger.LogInformation(
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

        var closeOrderId = GetNextOrderId();
        var tcs          = _connection.Wrapper.RegisterOrderCallback(closeOrderId);

        // Register exec details TCS before placing — catches the fill even if orderStatus times out
        var execTcs    = _connection.Wrapper.RegisterExecDetailsTcsCallback(closeOrderId);
        var contract   = BuildCloseContract(trade);
        var closeOrder = BuildCloseOrder(closeOrderId, trade);

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, closeOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var fill       = await tcs.Task.WaitAsync(cts.Token);
            var fillPrice  = fill.AvgFillPrice > 0 ? fill.AvgFillPrice : trade.EntryPrice;
            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;

            _connection.Wrapper.UnregisterExecDetailsTcsCallback(closeOrderId);

            _logger.LogInformation(
                "IBKR position closed. OrderId: {OrderId} Symbol: {Symbol} Status: {Status} FillPrice: {Price:F2}",
                closeOrderId, trade.Symbol, fill.Status, fillPrice);

            return new BrokerOrderResult(
                OrderId:       closeOrderId.ToString(),
                StopOrderId:   null,
                TargetOrderId: null,
                FillPrice:     fillPrice,
                FillQuantity:  trade.Quantity,
                FillAmount:    fillPrice * trade.Quantity * multiplier,
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
                var multiplier    = trade.TradeType == TradeType.Options ? 100m : 1m;

                _logger.LogInformation(
                    "IBKR ClosePosition recovered fill via execDetails — Symbol: {Symbol} FillPrice: {Price:F2}",
                    trade.Symbol, execFillPrice);

                return new BrokerOrderResult(
                    OrderId:       closeOrderId.ToString(),
                    StopOrderId:   null,
                    TargetOrderId: null,
                    FillPrice:     execFillPrice,
                    FillQuantity:  trade.Quantity,
                    FillAmount:    execFillPrice * trade.Quantity * multiplier,
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

        // Ensure the first issued ID exceeds both Gateway's counter and the DB high-water mark.
        // GetNextOrderId() adds 10, so target is set 10 below the safe floor.
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

        var reqId    = NextReqId();
        var tcs      = _connection.Wrapper.RegisterHistoricalDataCallback(reqId);
        var contract = symbol == "VIX"
            ? new Contract
            {
                Symbol   = "VIX",
                SecType  = "IND",
                Exchange = "CBOE",
                Currency = "USD",
            }
            : new Contract
            {
                Symbol   = symbol,
                SecType  = "STK",
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

    // -- Helpers --

    // Registers exec detail callbacks for the trail stop and optionally the target OCA order.
    // When either fires, routes the fill to PositionMonitorService via the broker fill handler.
    // targetOrderId is null when no target was placed (Level 2 restriction or 0DTE).
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

    // Called by IbkrEWrapper when a stop or target order fills broker-side.
    // Looks up the parent trade and fires the broker fill handler on a background thread
    // to avoid blocking the EWrapper callback thread.
    private void OnStopOrderFilled(int stopOrderId, decimal fillPrice)
    {
        (string EntryOrderId, TradeOutcome Outcome) mapping;

        lock (_stopMapLock)
        {
            if (!_stopOrderMap.TryGetValue(stopOrderId, out mapping))
                return;

            _stopOrderMap.Remove(stopOrderId);
        }

        _logger.LogInformation(
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

    private static Contract BuildContract(TradeOrder order)
    {
        if (order.TradeType == TradeType.Options)
        {
            return new Contract
            {
                Symbol      = order.Symbol,
                SecType     = "OPT",
                Exchange    = "SMART",
                Currency    = "USD",
                Right       = order.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike      = (double)(order.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    order.Expiration is not null
                        ? DateTimeOffset.Parse(order.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier  = "100",
            };
        }

        return new Contract
        {
            Symbol   = order.Symbol,
            SecType  = "STK",
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
                Symbol      = trade.Symbol,
                SecType     = "OPT",
                Exchange    = "SMART",
                Currency    = "USD",
                Right       = trade.Direction?.ToUpper() == "CALL" ? "C" : "P",
                Strike      = (double)(trade.Strike ?? 0),
                LastTradeDateOrContractMonth =
                    trade.Expiration is not null
                        ? DateTimeOffset.Parse(trade.Expiration).ToString("yyyyMMdd")
                        : string.Empty,
                Multiplier  = "100",
            };
        }

        return new Contract
        {
            Symbol   = trade.Symbol,
            SecType  = "STK",
            Exchange = "SMART",
            Currency = "USD",
        };
    }

    private static Order BuildMarketOrder(int orderId, int quantity, string action) =>
        new()
        {
            OrderId       = orderId,
            Action        = action,
            OrderType     = "MKT",
            TotalQuantity = quantity,
            Transmit      = false,
        };

    // Temporary bracket stop — placed atomically with the entry to ensure immediate protection.
    // Cancelled and replaced with OCA trail stop once the entry fill is confirmed.
    private static Order BuildStopOrder(int orderId, int parentId, int quantity, double stopPrice) =>
        new()
        {
            OrderId       = orderId,
            ParentId      = parentId,
            Action        = "SELL",
            OrderType     = "STP",
            AuxPrice      = stopPrice,
            TotalQuantity = quantity,
            Transmit      = false,
        };

    // Standalone trailing stop in an OCA group — no ParentId so IBKR accepts TRAIL type.
    private static Order BuildOcaTrailOrder(int orderId, int quantity, double trailPercent, string ocaGroup) =>
        new()
        {
            OrderId         = orderId,
            Action          = "SELL",
            OrderType       = "TRAIL",
            TrailingPercent = trailPercent,
            TotalQuantity   = quantity,
            OcaGroup        = ocaGroup,
            OcaType         = 1,
            Tif             = "GTC",
            Transmit        = true,
        };

    // TODO: restore when IBKR Level 3 options approval is granted (~July 2026)
    // private static Order BuildOcaLimitOrder(int orderId, int quantity, double limitPrice, string ocaGroup) =>
    //     new()
    //     {
    //         OrderId       = orderId,
    //         Action        = "SELL",
    //         OrderType     = "LMT",
    //         LmtPrice      = limitPrice,
    //         TotalQuantity = quantity,
    //         OcaGroup      = ocaGroup,
    //         OcaType       = 1,
    //         Tif           = "GTC",
    //         Transmit      = true,
    //     };

    private static Order BuildCloseOrder(int orderId, TradeRecord trade) =>
        new()
        {
            OrderId       = orderId,
            Action        = "SELL",
            OrderType     = "MKT",
            TotalQuantity = trade.Quantity,
            Transmit      = true,
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