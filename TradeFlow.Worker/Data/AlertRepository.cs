using Npgsql;

namespace TradeFlow.Worker.Data; 

public class AlertRepository : IAlertRepository
{
    private readonly TradeFlowDbContext _dbContext;
    private readonly ILogger<AlertRepository> _logger;

    public AlertRepository(TradeFlowDbContext dbContext, ILogger<AlertRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SaveManyAsync(IEnumerable<AlertEntity> alerts, CancellationToken cancellationToken = default)
    {
        var alertList = alerts.ToList();
        if (alertList.Count == 0)
        {
            return;
        }

        try
        {
            _dbContext.Alerts.AddRange(alertList);
            var saved = await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Saved {Count} new alerts to the database.", saved);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            _dbContext.ChangeTracker.Clear(); // Clear the change tracker to avoid issues with the failed entities

            // Unique violation - likely due to concurrent insert of the same alert
            _logger.LogWarning(
                "Attempted to insert duplicate alert - skipping. Alert IDs: {AlertIds}",
                string.Join(", ", alertList.Select(a => a.Id)));
        }
    }

    /// <inheritdoc/>
    public async Task<HashSet<string>> GetExistingAlertIdsAsync(
        IEnumerable<string> alertIds, CancellationToken cancellationToken = default)
    {
        var ids = alertIds.ToList();

        if (ids.Count == 0)
            return [];

        var existing = await _dbContext.Alerts
            .Where(a => ids.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        return existing.ToHashSet();
    }
}
