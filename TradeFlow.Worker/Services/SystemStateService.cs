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
    private const int HeartbeatIntervalSeconds = 30;
    private const int StartupDelayMs = 3000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IbkrConnectionService _ibkr;
    private readonly TradeGuard _tradeGuard;
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
    private readonly Lock _regimeLock = new();

    public SystemStateService(
        IServiceScopeFactory scopeFactory,
        IbkrConnectionService ibkr,
        TradeGuard tradeGuard,
        ILogger<SystemStateService> logger)
    {
        _scopeFactory = scopeFactory;
        _ibkr         = ibkr;
        _tradeGuard   = tradeGuard;
        _logger       = logger;
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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Brief delay so the rest of Worker startup completes before first write
        await Task.Delay(StartupDelayMs, ct);

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

            var row = await db.SystemState.FindAsync(new object[] { 1 }, ct);

            if (row is null)
            {
                row = new SystemState { Id = 1 };
                db.SystemState.Add(row);
            }

            row.RegimeTier       = tier;
            row.SizingMultiplier = sizingMult;
            row.BlockCalls       = blockCalls;
            row.SpyPrice         = spyPrice;
            row.Ma20             = ma20;
            row.Ma50             = ma50;
            row.Ma200            = ma200;
            row.Vix              = vix;
            row.VixDelta         = vixDelta;
            row.ChopScore        = chopScore;
            row.IbkrConnected    = _ibkr.IsConnected;
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
}