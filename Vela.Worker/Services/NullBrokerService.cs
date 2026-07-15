using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// No-op broker used during development and testing. Logs what it would do
/// and returns simulated fills without placing any real orders.
/// </summary>
public class NullBrokerService : IBrokerService
{
    private readonly ILogger<NullBrokerService> _logger;

    public NullBrokerService(ILogger<NullBrokerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Simulates placing a bracket order and returns a fake fill at the alert price.
    /// </summary>
    public Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        var orderType = order.LimitPrice.HasValue ? $"LMT ${order.LimitPrice:F2}" : "MKT";

        _logger.LogInformation(
            "[NullBroker] PLACE ORDER — {Type} {Symbol} {Direction} " +
            "× {Quantity} @ ~${Entry:F2} ({OrderType}) | Stop: ${Stop:F2} | Target: ${Target:F2} | Budget: ${Budget:F2}",
            order.TradeType,
            order.Symbol,
            order.Direction ?? "—",
            order.Quantity,
            order.EstimatedEntryPrice,
            orderType,
            order.StopPrice,
            order.TargetPrice,
            order.BudgetUsed);

        var result = new BrokerOrderResult(
            OrderId:       $"NULL-{Guid.NewGuid():N}"[..12],
            StopOrderId:   $"NULL-STOP-{Guid.NewGuid():N}"[..16],
            TargetOrderId: $"NULL-TGT-{Guid.NewGuid():N}"[..15],
            FillPrice:     order.EstimatedEntryPrice,
            FillQuantity:  order.Quantity,
            FillAmount:    order.BudgetUsed,
            Status:        OrderStatus.Simulated,
            FilledAt:      DateTimeOffset.UtcNow);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns a simulated position at 10% above entry with the full recorded quantity.
    /// </summary>
    public Task<(decimal Price, int Quantity)> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default)
    {
        var simulatedPrice = trade.EntryPrice * 1.10m;
        _logger.LogDebug(
            "[NullBroker] GetCurrentPositionPrice {Symbol} -> ${Price:F2} x{Qty} simulated",
            trade.Symbol, simulatedPrice, trade.Quantity);
        return Task.FromResult((simulatedPrice, trade.Quantity));
    }

    /// <summary>
    /// Returns an empty snapshot with TimedOut=false, confirming no positions in simulation.
    /// StartupReconciliationService will proceed normally, DB positions (if any) are treated
    /// as ghosts since no IBKR account is available to verify against.
    /// </summary>
    public Task<PositionsSnapshot> GetAllPositionsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[NullBroker] GetAllPositions -> empty snapshot (simulation)");
        return Task.FromResult(new PositionsSnapshot([], false));
    }

    /// <summary>
    /// Returns an empty snapshot with TimedOut=false. No orders in simulation.
    /// </summary>
    public Task<OrdersSnapshot> GetAllOpenOrdersAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[NullBroker] GetAllOpenOrders -> empty snapshot (simulation)");
        return Task.FromResult(new OrdersSnapshot([], false));
    }

    /// <summary>
    /// Always returns false, NullBrokerService places no real orders so none are tracked.
    /// </summary>
    public bool IsKnownOrder(int orderId) => false;

    /// <summary>
    /// Returns zero, 0% slippage in simulation.
    /// </summary>
    public Task<decimal> GetCurrentMarketPriceAsync(
        string symbol,
        TradeType tradeType,
        string? direction = null,
        decimal? strike = null,
        string? expiration = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[NullBroker] GetCurrentMarketPrice {Symbol} -> alerted price (0% slippage simulated)",
            symbol);
        return Task.FromResult(0m);
    }

    /// <summary>
    /// No-op partial close for testing, returns a simulated fill at entry price.
    /// </summary>
    public Task<BrokerOrderResult> PartialCloseAsync(
        TradeRecord trade,
        int quantityToClose,
        CancellationToken ct = default) =>
        Task.FromResult(new BrokerOrderResult(
            OrderId:       "NULL",
            StopOrderId:   null,
            TargetOrderId: null,
            FillPrice:     trade.EntryPrice,
            FillQuantity:  quantityToClose,
            FillAmount:    trade.EntryPrice * quantityToClose * 100m,
            Status:        OrderStatus.Filled,
            FilledAt:      DateTimeOffset.UtcNow));

    /// <summary>
    /// No-op trail stop replacement for testing. Returns a fake new stop ID.
    /// </summary>
    public Task<string?> ReplaceTrailStopAsync(
        string existingStopOrderId,
        int quantity,
        TradeOrder order,
        double newTrailPercent,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[NullBroker] ReplaceTrailStop {Symbol} — {OldId} trail: {Trail}% (simulated)",
            order.Symbol, existingStopOrderId, newTrailPercent);
        return Task.FromResult<string?>($"NULL-TRAIL-{Guid.NewGuid():N}"[..16]);
    }

    /// <summary>
    /// No-op protective stop placement for testing. Always returns null.
    /// </summary>
    public Task<string?> PlaceProtectiveStopAsync(
        string symbol,
        TradeType tradeType,
        string? optionsContractSymbol,
        string? direction,
        decimal? strike,
        string? expiration,
        int quantity,
        double trailPercent,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[NullBroker] PlaceProtectiveStop {Symbol} — trail: {Trail}% (simulated, returns null)",
            optionsContractSymbol ?? symbol, trailPercent);
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// No-op order cancellation for testing.
    /// </summary>
    public Task CancelOrderAsync(int orderId, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Simulates closing a position and returns a fake fill at 10% above entry.
    /// </summary>
    public Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NullBroker] CLOSE POSITION — {Symbol} × {Quantity} | Outcome: {Outcome}",
            trade.Symbol, trade.Quantity, outcome);

        return Task.FromResult(new BrokerOrderResult(
            OrderId:       $"NULL-CLOSE-{Guid.NewGuid():N}"[..17],
            StopOrderId:   null,
            TargetOrderId: null,
            FillPrice:     trade.EntryPrice * 1.10m,
            FillQuantity:  trade.Quantity,
            FillAmount:    trade.EntryAmount * 1.10m,
            Status:        OrderStatus.Simulated,
            FilledAt:      DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Returns a simulated account balance of $100,000.
    /// </summary>
    public Task<decimal> GetAccountBalanceAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[NullBroker] GetAccountBalance -> $100,000 simulated");
        return Task.FromResult(100_000m);
    }

    /// <summary>
    /// Returns a simulated open positions value of $0.
    /// </summary>
    public Task<decimal> GetOpenPositionsValueAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[NullBroker] GetOpenPositionsValue -> $0 simulated");
        return Task.FromResult(0m);
    }

    /// <summary>
    /// No-op, NullBrokerService never fires broker-side fills.
    /// </summary>
    public void RegisterBrokerFillHandler(Action<string, decimal, TradeOutcome> handler) { }

    /// <summary>
    /// Returns an empty list, no Gateway available in simulation.
    /// MarketConditionsLogger falls back to Yahoo Finance when this returns empty.
    /// </summary>
    public Task<List<HistoricalBar>> GetHistoricalBarsAsync(
        string symbol,
        int barCount,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[NullBroker] GetHistoricalBars {Symbol} -> empty (simulation)", symbol);
        return Task.FromResult(new List<HistoricalBar>());
    }

    /// <summary>
    /// Returns a zeroed snapshot, no Gateway available in simulation.
    /// </summary>
    public Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[NullBroker] GetAccountSnapshot -> zeroed snapshot (simulation)");
        return Task.FromResult(new AccountSnapshot(0, 0, 0, 0, false));
    }
}