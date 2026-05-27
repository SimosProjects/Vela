using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Abstraction over a broker API. Active implementation switches from
/// <see cref="NullBrokerService"/> during testing to <see cref="IbkrBrokerService"/>
/// for paper and live trading.
/// </summary>
public interface IBrokerService
{
    /// <summary>
    /// Places a bracket order consisting of a market entry, stop loss, and profit target.
    /// </summary>
    Task<BrokerOrderResult> PlaceOrderAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current market price of an open position from IBKR position data.
    /// Used by PositionMonitorService to evaluate stop and target thresholds.
    /// </summary>
    Task<decimal> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current market price for any symbol using a snapshot quote.
    /// Used by BrokerExecutionService for pre-trade slippage checks before placing orders.
    /// Returns 0 if the quote cannot be retrieved within the timeout.
    /// </summary>
    Task<decimal> GetCurrentMarketPriceAsync(
        string symbol,
        TradeType tradeType,
        string? direction = null,
        decimal? strike = null,
        string? expiration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels pending stop and target orders then places a market close order.
    /// </summary>
    Task<BrokerOrderResult> ClosePositionAsync(
        TradeRecord trade,
        TradeOutcome outcome,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the net liquidation value of the account.
    /// Used by TradeGuard for exposure checks before placing orders.
    /// </summary>
    Task<decimal> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total market value of all open positions.
    /// Used by TradeGuard to calculate current exposure before new orders.
    /// </summary>
    Task<decimal> GetOpenPositionsValueAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes a handler that fires when a broker-side stop or target order fills.
    /// The handler receives the entry order ID, fill price, and trade outcome.
    /// </summary>
    void RegisterBrokerFillHandler(Action<string, decimal, TradeOutcome> handler);
}