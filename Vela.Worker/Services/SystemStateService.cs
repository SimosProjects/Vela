using Vela.Worker.Configuration;
using Vela.Worker.Engine;

namespace Vela.Worker.Services;

/// <summary>
/// Writes Worker runtime state to the system_state table every 30 seconds.
/// Acts as the bridge between the Worker process and Vela.Api.
/// All writes are best-effort and any failure is logged as a warning and swallowed
/// so the trading path is never blocked or affected.
/// IsPaused is intentionally never written by this service; it is owned by the
/// Api pause endpoint and must not be overwritten on each heartbeat.
/// </summary>
public class SystemStateService : BackgroundService
{
    private const int HeartbeatIntervalSeconds = 3;
    private const int StartupDelayMs = 3000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IbkrConnectionService _ibkr;
    private readonly TradeGuard _tradeGuard;
    private readonly MarketRegimeService _marketRegime;
    private readonly RiskEngineOptions _riskOptions;
    private readonly ILogger<SystemStateService> _logger;

    // Regime snapshot set by UpdateRegime(), flushed to DB on next heartbeat cycle
    private string _regimeTier = "Unknown";
    private decimal _sizingMultiplier = 1.0m;
    private bool _blockCalls;
    private decimal? _spyPrice;
    private decimal? _ma20;
    private decimal? _ma50;
    private decimal? _ma200;
    private decimal? _vix;
    private decimal? _vixDelta;
    private int? _chopScore;
    private volatile bool _signalRConnected;
    private readonly Lock _regimeLock = new();

    // Tracks last-known override values so events only fire on change
    private bool _isPaused;
    private bool _allowOverrideBlocks;
    private bool _blockCallsOverride;
    private bool _blockHighOverride;
    private bool _blockLottoOverride;

    // True once the user has explicitly toggled BlockCalls from the dashboard this session.
    // When false, the override was seeded automatically from the regime and the system is
    // allowed to auto-clear it if the computed regime no longer requires blocking.
    // Reset to false on every regime checkpoint and Worker startup.
    private bool _blockCallsSetManually = false;

    // Set by UpdateRegime() when a regime checkpoint fires (and AllowOverrideBlocks is false).
    // Signals WriteHeartbeatAsync that in-memory overrides are authoritative this cycle and
    // should be written to DB, rather than reading DB overrides into memory.
    private volatile bool _regimeFreshlyUpdated = false;

    /// <summary>
    /// Fired whenever the pause state read from the database differs from the
    /// last-known value. Subscribers update their own state; this service does
    /// not hold a reference to any execution component.
    /// </summary>
    public event Action<bool>? PauseStateChanged;

    /// <summary>
    /// Fired whenever the block calls override read from the database differs
    /// from the last-known value. Wired to MarketRegimeService at the composition root.
    /// </summary>
    public event Action<bool>? BlockCallsOverrideChanged;

    /// <summary>
    /// Fired whenever the block high risk override read from the database differs
    /// from the last-known value. Wired to MarketRegimeService at the composition root.
    /// </summary>
    public event Action<bool>? BlockHighOverrideChanged;

    /// <summary>
    /// Fired whenever the block lotto override read from the database differs
    /// from the last-known value. Wired to MarketRegimeService at the composition root.
    /// </summary>
    public event Action<bool>? BlockLottoOverrideChanged;

    public SystemStateService(
        IServiceScopeFactory scopeFactory,
        IbkrConnectionService ibkr,
        TradeGuard tradeGuard,
        MarketRegimeService marketRegime,
        IOptions<RiskEngineOptions> riskOptions,
        ILogger<SystemStateService> logger)
    {
        _scopeFactory = scopeFactory;
        _ibkr = ibkr;
        _tradeGuard = tradeGuard;
        _marketRegime = marketRegime;
        _riskOptions = riskOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Stores the current market regime in memory. Persisted to the database
    /// on the next heartbeat cycle within 5 seconds.
    /// Called by MarketConditionsLogger immediately after SetRegime.
    /// Pass the EFFECTIVE regime values (after the downgrade-only rule), not the computed ones,
    /// so the heartbeat uses the locked-in tier for display while BlockCalls reflects reality.
    ///
    /// When AllowOverrideBlocks is false, each call is treated as an authoritative regime
    /// checkpoint and all three block flags are reset to their regime-derived values.
    /// When AllowOverrideBlocks is true, the block flags are left untouched so that
    /// dashboard-set values persist through regime checkpoints.
    /// </summary>
    public void UpdateRegime(
        string tier,
        decimal sizingMultiplier,
        bool blockCalls,
        decimal spyPrice,
        decimal ma20,
        decimal ma50,
        decimal ma200,
        decimal vix,
        decimal vixDelta,
        int chopScore)
    {
        lock (_regimeLock)
        {
            _regimeTier = tier;
            _sizingMultiplier = sizingMultiplier;
            _blockCalls = blockCalls;
            _spyPrice = spyPrice;
            _ma20 = ma20;
            _ma50 = ma50;
            _ma200 = ma200;
            _vix = vix;
            _vixDelta = vixDelta;
            _chopScore = chopScore;
        }

        // When override is active, skip block events entirely so dashboard-set values
        // persist through this checkpoint. Tier and sizing still update normally.
        if (_allowOverrideBlocks)
        {
            _logger.LogInformation(
                "Regime checkpoint: block override active — block settings unchanged (tier: {Tier})", tier);
            return;
        }

        // Each regime checkpoint is authoritative. Always fire all three override events
        // unconditionally, even when the value hasn't changed, so MarketRegimeService
        // is guaranteed to hold the correct state after every checkpoint.
        var isChoppyOrBearish = tier is "Choppy" or "Bearish";

        _blockCallsOverride = blockCalls;
        _blockCallsSetManually = false;
        BlockCallsOverrideChanged?.Invoke(blockCalls);

        _blockHighOverride = isChoppyOrBearish;
        BlockHighOverrideChanged?.Invoke(isChoppyOrBearish);

        _blockLottoOverride = isChoppyOrBearish;
        BlockLottoOverrideChanged?.Invoke(isChoppyOrBearish);

        _regimeFreshlyUpdated = true;
    }

    /// <summary>
    /// Stores the current SignalR connection state in memory. Persisted to the
    /// database on the next heartbeat cycle within 5 seconds.
    /// Called by SignalRListenerService on connect, reconnect, and disconnect.
    /// </summary>
    public void UpdateSignalRConnected(bool connected)
    {
        _signalRConnected = connected;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(StartupDelayMs, ct);

        await LoadRegimeFromDatabaseAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            await WriteHeartbeatAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct);
        }
    }

    // -- Helpers --

    internal async Task WriteHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            string tier; decimal sizingMult; bool blockCalls;
            decimal? spyPrice, ma20, ma50, ma200, vix, vixDelta;
            int? chopScore;

            lock (_regimeLock)
            {
                tier = _regimeTier;
                sizingMult = _sizingMultiplier;
                blockCalls = _blockCalls;
                spyPrice = _spyPrice;
                ma20 = _ma20;
                ma50 = _ma50;
                ma200 = _ma200;
                vix = _vix;
                vixDelta = _vixDelta;
                chopScore = _chopScore;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

            var row = await db.SystemState
                .AsTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (row is null)
            {
                row = new SystemState { Id = 1 };
                db.SystemState.Add(row);
            }

            // Apply any pending manual regime override written by the dashboard API.
            if (!string.IsNullOrEmpty(row.ForceRegime) &&
                Enum.TryParse<RegimeTier>(row.ForceRegime, out var forcedTier))
            {
                var sizingForced = forcedTier switch
                {
                    RegimeTier.Bullish => (decimal)_riskOptions.RegimeBullishSizingPct,
                    RegimeTier.Choppy => (decimal)_riskOptions.RegimeChoppySizingPct,
                    RegimeTier.Bearish => (decimal)_riskOptions.RegimeBearishSizingPct,
                    _ => 1.0m
                };
                var blockCallsForced = forcedTier == RegimeTier.Bearish
                    && _riskOptions.RegimeBearishBlockCalls;

                _marketRegime.SetRegimeTier(forcedTier, sizingForced, blockCallsForced);

                tier = forcedTier.ToString();
                sizingMult = sizingForced;
                blockCalls = blockCallsForced;

                lock (_regimeLock)
                {
                    _regimeTier = tier;
                    _sizingMultiplier = sizingMult;
                    _blockCalls = blockCalls;
                }

                row.ForceRegime = null;

                _logger.LogInformation(
                    "SystemStateService: applied manual regime override to {Tier}", tier);
            }

            // Pause state always propagates regardless of override or regime checkpoint status.
            var isPaused = row.IsPaused;
            if (isPaused != _isPaused)
            {
                _isPaused = isPaused;
                PauseStateChanged?.Invoke(isPaused);
                _logger.LogWarning(
                    "Dashboard: trading {State} — new entries will be {Action}",
                    isPaused ? "PAUSED" : "RESUMED",
                    isPaused ? "rejected" : "accepted");
            }

            if (_regimeFreshlyUpdated)
            {
                // Regime checkpoint just fired — in-memory overrides are authoritative.
                // Write them to DB so a Worker restart picks up the correct regime-derived
                // state rather than a stale previous-day manual override.
                row.BlockCallsOverride = _blockCallsOverride;
                row.BlockHighOverride = _blockHighOverride;
                row.BlockLottoOverride = _blockLottoOverride;
                _regimeFreshlyUpdated = false;
            }
            else
            {
                // Between regime checkpoints, DB is authoritative for block and override flags.
                // Dashboard writes go DB → memory via the change detection below.

                // Detect AllowOverrideBlocks toggle first. If it was just disabled, derive
                // block values from regime immediately and skip the individual block propagation
                // this tick to avoid conflicting events from stale DB values.
                var allowOverrideChanged = false;
                var allowOverride = row.AllowOverrideBlocks;
                if (allowOverride != _allowOverrideBlocks)
                {
                    _allowOverrideBlocks = allowOverride;
                    allowOverrideChanged = true;

                    if (!allowOverride)
                    {
                        // Override just disabled — snap all three block flags back to regime
                        // immediately. _regimeFreshlyUpdated = true writes them to DB next tick.
                        ApplyRegimeDerivedBlocks();
                    }

                    _logger.LogInformation(
                        "Dashboard: block override {State}",
                        allowOverride
                            ? "ENABLED — regime checkpoints will not update block settings"
                            : "DISABLED — regime now controls block settings");
                }

                // Skip individual block propagation if override was just disabled.
                // ApplyRegimeDerivedBlocks() already fired the correct events and
                // _regimeFreshlyUpdated will write the derived values to DB next tick.
                if (!allowOverrideChanged || allowOverride)
                {
                    // Auto-clear BlockCalls when the computed regime no longer requires blocking
                    // and the user has not manually toggled it since the last checkpoint.
                    if (!blockCalls && _blockCallsOverride && !_blockCallsSetManually && !_allowOverrideBlocks)
                    {
                        row.BlockCallsOverride = false;
                        _blockCallsOverride = false;
                        BlockCallsOverrideChanged?.Invoke(false);
                        _logger.LogInformation(
                            "Block calls override auto-cleared — {Tier} regime no longer requires blocking",
                            tier);
                    }

                    // Propagate block calls override to regime service if DB changed.
                    var blockCallsOverride = row.BlockCallsOverride;
                    if (blockCallsOverride != _blockCallsOverride)
                    {
                        _blockCallsOverride = blockCallsOverride;
                        _blockCallsSetManually = !_allowOverrideBlocks;
                        BlockCallsOverrideChanged?.Invoke(blockCallsOverride);
                        _logger.LogWarning(
                            "Dashboard: call entries {State} via manual override",
                            blockCallsOverride ? "BLOCKED" : "unblocked");
                    }

                    // Propagate block high override if DB changed.
                    var blockHighOverride = row.BlockHighOverride;
                    if (blockHighOverride != _blockHighOverride)
                    {
                        _blockHighOverride = blockHighOverride;
                        BlockHighOverrideChanged?.Invoke(blockHighOverride);
                        _logger.LogWarning(
                            "Dashboard: high risk entries {State} via manual override",
                            blockHighOverride ? "BLOCKED" : "unblocked");
                    }

                    // Propagate block lotto override if DB changed.
                    var blockLottoOverride = row.BlockLottoOverride;
                    if (blockLottoOverride != _blockLottoOverride)
                    {
                        _blockLottoOverride = blockLottoOverride;
                        BlockLottoOverrideChanged?.Invoke(blockLottoOverride);
                        _logger.LogWarning(
                            "Dashboard: lotto entries {State} via manual override",
                            blockLottoOverride ? "BLOCKED" : "unblocked");
                    }
                }
            }

            row.RegimeTier = tier;
            row.SizingMultiplier = sizingMult;
            row.BlockCalls = blockCalls;
            row.SpyPrice = spyPrice;
            row.Ma20 = ma20;
            row.Ma50 = ma50;
            row.Ma200 = ma200;
            row.Vix = vix;
            row.VixDelta = vixDelta;
            row.ChopScore = chopScore;
            row.IbkrConnected = _ibkr.IsConnected;
            row.SignalRConnected = _signalRConnected;
            row.AccountBalance = _tradeGuard.CachedBalance;
            row.OpenValue = _tradeGuard.CachedOpenValue;
            row.WorkerHeartbeat = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemStateService: heartbeat write failed — Worker continues normally");
        }
    }

    // Fires all three block events with values derived from the last recorded regime tier.
    // Called when AllowOverrideBlocks is disabled so block settings snap back to regime immediately
    // rather than waiting for the next 9:20am or intraday checkpoint.
    private void ApplyRegimeDerivedBlocks()
    {
        bool blockCalls;
        string tier;

        lock (_regimeLock)
        {
            blockCalls = _blockCalls;
            tier = _regimeTier;
        }

        var isChoppyOrBearish = tier is "Choppy" or "Bearish";

        _blockCallsOverride = blockCalls;
        _blockCallsSetManually = false;
        BlockCallsOverrideChanged?.Invoke(blockCalls);

        _blockHighOverride = isChoppyOrBearish;
        BlockHighOverrideChanged?.Invoke(isChoppyOrBearish);

        _blockLottoOverride = isChoppyOrBearish;
        BlockLottoOverrideChanged?.Invoke(isChoppyOrBearish);

        _regimeFreshlyUpdated = true;

        _logger.LogInformation(
            "Block settings snapped to {Tier} regime — calls:{Calls} high:{High} lotto:{Lotto}",
            tier, blockCalls, isChoppyOrBearish, isChoppyOrBearish);
    }

    private async Task LoadRegimeFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

            var row = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (row is null || row.RegimeTier == "Unknown")
                return;

            var derivedBlockCalls = row.RegimeTier == "Bearish" && _riskOptions.RegimeBearishBlockCalls;
            var isChoppyOrBearish = row.RegimeTier is "Choppy" or "Bearish";

            lock (_regimeLock)
            {
                _regimeTier = row.RegimeTier;
                _sizingMultiplier = row.SizingMultiplier;
                _blockCalls = derivedBlockCalls;
                _spyPrice = row.SpyPrice;
                _ma20 = row.Ma20;
                _ma50 = row.Ma50;
                _ma200 = row.Ma200;
                _vix = row.Vix;
                _vixDelta = row.VixDelta;
                _chopScore = row.ChopScore;
            }

            if (Enum.TryParse<RegimeTier>(_regimeTier, out var restoredTier))
                _marketRegime.SetRegimeTier(restoredTier, _sizingMultiplier, derivedBlockCalls);

            _allowOverrideBlocks = row.AllowOverrideBlocks;

            if (_allowOverrideBlocks)
            {
                // Override is active — restore block values exactly from DB without re-deriving
                // from regime. This preserves settings across restarts when override is on.
                _blockCallsOverride = row.BlockCallsOverride;
                _blockHighOverride = row.BlockHighOverride;
                _blockLottoOverride = row.BlockLottoOverride;
                _blockCallsSetManually = false;

                BlockCallsOverrideChanged?.Invoke(row.BlockCallsOverride);
                BlockHighOverrideChanged?.Invoke(row.BlockHighOverride);
                BlockLottoOverrideChanged?.Invoke(row.BlockLottoOverride);

                _logger.LogInformation(
                    "SystemStateService: restored regime {Tier} from database — block override ACTIVE (calls:{Calls} high:{High} lotto:{Lotto})",
                    row.RegimeTier, row.BlockCallsOverride, row.BlockHighOverride, row.BlockLottoOverride);
                return;
            }

            // Override is off — re-seed block overrides from regime and normalise DB if stale.
            if (row.BlockCallsOverride != derivedBlockCalls)
            {
                await db.SystemState
                    .Where(s => s.Id == 1)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.BlockCallsOverride, derivedBlockCalls), ct);

                _blockCallsOverride = derivedBlockCalls;
                _blockCallsSetManually = false;
                BlockCallsOverrideChanged?.Invoke(derivedBlockCalls);

                _logger.LogInformation(
                    "SystemStateService: block calls override seeded to {Value} from {Tier} regime on startup",
                    derivedBlockCalls, row.RegimeTier);
            }
            else
            {
                _blockCallsOverride = row.BlockCallsOverride;
                _blockCallsSetManually = false;
                BlockCallsOverrideChanged?.Invoke(row.BlockCallsOverride);
            }

            if (row.BlockHighOverride != isChoppyOrBearish)
            {
                await db.SystemState
                    .Where(s => s.Id == 1)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.BlockHighOverride, isChoppyOrBearish), ct);

                _blockHighOverride = isChoppyOrBearish;
                BlockHighOverrideChanged?.Invoke(isChoppyOrBearish);

                _logger.LogInformation(
                    "SystemStateService: block high override seeded to {Value} from {Tier} regime on startup",
                    isChoppyOrBearish, row.RegimeTier);
            }
            else
            {
                _blockHighOverride = row.BlockHighOverride;
                BlockHighOverrideChanged?.Invoke(row.BlockHighOverride);
            }

            if (row.BlockLottoOverride != isChoppyOrBearish)
            {
                await db.SystemState
                    .Where(s => s.Id == 1)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.BlockLottoOverride, isChoppyOrBearish), ct);

                _blockLottoOverride = isChoppyOrBearish;
                BlockLottoOverrideChanged?.Invoke(isChoppyOrBearish);

                _logger.LogInformation(
                    "SystemStateService: block lotto override seeded to {Value} from {Tier} regime on startup",
                    isChoppyOrBearish, row.RegimeTier);
            }
            else
            {
                _blockLottoOverride = row.BlockLottoOverride;
                BlockLottoOverrideChanged?.Invoke(row.BlockLottoOverride);
            }

            _logger.LogInformation(
                "SystemStateService: restored regime {Tier} from database", row.RegimeTier);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemStateService: regime restore failed — starting with Unknown");
        }
    }
}