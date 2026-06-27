namespace Vela.AlertPoC.Services;

/// <summary>
/// Defines the contract for fetching alerts from the Xtrades REST API.
/// Two separate fetch methods exist because entries and exits have different
/// sort semantics: entries sort by TimeOfEntryAlertEpoch, exits by TimeOfFullExitAlertEpoch.
/// Combining them into one query with a single sort causes exits to be buried
/// below entries and fall off the page — the root cause of the June 23 missed STC gap.
/// </summary>
public interface IAlertApiClient
{
    /// <summary>
    /// Fetches recent entry alerts (BTO/AVG) from the Xtrades REST API,
    /// ordered by entry time descending so the freshest alerts are always
    /// within the first page.
    /// </summary>
    Task<List<Alert>> GetAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 10);

    /// <summary>
    /// Fetches recent exit alerts (STC/BTC) from the Xtrades REST API,
    /// ordered by exit time descending. Called on every poll cycle so that
    /// exit alerts fired during a SignalR gap are caught before the next
    /// session and routed through <see cref="BrokerExecutionService.HandleExitAsync"/>.
    /// </summary>
    Task<List<Alert>> GetExitAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 20);

    /// <summary>
    /// Verifies that the Xtrades API is reachable and the current token is valid.
    /// Throws <see cref="AlertApiException"/> with status code 401 or 403 when the
    /// token has expired or been revoked. Returns false for other unreachable states.
    /// </summary>
    Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the contract for normalizing raw alerts from the API into a consistent format
/// for downstream processing.
/// </summary>
public interface IAlertNormalizer
{
    Alert Normalize(Alert alert);
    bool IsProcessable(Alert alert);
}