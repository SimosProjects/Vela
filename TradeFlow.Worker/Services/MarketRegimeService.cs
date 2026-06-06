namespace TradeFlow.Worker.Services;

/// <summary>
/// Holds the current day's market regime assessment, set once at 9:20am ET by MarketConditionsLogger.
/// Consumed by risk rules, PositionSizer, and TradeGuard throughout the trading session.
/// </summary>
public class MarketRegimeService
{
    private volatile bool _isChoppy = false;
    private volatile int _chopScore = 0;
    private volatile RegimeTier _tier = RegimeTier.Bullish;
    private volatile bool _blockCalls = false;

    private decimal _ma20 = 0m;
    private decimal _ma50 = 0m;
    private decimal _ma200 = 0m;
    private readonly Lock _maLock = new();

    private readonly ILogger<MarketRegimeService> _logger;

    public MarketRegimeService(ILogger<MarketRegimeService> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether the current session is classified as a choppy market.</summary>
    public bool IsChoppy => _isChoppy;

    /// <summary>Number of chop signals that fired this morning (0-6).</summary>
    public int ChopScore => _chopScore;

    /// <summary>Three-tier regime classification for position sizing.</summary>
    public RegimeTier Tier => _tier;

    /// <summary>
    /// Sizing multiplier derived from the current regime tier.
    /// Applied by PositionSizer to options and stock budgets.
    /// </summary>
    public decimal SizingMultiplier { get; private set; } = 1.0m;

    /// <summary>
    /// When true, call option entries are blocked for the session.
    /// Set in a Bearish regime when BlockCallsInBearish config is enabled.
    /// </summary>
    public bool BlockCalls => _blockCalls;

    /// <summary>SPY 20-day moving average as of this morning's open.</summary>
    public decimal Ma20 { get { lock (_maLock) return _ma20; } }

    /// <summary>SPY 50-day moving average as of this morning's open.</summary>
    public decimal Ma50 { get { lock (_maLock) return _ma50; } }

    /// <summary>SPY 200-day moving average as of this morning's open.</summary>
    public decimal Ma200 { get { lock (_maLock) return _ma200; } }

    /// <summary>
    /// Sets the market regime for the day based on the morning chop score and MA cascade.
    /// Called once at market open by MarketConditionsLogger after fetching market data.
    /// </summary>
    public void SetRegime(
        int chopScore,
        int minSignalsForChop,
        RegimeTier tier,
        decimal sizingMultiplier,
        bool blockCalls,
        decimal ma20,
        decimal ma50,
        decimal ma200)
    {
        _chopScore        = chopScore;
        _isChoppy         = chopScore >= minSignalsForChop;
        _tier             = tier;
        SizingMultiplier  = sizingMultiplier;
        _blockCalls       = blockCalls;

        lock (_maLock)
        {
            _ma20  = ma20;
            _ma50  = ma50;
            _ma200 = ma200;
        }

        _logger.LogInformation(
            "Market regime set — Tier: {Tier} | Sizing: {Multiplier:P0} | " +
            "BlockCalls: {BlockCalls} | ChopScore: {Score}/6 | " +
            "SPY 20MA: ${Ma20:F2} 50MA: ${Ma50:F2} 200MA: ${Ma200:F2}",
            tier, sizingMultiplier, blockCalls, chopScore, ma20, ma50, ma200);

        if (_isChoppy)
            _logger.LogWarning(
                "Market regime: CHOPPY (score {Score}/6, threshold {Min}) — " +
                "high risk and lotto trades blocked for the session",
                chopScore, minSignalsForChop);
    }
}

/// <summary>
/// Three-tier market regime used for position sizing and directional gating.
/// Bullish = full size. Choppy = half size. Bearish = quarter size, calls optionally blocked.
/// </summary>
public enum RegimeTier
{
    Bullish,
    Choppy,
    Bearish
}