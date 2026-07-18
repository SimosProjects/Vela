namespace Vela.Worker.Models;

/// <summary>
/// Fired by IbkrBrokerService when a broker-side stop or target order's bounded completion
/// wait expires with the order confirmed only partially filled (see the 2026-07-17 UBER
/// incident: a partial execDetails event was previously mistaken for a full close).
/// ConfirmedSoldQty and RemainingQty come from a fresh IBKR position query at the moment
/// the wait expired, not from arithmetic on the originally requested quantity.
/// RemainderProtected reflects whether the same stop/target order ID that partially filled
/// still appears live in a fresh open orders snapshot.
/// </summary>
public record BrokerPartialFillEvent(
    string EntryOrderId,
    int ConfirmedSoldQty,
    int RemainingQty,
    decimal FillPrice,
    TradeOutcome Outcome,
    bool RemainderProtected,
    string? ProtectionNote);
