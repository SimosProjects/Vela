namespace Vela.Api.Models;

/// <summary>A single stock alert item produced by one Spyglass scan cycle.</summary>
public record SpyglassAlertItem(
    string Id,
    string Symbol,
    string AsOf,
    double Score,
    IReadOnlyList<string> Setups,
    decimal CurrentPrice,
    // Computed price target based on the setup type. Null when the setup cannot
    // produce a reliable projection (e.g. insufficient historical data).
    // Used by PositionSizer directly instead of the configured StockTargetMultiple.
    decimal? PriceTarget);

/// <summary>
/// Envelope posted by Spyglass at the end of each scan cycle.
/// Contains metadata about the scan and the list of qualifying alerts.
/// </summary>
public record SpyglassEnvelope(
    string Source,
    DateTimeOffset EmittedAt,
    IReadOnlyList<SpyglassAlertItem> Alerts);