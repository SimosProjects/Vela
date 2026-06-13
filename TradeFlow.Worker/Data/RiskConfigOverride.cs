namespace TradeFlow.Worker.Data;

/// <summary>
/// Single-row table persisting user-saved risk config overrides from the dashboard.
/// Id is always 1. Deserialized to RiskConfigDto on read; re-serialized on write.
/// </summary>
public class RiskConfigOverride
{
    public int Id { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}