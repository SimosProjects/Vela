using Vela.Worker.Models;

namespace Vela.Worker.Services;

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
    /// Returns the current average cost and actual held quantity of an open position from IBKR.
    /// Used by BrokerExecutionService to verify a position exists after an entry timeout,
    /// and by PositionMonitorService to check stop and target thresholds.
    /// A quantity of zero or negative means no valid long position — caller must not record the trade.
    /// Returns (0, 0) if the broker is unavailable or times out.
    /// </summary>
    Task<(decimal Price, int Quantity)> GetCurrentPositionPriceAsync(
        TradeRecord trade,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of all positions currently held in the IBKR account.
    /// TimedOut=true means Gateway did not respond — the caller must not treat an empty
    /// position list as confirmation the account is flat. TimedOut=false with an empty list
    /// means Gateway confirmed no positions are held.
    /// </summary>
    Task<PositionsSnapshot> GetAllPositionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of all open orders in the IBKR account.
    /// TimedOut=true means Gateway did not respond within the timeout window.
    /// TimedOut=false with an empty list means no open orders exist.
    /// </summary>
    Task<OrdersSnapshot> GetAllOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the given order ID was placed and is tracked by Vela this session.
    /// Used by StartupReconciliationService to classify open orders as managed vs unknown.
    /// Always returns false in NullBrokerService (no orders placed in simulation).
    /// </summary>
    bool IsKnownOrder(int orderId);

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
    /// Places a market sell order for a specific quantity without affecting the remaining
    /// position. Used for partial closes on 1DTE positions at end of day.
    /// </summary>
    Task<BrokerOrderResult> PartialCloseAsync(
        TradeRecord trade,
        int quantityToClose,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a specific order by ID. Used to remove trail stops on positions
    /// converted to lotto overnight holds.
    /// </summary>
    Task CancelOrderAsync(int orderId, CancellationToken ct = default);

    /// <summary>
    /// Cancels an existing trail stop and places a new one with a tighter trail percentage.
    /// Called when post-fill slippage is elevated, to protect the position more aggressively.
    /// Returns the new stop order ID, or null if the replacement fails.
    /// </summary>
    Task<string?> ReplaceTrailStopAsync(
        string existingStopOrderId,
        int quantity,
        TradeOrder order,
        double newTrailPercent,
        CancellationToken ct = default);

    /// <summary>
    /// Places a standalone TRAIL stop (SELL, no OCA group, no ParentId) on an existing IBKR
    /// position that currently has no resting protection. Unlike the entry-time trail helpers,
    /// this takes contract primitives directly rather than a TradeOrder, the caller is acting
    /// on a position discovered independently rather than a fresh entry.
    /// Returns the order ID on success, or null if IBKR rejects it within the detection window.
    /// </summary>
    Task<string?> PlaceProtectiveStopAsync(
        string symbol,
        TradeType tradeType,
        string? optionsContractSymbol,
        string? direction,
        decimal? strike,
        string? expiration,
        int quantity,
        double trailPercent,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels an existing standalone target order and replaces both it and the current
    /// unprotected position with a genuine OCA trail+target pair, placed atomically together.
    /// Unlike the entry-time PlaceTrailWithTargetAsync, this does NOT fall back to a bare
    /// trail-only stop if the OCA placement is rejected, since existingTargetOrderId has
    /// already been cancelled, silently falling back would leave the position protected by
    /// an unlinked stop with its original target gone and nothing coordinating them.
    /// Returns (null, null) on rejection so the caller can report it and require manual
    /// placement, rather than silently degrading protection.
    /// </summary>
    Task<(string? StopId, string? TargetId)> PlaceProtectiveStopWithTargetAsync(
        string symbol,
        TradeType tradeType,
        string? optionsContractSymbol,
        string? direction,
        decimal? strike,
        string? expiration,
        int quantity,
        double trailPercent,
        string existingTargetOrderId,
        decimal targetPrice,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the net liquidation value of the account.
    /// Used by TradeGuard for exposure checks before placing orders.
    /// </summary>
    Task<decimal> GetAccountBalanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total market value of all open positions.
    /// Used by TradeGuard to calculate current exposure before new orders.
    /// </summary>
    Task<decimal> GetOpenPositionsValueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes a handler that fires when a broker-side stop or target order fills.
    /// </summary>
    void RegisterBrokerFillHandler(Action<string, decimal, TradeOutcome> handler);

    /// <summary>
    /// Fetches daily OHLCV bars for a stock symbol from the broker's historical data feed.
    /// Returns an empty list if the broker is unavailable or data cannot be retrieved.
    /// </summary>
    Task<List<HistoricalBar>> GetHistoricalBarsAsync(
        string symbol,
        int barCount,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a live snapshot of account-level figures directly from IB Gateway.
    /// Always issues a fresh request, never cached or backed by a database value.
    /// TimedOut=true means Gateway did not respond within the timeout window.
    /// </summary>
    Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct = default);
}

/// <summary>
/// Snapshot of all positions held in the IBKR account.
/// TimedOut=true indicates Gateway did not respond, callers must not treat an empty
/// Positions list as confirmation the account is flat in this case.
/// </summary>
public record PositionsSnapshot(List<IbkrPosition> Positions, bool TimedOut);

/// <summary>
/// Snapshot of all open orders in the IBKR account.
/// TimedOut=true indicates Gateway did not respond within the timeout window.
/// </summary>
public record OrdersSnapshot(List<IbkrOpenOrder> Orders, bool TimedOut);

/// <summary>
/// A single position held in the IBKR account, returned by GetAllPositionsAsync.
/// Quantity is negative for short positions.
/// </summary>
public record IbkrPosition(
    string Symbol,
    string SecType,
    string? LocalSymbol,
    int Quantity,
    decimal AvgCost);

/// <summary>
/// A single open order in the IBKR account, returned by GetAllOpenOrdersAsync.
/// </summary>
public record IbkrOpenOrder(
    int OrderId,
    string Symbol,
    string SecType,
    string? LocalSymbol,
    string Action,
    string OrderType,
    double Quantity,
    string Status,
    double? AuxPrice,
    double? LmtPrice);

/// <summary>
/// Live account-level snapshot returned by GetAccountSnapshotAsync.
/// TimedOut=true indicates Gateway did not respond within the timeout window,
/// callers must not treat the zeroed values as real account figures in this case.
/// </summary>
public record AccountSnapshot(
    decimal NetLiquidation,
    decimal TotalCash,
    decimal BuyingPower,
    decimal TodayPnL,
    bool TimedOut);