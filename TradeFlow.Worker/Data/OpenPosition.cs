namespace TradeFlow.Worker.Data;

/// <summary>
/// Persisted snapshot of an open trade position.
/// Written when TradeGuard registers an open, deleted when the position closes.
/// Allows TradeGuard to reload its in-memory state after a Worker restart
/// so exit alerts and position monitoring are not lost between sessions.
/// </summary>
public class OpenPosition
{
    public string OrderId { get; set; } = string.Empty;
    public string? StopOrderId { get; set; }
    public string? TargetOrderId { get; set; }
    public string AlertId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string TradeType { get; set; } = string.Empty;
    public string? OptionsContract { get; set; }
    public string? Direction { get; set; }
    public decimal? Strike { get; set; }
    public string? Expiration { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryAmount { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TargetPrice { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public bool IsAverage { get; set; }
    public bool HasAveraged { get; set; }
    public bool IsManual { get; set; }
}