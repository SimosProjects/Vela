namespace Vela.AlertPoC.Services;

/// <summary>
/// Defines the contract for fetching alerts from an external source (e.g. Xtrades API).
/// Abstracting behind an interface allows the real HTTP client to be swapped for a stub
/// in unit tests, and for the API client to be replaced without touching callers.
/// </summary>
public interface IAlertApiClient
{
    Task<List<Alert>> GetAlertsAsync(CancellationToken cancellationToken = default, int pageSize = 10);

    /// <summary>
    /// Verifies that the Xtrades API is reachable and the current token is valid.
    /// Throws <see cref="Vela.Worker.Services.AlertApiException"/> with status code
    /// 401 or 403 when the token has expired or been revoked. Returns false for other
    /// unreachable states.
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