namespace Vela.Worker.Data;

/// <summary>
/// A dashboard-initiated request to force-close an open position. Written by the Api
/// and consumed by ForceCloseConsumerService in the Worker, which performs the broker close
/// and writes the final outcome back to Status. Status is "Requested" on insert, then one of
/// the ForceCloseOutcome values, or "Expired" when the request aged out before processing.
/// </summary>
public class ForceCloseRequest
{
    public long Id { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = "Requested";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}