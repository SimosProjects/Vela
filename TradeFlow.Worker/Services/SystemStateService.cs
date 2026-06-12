using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Engine;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Writes Worker runtime state to the system_state table every 30 seconds.
/// Acts as the bridge between the Worker process and TradeFlow.Api.
/// All writes are best-effort and any failure is logged as a warning and swallowed
/// so the trading path is never blocked or affected.
/// IsPaused is intentionally never written by this service; it is owned by the
/// Api pause endpoint and must not be overwritten on each heartbeat.
/// </summary>
public class SystemStateService : BackgroundService
{
    private const int HeartbeatIntervalSeconds = 5;
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

    // Tracks last-known values so events only fire on change
    private bool _isPaused;
    private bool _blockCallsOverride;
    private bool _blockHighOverride;
    private bool _blockLottoOverride;

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
        _scopeFactory  = scopeFactory;
        _ibkr          = ibkr;
        _tradeGuard    = tradeGuard;
        _marketRegime  = marketRegime;
        _riskOptions   = riskOptions.Value;
        _logger        = logger;
    }

    /// <summary>
    /// Stores the current market regime in memory. Persisted to the database
    /// on the next heartbeat cycle within 30 seconds.
    /// Called by MarketConditionsLogger immediately after SetRegime.
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
            _regimeTier       = tier;
            _sizingMultiplier = sizingMultiplier;
            _blockCalls       = blockCalls;
            _spyPrice         = spyPrice;
            _ma20             = ma20;
            _ma50             = ma50;
            _ma200            = ma200;
            _vix              = vix;
            _vixDelta         = vixDelta;
            _chopScore        = chopScore;
        }
    }

    /// <summary>
    /// Stores the current SignalR connection state in memory. Persisted to the
    /// database on the next heartbeat cycle within 30 seconds.
    /// Called by SignalRListenerService on connect, reconnect, and disconnect.
    /// </summary>
    public void UpdateSignalRConnected(bool connected)
    {
        _signalRConnected = connected;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Brief delay so the rest of Worker startup completes before first write
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
                tier       = _regimeTier;
                sizingMult = _sizingMultiplier;
                blockCalls = _blockCalls;
                spyPrice   = _spyPrice;
                ma20       = _ma20;
                ma50       = _ma50;
                ma200      = _ma200;
                vix        = _vix;
                vixDelta   = _vixDelta;
                chopScore  = _chopScore;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();

            var row = await db.SystemState
                .AsTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (row is null)
            {
                row = new SystemState { Id = 1 };
                db.SystemState.Add(row);
            }

            // Apply any pending manual regime override written by the dashboard API.
            // Consumed here and cleared so it only fires once.
            if (!string.IsNullOrEmpty(row.ForceRegime) &&
                Enum.TryParse<RegimeTier>(row.ForceRegime, out var forcedTier))
            {
                var sizingForced = forcedTier switch
                {
                    RegimeTier.Bullish => (decimal)_riskOptions.RegimeBullishSizingPct,
                    RegimeTier.Choppy  => (decimal)_riskOptions.RegimeChoppySizingPct,
                    RegimeTier.Bearish => (decimal)_riskOptions.RegimeBearishSizingPct,
                    _                  => 1.0m
                };
                var blockCallsForced = forcedTier == RegimeTier.Bearish
                    && _riskOptions.RegimeBearishBlockCalls;

                _marketRegime.SetRegimeTier(forcedTier, sizingForced, blockCallsForced);

                tier       = forcedTier.ToString();
                sizingMult = sizingForced;
                blockCalls = blockCallsForced;

                lock (_regimeLock)
                {
                    _regimeTier       = tier;
                    _sizingMultiplier = sizingMult;
                    _blockCalls       = blockCalls;
                }

                row.ForceRegime = null;

                _logger.LogInformation(
                    "SystemStateService: applied manual regime override to {Tier}", tier);
            }

            // Propagate pause state to execution layer if it changed since last heartbeat.
            // Fired before the DB write so execution reflects the stored state immediately.
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

            // Propagate block calls override to regime service if it changed.
            var blockCallsOverride = row.BlockCallsOverride;
            if (blockCallsOverride != _blockCallsOverride)
            {
                _blockCallsOverride = blockCallsOverride;
                BlockCallsOverrideChanged?.Invoke(blockCallsOverride);
                _logger.LogWarning(
                    "Dashboard: call entries {State} via manual override",
                    blockCallsOverride ? "BLOCKED" : "unblocked");
            }

            // Propagate block high override if it changed.
            var blockHighOverride = row.BlockHighOverride;
            if (blockHighOverride != _blockHighOverride)
            {
                _blockHighOverride = blockHighOverride;
                BlockHighOverrideChanged?.Invoke(blockHighOverride);
                _logger.LogWarning(
                    "Dashboard: high risk entries {State} via manual override",
                    blockHighOverride ? "BLOCKED" : "unblocked");
            }

            // Propagate block lotto override if it changed.
            var blockLottoOverride = row.BlockLottoOverride;
            if (blockLottoOverride != _blockLottoOverride)
            {
                _blockLottoOverride = blockLottoOverride;
                BlockLottoOverrideChanged?.Invoke(blockLottoOverride);
                _logger.LogWarning(
                    "Dashboard: lotto entries {State} via manual override",
                    blockLottoOverride ? "BLOCKED" : "unblocked");
            }

            row.RegimeTier       = tier;
            row.SizingMultiplier = sizingMult;
            row.BlockCalls       = blockCalls; // regime-driven only — override is a separate column
            row.SpyPrice         = spyPrice;
            row.Ma20             = ma20;
            row.Ma50             = ma50;
            row.Ma200            = ma200;
            row.Vix              = vix;
            row.VixDelta         = vixDelta;
            row.ChopScore        = chopScore;
            row.IbkrConnected    = _ibkr.IsConnected;
            row.SignalRConnected  = _signalRConnected;
            row.AccountBalance   = _tradeGuard.CachedBalance;
            row.OpenValue        = _tradeGuard.CachedOpenValue;
            row.WorkerHeartbeat  = DateTimeOffset.UtcNow;
            row.UpdatedAt        = DateTimeOffset.UtcNow;

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

    private async Task LoadRegimeFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();

            var row = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (row is null || row.RegimeTier == "Unknown")
                return;

            // Derive blockCalls from tier + config rather than the stored value.
            // The stored block_calls column can be stale if a previous startup wrote
            // a default before MarketRegimeService was properly restored.
            var derivedBlockCalls = row.RegimeTier == "Bearish" && _riskOptions.RegimeBearishBlockCalls;

            lock (_regimeLock)
            {
                _regimeTier       = row.RegimeTier;
                _sizingMultiplier = row.SizingMultiplier;
                _blockCalls       = derivedBlockCalls;
                _spyPrice         = row.SpyPrice;
                _ma20             = row.Ma20;
                _ma50             = row.Ma50;
                _ma200            = row.Ma200;
                _vix              = row.Vix;
                _vixDelta         = row.VixDelta;
                _chopScore        = row.ChopScore;
            }

            // Restore regime to MarketRegimeService so the risk engine reflects the
            // correct tier and call-blocking state immediately, without waiting for
            // the 9:20am market conditions assessment.
            if (Enum.TryParse<RegimeTier>(_regimeTier, out var restoredTier))
                _marketRegime.SetRegimeTier(restoredTier, _sizingMultiplier, derivedBlockCalls);

            // Seed the block calls override from the regime on startup so the dashboard
            // toggle initialises correctly. User can freely change it during the session.
            // Only writes to DB if the stored override doesn't already match the regime.
            if (row.BlockCallsOverride != derivedBlockCalls)
            {
                await db.SystemState
                    .Where(s => s.Id == 1)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.BlockCallsOverride, derivedBlockCalls), ct);

                _blockCallsOverride = derivedBlockCalls;
                BlockCallsOverrideChanged?.Invoke(derivedBlockCalls);

                _logger.LogInformation(
                    "SystemStateService: block calls override seeded to {Value} from {Tier} regime on startup",
                    derivedBlockCalls, row.RegimeTier);
            }
            else
            {
                _blockCallsOverride = row.BlockCallsOverride;
            }

            // Seed high risk and lotto overrides — both active in Choppy and Bearish regimes.
            var isChoppyOrBearish = row.RegimeTier is "Choppy" or "Bearish";

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