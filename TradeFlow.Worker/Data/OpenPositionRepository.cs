namespace TradeFlow.Worker.Data;

/// <inheritdoc/>
public class OpenPositionRepository : IOpenPositionRepository
{
    private readonly TradeFlowDbContext _db;
    private readonly ILogger<OpenPositionRepository> _logger;

    public OpenPositionRepository(TradeFlowDbContext db, ILogger<OpenPositionRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<List<OpenPosition>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.OpenPositions.AsTracking().ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(OpenPosition position, CancellationToken ct = default)
    {
        try
        {
            // Insert or update — safe across Worker restarts if same order ID is seen again
            var existing = await _db.OpenPositions.AsTracking()
                .FirstOrDefaultAsync(p => p.OrderId == position.OrderId, ct);

            if (existing is null)
                _db.OpenPositions.Add(position);
            else
            {
                existing.HasAveraged   = position.HasAveraged;
                existing.StopOrderId   = position.StopOrderId;
                existing.TargetOrderId = position.TargetOrderId;
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save open position for {Symbol} OrderId: {OrderId}",
                position.Symbol, position.OrderId);
            throw;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string orderId, CancellationToken ct = default)
    {
        try
        {
            await _db.OpenPositions
                .Where(p => p.OrderId == orderId)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete open position OrderId: {OrderId}", orderId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateQuantityAsync(string orderId, int newQuantity, CancellationToken ct = default)
    {
        try
        {
            await _db.OpenPositions
                .Where(p => p.OrderId == orderId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Quantity, newQuantity), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update quantity for open position OrderId: {OrderId}", orderId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<OpenPosition?> GetBySymbolAndUserAsync(
        string symbol, string userName, CancellationToken ct = default)
    {
        try
        {
            return await _db.OpenPositions.AsTracking()
                .FirstOrDefaultAsync(p =>
                    p.Symbol   == symbol &&
                    p.UserName == userName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to find open position for {Symbol} / {UserName}", symbol, userName);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAverageAsync(
        string orderId,
        int newQuantity,
        decimal newEntryPrice,
        decimal newEntryAmount,
        string? newStopOrderId,
        CancellationToken ct = default)
    {
        try
        {
            await _db.OpenPositions
                .Where(p => p.OrderId == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Quantity,    newQuantity)
                    .SetProperty(p => p.EntryPrice,  newEntryPrice)
                    .SetProperty(p => p.EntryAmount, newEntryAmount)
                    .SetProperty(p => p.StopOrderId, newStopOrderId)
                    .SetProperty(p => p.HasAveraged, true), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update averaged position OrderId: {OrderId}", orderId);
            throw;
        }
    }
}