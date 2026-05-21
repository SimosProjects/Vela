using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// No-op broker used during development and testing. Logs what it would do
/// and returns simulated fills without placing any real orders.
/// Swap for <see cref="IbkrBrokerService"/> in Program.cs when IBKR is ready.
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
        var orderId = $"NULL-{Guid.NewGuid():N}"[..12];

        _logger.LogInformation(
            "[NullBroker] PLACE ORDER — {Type} {Symbol} {Direction} " +
            "× {Quantity} @ ~${Entry:F2} | Stop: ${Stop:F2} | Target: ${Target:F2} | Budget: ${Budget:F2}",
            order.TradeType,
            order.Symbol,
            order.Direction ?? "—",
            order.Quantity,
            order.EstimatedEntryPrice,
            order.StopPrice,
            order.TargetPrice,
            order.BudgetUsed);

        var result = new BrokerOrderResult(
            OrderId: orderId,
            StopOrderId: $"NULL-STOP-{Guid.NewGuid():N}"[..16],
            TargetOrderId: $"NULL-TGT-{Guid.NewGuid():N}"[..15],
            FillPrice: order.EstimatedEntryPrice,
            FillQuantity: order.Quantity,
            FillAmount: order.BudgetUsed,
            Status: OrderStatus.Simulated,
            FilledAt: DateTimeOffset.UtcNow);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns a simulated current price at 10% above entry for testing position monitoring.
    /// </summary>
    public Task<decimal> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default)
    {
        var simulatedPrice = trade.EntryPrice * 1.10m;
        _logger.LogDebug(
            "[NullBroker] GetCurrentPositionPrice {Symbol} → ${Price:F2} simulated",
            trade.Symbol, simulatedPrice);
        return Task.FromResult(simulatedPrice);
    }

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
            trade.Symbol,
            trade.Quantity,
            outcome);

        var simulatedExitPrice  = trade.EntryPrice * 1.10m;
        var simulatedExitAmount = trade.EntryAmount * 1.10m;

        var result = new BrokerOrderResult(
            OrderId: $"NULL-CLOSE-{Guid.NewGuid():N}"[..17],
            StopOrderId: null,
            TargetOrderId: null,
            FillPrice: simulatedExitPrice,
            FillQuantity: trade.Quantity,
            FillAmount: simulatedExitAmount,
            Status: OrderStatus.Simulated,
            FilledAt: DateTimeOffset.UtcNow);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns a simulated account balance of $100,000.
    /// </summary>
    public Task<decimal> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[NullBroker] GetAccountBalance → $100,000 simulated");
        return Task.FromResult(100_000m);
    }

    /// <summary>
    /// Returns a simulated open positions value of $0.
    /// </summary>
    public Task<decimal> GetOpenPositionsValueAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[NullBroker] GetOpenPositionsValue → $0 simulated");
        return Task.FromResult(0m);
    }

    /// <summary>
    /// No-op — NullBrokerService never fires broker-side fills.
    /// </summary>
    public void RegisterBrokerFillHandler(Action<string, decimal, TradeOutcome> handler)
    {
        // No broker-side fills in simulation
    }
}