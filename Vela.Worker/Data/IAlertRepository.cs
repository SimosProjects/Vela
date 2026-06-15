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
    /// <param name="alerts"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SaveManyAsync(IEnumerable<AlertEntity> alerts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Given a list of alert IDs, returns the subset that already exist in the database.
    /// </summary>
    /// <param name="alertIds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<HashSet<string>> GetExistingAlertIdsAsync(IEnumerable<string> alertIds, CancellationToken cancellationToken = default);
}