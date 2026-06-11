using Npgsql;

namespace TradeFlow.Worker.Data;

public class AlertRepository : IAlertRepository
{
    private readonly TradeFlowDbContext _dbContext;
    private readonly ILogger<AlertRepository> _logger;

    public AlertRepository(TradeFlowDbContext dbContext, ILogger<AlertRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc/>
    public async Task SaveManyAsync(IEnumerable<AlertEntity> alerts, CancellationToken cancellationToken = default)
    {
        var alertList = alerts.ToList();
        if (alertList.Count == 0) return;

        // Pre-filter already-persisted alerts before inserting. Polling and SignalR both process
        // the same feed, so concurrent duplicate inserts are expected on every cycle, not a race
        // condition worth surfacing. The catch below handles the rare true race in the narrow
        // window between this check and the insert.
        var ids = alertList.Select(a => a.Id).ToList();
        var existing = await GetExistingAlertIdsAsync(ids, cancellationToken);
        var toInsert = alertList.Where(a => !existing.Contains(a.Id)).ToList();

        if (toInsert.Count == 0)
        {
            _logger.LogDebug("All {Count} alert(s) already persisted — nothing to insert.", alertList.Count);
            return;
        }

        try
        {
            _dbContext.Alerts.AddRange(toInsert);
            var saved = await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved {Count} new alerts to the database.", saved);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            _dbContext.ChangeTracker.Clear();
            // Concurrent insert in the window between the existence check and SaveChangesAsync.
            // Genuinely rare, the pre-filter handles the common polling/SignalR overlap case.
            _logger.LogDebug(
                "Concurrent insert race — alert already persisted by another path. IDs: {AlertIds}",
                string.Join(", ", toInsert.Select(a => a.Id)));
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