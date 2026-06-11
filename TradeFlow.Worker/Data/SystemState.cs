namespace TradeFlow.Worker.Data;

/// <summary>
/// Single-row bridge table written by the Worker every 30 seconds.
/// Exposes Worker-side state (regime, account, connection health, pause flag)
/// to TradeFlow.Api without requiring inter-process communication.
/// Id is always 1, the row is upserted, never inserted twice.
/// </summary>
public class SystemState
{
    public int Id { get; set; } = 1;

    // Regime, set by MarketConditionsLogger at 9:20am and on any scheduled mid-day re-check
    public string RegimeTier { get; set; } = "Unknown";
    public decimal SizingMultiplier { get; set; } = 1.0m;
    public bool BlockCalls { get; set; }
    public decimal? SpyPrice { get; set; }
    public decimal? Ma20 { get; set; }
    public decimal? Ma50 { get; set; }
    public decimal? Ma200 { get; set; }
    public decimal? Vix { get; set; }
    public decimal? VixDelta { get; set; }
    public int? ChopScore { get; set; }

    // Written by the dashboard API to request a manual regime override.
    // Consumed and cleared by SystemStateService on its next heartbeat tick.
    [System.ComponentModel.DataAnnotations.Schema.Column("force_regime")]
    public string? ForceRegime { get; set; }

    // System status, updated every 30 seconds
    public bool IsPaused { get; set; }
    public bool IbkrConnected { get; set; }
    public bool SignalRConnected { get; set; }
    public DateTimeOffset? WorkerHeartbeat { get; set; }

    // Account snapshot, updated every 30 seconds
    public decimal? AccountBalance { get; set; }
    public decimal? OpenValue { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}