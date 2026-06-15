using System.Diagnostics.Metrics;

namespace Vela.Worker.Metrics;

/// <summary>
/// Instruments for the alert ingestion pipeline.
/// Registered as a singleton. Meter is thread-safe and designed
/// to be shared across the application lifetime.
/// </summary>
public sealed class AlertMetrics : IDisposable
{
    private readonly Meter _meter;

    // How many alerts we fetched from the API
    public readonly Counter<int> AlertsFetched;

    // How many were new after deduplication
    public readonly Counter<int> AlertsNew;

    // How many passed the risk engine
    public readonly Counter<int> AlertsApproved;

    // How many were rejected
    public readonly Counter<int> AlertsRejected;

    // How long each poll cycle took
    public readonly Histogram<double> PollDurationMs;

    public AlertMetrics()
    {
        _meter = new Meter("Vela.Worker", "1.0.0");

        AlertsFetched = _meter.CreateCounter<int>(
            "vela.alerts.fetched",
            description: "Total alerts fetched from the Xtrades API");

        AlertsNew = _meter.CreateCounter<int>(
            "vela.alerts.new",
            description: "New alerts after deduplication");

        AlertsApproved = _meter.CreateCounter<int>(
            "vela.alerts.approved",
            description: "Alerts approved by the risk engine");

        AlertsRejected = _meter.CreateCounter<int>(
            "vela.alerts.rejected",
            description: "Alerts rejected by the risk engine");

        PollDurationMs = _meter.CreateHistogram<double>(
            "vela.poll.duration",
            unit: "ms",
            description: "Duration of each REST poll cycle");
    }

    public void Dispose() => _meter.Dispose();
}