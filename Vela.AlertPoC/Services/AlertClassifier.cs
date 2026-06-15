namespace Vela.AlertPoC.Services;

// Represents the normalized category of an alert after classification.
// Using a record keeps each result immutable and comparable by value —
// useful later when deduplicating or logging classification outcomes.
public record AlertClassification(
    AlertCategory Category,
    string Description
);

public enum AlertCategory
{
    CallOptionEntry,
    CallOptionExit,
    PutOptionEntry,
    PutOptionExit,
    StockEntry,
    StockExit,
    Unclassified
}

/// <summary>
/// Service responsible for classifying raw alerts into well-known categories based on their properties.
/// </summary>
public static class AlertClassifier
{
    /// <summary>
    ///  Classifies an alert into a well-known category based on its properties.
    /// </summary>
    /// <param name="alert"></param>
    /// <returns></returns>
    public static AlertClassification Classify(Alert alert)
    {
        var type = alert.Type?.ToLowerInvariant();
        var direction = alert.Direction?.ToLowerInvariant();
        var side = alert.Side?.ToLowerInvariant();

        return (type, direction, side) switch
        {
            ("options", "call", "bto") => new(AlertCategory.CallOptionEntry, "Call option entry"),
            ("options", "call", "stc") => new(AlertCategory.CallOptionExit, "Call option exit"),
            ("options", "put",  "bto") => new(AlertCategory.PutOptionEntry, "Put option entry"),
            ("options", "put",  "stc") => new(AlertCategory.PutOptionExit, "Put option exit"),
            ("commons", _,      "bto") => new(AlertCategory.StockEntry, "Stock entry"),
            ("commons", _,      "stc") => new(AlertCategory.StockExit, "Stock exit"),
            _ => new(AlertCategory.Unclassified, $"Unclassified alert (type={type}, direction={direction}, side={side})")
        };
    }

    /// <summary>
    /// Returns true if the classification represents an entry alert (call option entry, put option entry, or stock entry).
    /// </summary>
    /// <param name="classification"></param>
    /// <returns></returns>
    public static bool IsEntry(AlertClassification classification) =>
        classification.Category is AlertCategory.CallOptionEntry or
                                      AlertCategory.PutOptionEntry or
                                      AlertCategory.StockEntry;
}