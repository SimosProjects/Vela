namespace TradeFlow.Worker.Data;

/// <summary>
/// Read model for the worker_logs table written by WorkerLogSink.
/// The table is created by the sink on Worker startup — no EF migration required.
/// </summary>
public class WorkerLog
{
    public long Id { get; set; }
    public DateTimeOffset LoggedAt { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
}