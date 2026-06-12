
namespace TradeFlow.Worker.Data;

/// <summary>
/// Persists open trade positions so TradeGuard can reload its state after a Worker restart.
/// </summary>
public interface IOpenPositionRepository
{
    /// <summary>Gets all currently open positions from the database.</summary>
    Task<List<OpenPosition>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Saves a new open position when a trade is entered.</summary>
    Task SaveAsync(OpenPosition position, CancellationToken ct = default);

    /// <summary>Removes a position when it closes.</summary>
    Task DeleteAsync(string orderId, CancellationToken ct = default);

    /// <summary>Updates the quantity of an open position after a partial close.</summary>
    Task UpdateQuantityAsync(string orderId, int newQuantity, CancellationToken ct = default);

    /// <summary>Finds an open position by symbol and trader username.</summary>
    Task<OpenPosition?> GetBySymbolAndUserAsync(
        string symbol, string userName, CancellationToken ct = default);

    /// <summary>
    /// Updates an averaged position, combines quantity, recalculates weighted entry price,
    /// and updates the stop order ID to the new OCA trail stop.
    /// </summary>
    Task UpdateAverageAsync(
        string orderId,
        int newQuantity,
        decimal newEntryPrice,
        decimal newEntryAmount,
        string? newStopOrderId,
        CancellationToken ct = default);
}