namespace TradeFlow.AlertPoC.Services;

/// <summary>
/// Service responsible for normalizing raw alerts from the API into a consistent format for downstream processing.
/// </summary>
public class AlertNormalizer : IAlertNormalizer
{
    /// <summary>
    /// Normalizes an alert by trimming whitespace and standardizing casing on key string properties.
    /// This helps downstream classification and deduplication logic be more robust to minor variations in the API response.
    /// </summary>
    public Alert Normalize(Alert alert) => alert with
    {
        // Normalize symbol to uppercase for consistent downstream processing
        Symbol = alert.Symbol?.ToUpperInvariant(),

        // Normalize side to lowercase for consistent classification logic
        Side = alert.Side?.ToLowerInvariant(),

        // Normalize type to lowercase for consistent classification logic
        Type = alert.Type?.ToLowerInvariant(),

        // Normalize direction to lowercase for consistent classification logic
        Direction = alert.Direction?.ToLowerInvariant(),

        // Trim whitespace from the original message to clean up any formatting inconsistencies
        OriginalMessage = alert.OriginalMessage?.Trim(),

        // Trim whitespace from the original exit message as well
        OriginalExitMessage = alert.OriginalExitMessage?.Trim(),

        // Fall back to actual market price at alert time when trader posts a market order
        // without an explicit price — fixes null PricePaid on SignalR events for traders
        // like Sean@BearishBull, woooh77, Shinobi, IGGY, Paltrader who use "@ m" notation.
        PricePaid = alert.PricePaid ?? alert.ActualPriceAtTimeOfAlert,

        DiscordRank = alert.UserMeta?.DiscordRankDisplayName
    };

    /// <summary>
    /// Returns true if the alert has all the required properties to be processed further (e.g. classified and executed).
    /// </summary>
    /// <param name="alert"></param>
    /// <returns></returns>
    public bool IsProcessable(Alert alert) =>
        alert.Id is not null &&
        alert.Symbol is not null &&
        alert.Side is not null &&
        alert.Type is not null;
}