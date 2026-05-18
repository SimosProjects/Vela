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
                _logger.LogInformation("IBKR account balance: ${Balance:F2}", balance);
                return balance;
            }

            _logger.LogWarning("IBKR GetAccountBalance could not parse value: {Value}", valueStr);
            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetAccountBalance timed out");
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Returns the current average cost of an open position from IBKR position data.
    /// Used by PositionMonitorService to check stop and target thresholds.
    /// </summary>
    public async Task<decimal> GetCurrentPositionPriceAsync(TradeRecord trade, CancellationToken ct = default)
    {
        if (!EnsureConnected()) return 0m;

        var key = trade.TradeType == TradeType.Options
            ? $"{trade.Symbol}::{trade.OptionsContract}"
            : $"{trade.Symbol}::STK";

        var tcs = _connection.Wrapper.RegisterPositionCallback(key);
        _connection.Client.reqPositions();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var price = await tcs.Task.WaitAsync(cts.Token);
            _logger.LogDebug("IBKR current price for {Symbol}: ${Price:F2}", trade.Symbol, price);
            return price;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetCurrentPositionPrice timed out for {Symbol}.", trade.Symbol);
            return 0m;
        }
        finally
        {
            _connection.Client.cancelPositions();
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
        var tcs = _connection.Wrapper.RegisterAccountCallback(reqId);
        _connection.Client.reqAccountSummary(reqId, "All", "GrossPositionValue");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);
            var valueStr = await tcs.Task.WaitAsync(cts.Token);

            if (decimal.TryParse(valueStr, out var value))
            {
                _logger.LogInformation("IBKR open positions value: ${Value:F2}", value);
                return value;
            }

            return 0m;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IBKR GetOpenPositionsValue timed out");
            return 0m;
        }
        finally
        {
            _connection.Client.cancelAccountSummary(reqId);
        }
    }

    /// <summary>
    /// Places a bracket entry order then, once filled, replaces the fixed stop with
    /// an OCA group containing a TRAIL stop and LMT target. This gives trailing stop
    /// protection while ensuring the target and stop cancel each other automatically.
    /// </summary>
    public async Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        var orderId   = GetNextOrderId();
        var tcs       = _connection.Wrapper.RegisterOrderCallback(orderId);
        var contract  = BuildContract(order);
        var entryOrder = BuildMarketOrder(orderId, order.Quantity, "BUY");

        // Place entry with a temporary fixed STP as bracket child so there is always
        // stop protection in place while we wait for the fill confirmation.
        var tempStopId    = orderId + 1;
        var tempStopOrder = BuildStopOrder(
            tempStopId, orderId, order.Quantity,
            Math.Round((double)order.StopPrice, 2));

        // Transmit=false on entry and stop, true on stop triggers atomic bracket submission
        entryOrder.Transmit    = false;
        tempStopOrder.Transmit = true;

        try
        {
            _connection.Client.placeOrder(orderId, contract, entryOrder);
            _connection.Client.placeOrder(tempStopId, contract, tempStopOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var state = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogInformation(
                "IBKR entry filled. OrderId: {OrderId} Status: {Status} — replacing stop with OCA trail+target",
                orderId, state.Status);

            // Cancel the temporary fixed stop and replace with OCA trail + limit target.
            // OCA ensures whichever side triggers first automatically cancels the other.
            _connection.Client.cancelOrder(tempStopId);
            await Task.Delay(300, ct); // brief pause for Gateway to process the cancel

            var ocaGroup      = $"OCA_{orderId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var trailStopId   = orderId + 2;
            var targetOrderId = orderId + 3;

            var trailPercent  = order.TradeType == TradeType.Options ? 50.0 : 15.0;
            var trailOrder    = BuildOcaTrailOrder(trailStopId, order.Quantity, trailPercent, ocaGroup);
            var targetOrder   = BuildOcaLimitOrder(
                targetOrderId, order.Quantity,
                Math.Round((double)order.TargetPrice, 2), ocaGroup);

            _connection.Client.placeOrder(trailStopId, contract, trailOrder);
            _connection.Client.placeOrder(targetOrderId, contract, targetOrder);

            _logger.LogInformation(
                "IBKR OCA group placed — Trail: {TrailPct}% | Target: ${Target:F2} | OCA: {Oca}",
                trailPercent, order.TargetPrice, ocaGroup);

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   trailStopId.ToString(),
                TargetOrderId: targetOrderId.ToString(),
                FillPrice:     order.EstimatedEntryPrice,
                FillQuantity:  order.Quantity,
                FillAmount:    order.BudgetUsed,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR PlaceOrder timed out for {Symbol}. Order may still be pending.",
                order.Symbol);

            return new BrokerOrderResult(
                OrderId:       orderId.ToString(),
                StopOrderId:   (orderId + 1).ToString(),
                TargetOrderId: (orderId + 2).ToString(),
                FillPrice:     order.EstimatedEntryPrice,
                FillQuantity:  order.Quantity,
                FillAmount:    order.BudgetUsed,
                Status:        OrderStatus.Pending,
                FilledAt:      DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Cancels any active stop and target orders then places a market close order.
    /// </summary>
    public async Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken ct = default)
    {
        if (!EnsureConnected())
            return FailedResult("Not connected to IB Gateway");

        if (trade.StopOrderId is not null && int.TryParse(trade.StopOrderId, out var stopId))
        {
            _connection.Client.cancelOrder(stopId);
            _logger.LogInformation(
                "IBKR cancelled stop order {OrderId} for {Symbol}", stopId, trade.Symbol);
        }

        if (trade.TargetOrderId is not null && int.TryParse(trade.TargetOrderId, out var targetId))
        {
            _connection.Client.cancelOrder(targetId);
            _logger.LogInformation(
                "IBKR cancelled target order {OrderId} for {Symbol}", targetId, trade.Symbol);
        }

        await Task.Delay(500, ct);

        var closeOrderId = GetNextOrderId();
        var tcs          = _connection.Wrapper.RegisterOrderCallback(closeOrderId);
        var contract     = BuildCloseContract(trade);
        var closeOrder   = BuildCloseOrder(closeOrderId, trade);

        try
        {
            _connection.Client.placeOrder(closeOrderId, contract, closeOrder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.TimeoutMs);

            var state = await tcs.Task.WaitAsync(cts.Token);

            _logger.LogInformation(
                "IBKR position closed. OrderId: {OrderId} Symbol: {Symbol} Status: {Status}",
                closeOrderId, trade.Symbol, state.Status);

            var estimatedFill = trade.ExitPrice ?? trade.EntryPrice;
            var multiplier    = trade.TradeType == TradeType.Options ? 100m : 1m;

            return new BrokerOrderResult(
                OrderId:       closeOrderId.ToString(),
                StopOrderId:   null,
                TargetOrderId: null,
                FillPrice:     estimatedFill,
                FillQuantity:  trade.Quantity,
                FillAmount:    estimatedFill * trade.Quantity * multiplier,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "IBKR ClosePosition timed out for {Symbol}. Close order may still be pending.",
                trade.Symbol);

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

    // Connects to IB Gateway if not already connected
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
    private int GetNextOrderId() => Interlocked.Add(ref _nextReqId, 10);

    /// <summary>
    /// Syncs the internal order ID counter from Gateway's next valid order ID.
    /// Called on startup to prevent order ID collisions across Worker restarts.
    /// </summary>
    public void SyncOrderId()
    {
        var gatewayId = _connection.Wrapper.NextValidOrderId;
        if (gatewayId > _nextReqId)
            Interlocked.Exchange(ref _nextReqId, gatewayId);
    }

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
    // Cancelled and replaced with OCA trail+target once the entry fill is confirmed.
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
    // OcaGroup ties it to the limit target so whichever triggers first cancels the other.
    private static Order BuildOcaTrailOrder(int orderId, int quantity, double trailPercent, string ocaGroup) =>
        new()
        {
            OrderId          = orderId,
            Action           = "SELL",
            OrderType        = "TRAIL",
            TrailingPercent  = trailPercent,
            TotalQuantity    = quantity,
            OcaGroup         = ocaGroup,
            OcaType          = 1, // cancel all remaining orders with block
            Transmit         = false,
        };

    // Limit target in the same OCA group as the trail stop.
    private static Order BuildOcaLimitOrder(int orderId, int quantity, double limitPrice, string ocaGroup) =>
        new()
        {
            OrderId       = orderId,
            Action        = "SELL",
            OrderType     = "LMT",
            LmtPrice      = limitPrice,
            TotalQuantity = quantity,
            OcaGroup      = ocaGroup,
            OcaType       = 1,
            Transmit      = true,
        };

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