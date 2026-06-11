namespace TradeFlow.Api.Models;

/// <summary>
/// Composite state snapshot returned by GET /api/dashboard/state.
/// Spread into the frontend data object so regime, account, and system
/// become top-level keys alongside positions and closedToday.
/// </summary>
public record DashboardStateResponse(
    RegimeResponse Regime,
    AccountResponse Account,
    SystemStatusResponse System
);

/// <summary>Market regime and sizing context, sourced from system_state.</summary>
public record RegimeResponse(
    string Tier,
    decimal? SpyPrice,
    decimal? Ma20,
    decimal Ma20pct,
    decimal? Ma50,
    decimal Ma50pct,
    decimal? Ma200,
    decimal Ma200pct,
    decimal? Vix,
    decimal? VixDelta,
    int SizingPct,
    bool BlockCalls,
    int? ChopScore,
    string Bias
);

/// <summary>
/// Account snapshot. Balance and OpenValue come from system_state.
/// DailyPnl is the sum of today's closed trade_metrics rows.
/// ExposurePct is derived as OpenValue / Balance.
/// </summary>
public record AccountResponse(
    decimal Balance,
    decimal OpenValue,
    decimal ExposurePct,
    decimal DailyPnl,
    decimal Deployable
);

/// <summary>
/// System health derived from system_state timestamps.
/// WorkerRunning: heartbeat within the last 60 seconds.
/// XtradesConnected: live SignalR connection state written by the Worker.
/// MarketOpen: derived from current Eastern Time.
/// BlockCallsOverride: dashboard-driven call block, independent of regime.
/// </summary>
public record SystemStatusResponse(
    bool IbkrConnected,
    bool XtradesConnected,
    bool WorkerRunning,
    bool MarketOpen,
    bool IsPaused,
    bool BlockCallsOverride,
    DateTimeOffset? WorkerHeartbeat,
    DateTimeOffset? LastAlertAt
);

/// <summary>
/// Open position returned by GET /api/dashboard/positions.
/// Contract is formatted from the OCC options_contract symbol (e.g. TSLA 250C 6/20)
/// or falls back to the underlying symbol for stock positions.
/// XScore is sourced by joining open_positions to alerts on alert_id.
/// </summary>
public record PositionResponse(
    string Id,
    string Contract,
    string? Direction,
    int Quantity,
    decimal EntryPrice,
    decimal CostBasis,
    decimal StopPrice,
    decimal TargetPrice,
    DateTimeOffset OpenedAt,
    string Trader,
    double? XScore,
    string RiskTier
);

/// <summary>Closed trade returned by GET /api/dashboard/closed-today.</summary>
public record ClosedTradeResponse(
    string Id,
    string Contract,
    string? Direction,
    string? Trader,
    double? XScore,
    string? DiscordRank,
    int? Quantity,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? Pnl,
    decimal? PnlPct,
    string? Outcome,
    DateTimeOffset? ClosedAt
);