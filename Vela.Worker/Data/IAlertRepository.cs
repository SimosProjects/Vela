namespace Vela.Worker.Data;

/// <summary>
/// Defines the contract for alert persistence operations.
/// This abstraction allows the alert processing pipeline to interact with the database
/// without being coupled to a specific data access implementation, enabling easier testing and future flexibility.
/// </summary>
public interface IAlertRepository
{
    /// <summary>
    /// Saves a batch of alert entities in a single transaction. Duplicate key violations
    /// are caught and skipped silently, concurrent poll cycles may attempt to insert the same alert.
    /// </summary>
    Task SaveManyAsync(IEnumerable<AlertEntity> alerts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Given a list of alert IDs, returns the subset that already exist in the database.
    /// </summary>
    Task<HashSet<string>> GetExistingAlertIdsAsync(
        IEnumerable<string> alertIds, CancellationToken cancellationToken = default);

    /// <summary>Updates the RiskReason field for a single alert by ID.</summary>
    Task UpdateRiskReasonAsync(string id, string riskReason, CancellationToken ct = default);

    /// <summary>
    /// Updates both RiskApproved and RiskReason for a single alert by ID.
    /// Used by SpyglassAlertConsumerService after running Spyglass alerts through the risk engine,
    /// so the alerts table reflects the same outcome shape as Xtrades alerts.
    /// </summary>
    Task UpdateRiskResultAsync(string id, bool riskApproved, string riskReason, CancellationToken ct = default);
}