namespace Vela.Worker.Services;

/// <summary>
/// Holds the current day's market regime assessment, set at 9:20am ET by MarketConditionsLogger
/// and re-evaluated at intraday checkpoints (11:00, 13:00, 14:00 ET).
/// Consumed by risk rules, PositionSizer, and TradeGuard throughout the trading session.
///
/// Downgrade-only rule: intraday checkpoints can only worsen the regime tier (Bullish → Choppy →
/// Bearish). Improvements to the tier wait for the next 9:20am full assessment. This prevents the
/// session sizing from loosening mid-day on a temporary bounce.
///
/// BlockCalls is intentionally split from the tier. Even when the tier is held conservatively,
/// BlockCalls follows the computed regime so calls are unblocked when conditions genuinely improve.
/// </summary>
public class MarketRegimeService
{
    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private volatile bool _isChoppy = false;
    private volatile int _chopScore = 0;
    private volatile RegimeTier _tier = RegimeTier.Bullish;

    // Regime-computed call-blocking flag. Always reflects the computed tier, not the locked-in
    // one, so SystemStateService can auto-clear the dashboard override when conditions improve
    // even while sizing stays conservative under the downgrade-only rule.
    private volatile bool _blockCalls = false;

    // Dashboard override flags, controlled exclusively by the toggle, seeded from regime on startup
    private volatile bool _blockCallsOverride = false;
    private volatile bool _blockHighOverride = false;
    private volatile bool _blockLottoOverride = false;

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
    /// Under the downgrade-only rule this may be more conservative than the computed tier.
    /// </summary>
    public decimal SizingMultiplier { get; private set; } = 1.0m;

    /// <summary>
    /// When true, call option entries are blocked for the session.
    /// Controlled entirely by the dashboard toggle. The regime seeds the initial
    /// value on startup but the user can freely override it in either direction.
    /// </summary>
    public bool BlockCalls => _blockCallsOverride;

    /// <summary>
    /// Regime-computed call blocking flag. Unlike BlockCalls, this always reflects
    /// the computed regime rather than the dashboard override. SystemStateService reads
    /// this to drive the auto-clear logic when the regime improves.
    /// </summary>
    public bool RegimeBlockCalls => _blockCalls;

    /// <summary>
    /// When true, high risk entries (expiring this week beyond 1DTE) are blocked.
    /// Seeded from regime on startup (Choppy or Bearish = true). User-controllable.
    /// </summary>
    public bool BlockHigh => _blockHighOverride;

    /// <summary>
    /// When true, lotto entries (0DTE/1DTE) are blocked for the session.
    /// Seeded from regime on startup (Choppy or Bearish = true). User-controllable.
    /// </summary>
    public bool BlockLotto => _blockLottoOverride;

    /// <summary>Whether the dashboard has manually overridden call blocking.</summary>
    public bool BlockCallsOverride => _blockCallsOverride;

    /// <summary>SPY 20-day moving average as of this morning's open.</summary>
    public decimal Ma20 { get { lock (_maLock) return _ma20; } }

    /// <summary>SPY 50-day moving average as of this morning's open.</summary>
    public decimal Ma50 { get { lock (_maLock) return _ma50; } }

    /// <summary>SPY 200-day moving average as of this morning's open.</summary>
    public decimal Ma200 { get { lock (_maLock) return _ma200; } }

    /// <summary>
    /// Sets the market regime based on the morning chop score and MA cascade.
    /// Called by MarketConditionsLogger after fetching market data.
    ///
    /// Downgrade-only rule: when isIntradayCheck is true, the tier and sizing multiplier
    /// can only move to a more restrictive level. An improvement (e.g. Bearish → Choppy)
    /// is noted in the log but the session tier is held until the next 9:20am assessment.
    ///
    /// BlockCalls is always updated to the computed value regardless of the downgrade-only
    /// rule, so the dashboard auto-clear fires correctly when conditions improve.
    /// </summary>
    public void SetRegime(
        int chopScore,
        int minSignalsForChop,
        RegimeTier tier,
        decimal sizingMultiplier,
        bool blockCalls,
        decimal ma20,
        decimal ma50,
        decimal ma200,
        bool isIntradayCheck = false)
    {
        _chopScore = chopScore;
        _isChoppy  = chopScore >= minSignalsForChop;

        // Downgrade-only: hold the current tier if the computed tier would be an improvement.
        // (int)Bearish > (int)Choppy > (int)Bullish — a lower int value is more bullish.
        if (isIntradayCheck && (int)tier < (int)_tier)
        {
            _logger.LogInformation(
                "Intraday regime check: computed {Computed} but holding {Current} tier — " +
                "upgrades apply at next 9:20am assessment (downgrade-only rule)",
                tier, _tier);

            // Keep tier and sizing at the more conservative current level.
            // BlockCalls is intentionally updated to the computed value so the dashboard
            // auto-clear can fire even while sizing stays restrictive.
            _blockCalls = blockCalls;
        }
        else
        {
            _tier            = tier;
            SizingMultiplier = sizingMultiplier;
            _blockCalls      = blockCalls;
        }

        lock (_maLock)
        {
            _ma20  = ma20;
            _ma50  = ma50;
            _ma200 = ma200;
        }

        _logger.LogInformation(
            "Market regime {Action} — Tier: {Tier} | Sizing: {Multiplier:P0} | " +
            "BlockCalls (computed): {BlockCalls} | ChopScore: {Score}/6 | " +
            "SPY 20MA: ${Ma20:F2} 50MA: ${Ma50:F2} 200MA: ${Ma200:F2}",
            isIntradayCheck ? "checked" : "set",
            _tier, SizingMultiplier, blockCalls, chopScore, ma20, ma50, ma200);

        if (_isChoppy)
            _logger.LogWarning(
                "Market regime: CHOPPY (score {Score}/6, threshold {Min}) — " +
                "high risk and lotto trades blocked for the session",
                chopScore, minSignalsForChop);
    }

    /// <summary>
    /// Applies a manual regime override without a full market data re-assessment.
    /// Called by SystemStateService when a force_regime value is written to system_state
    /// via the dashboard API. MA values and chop score are preserved from the last assessment.
    /// Does not affect the downgrade-only session tracking — a manual override is always applied.
    /// </summary>
    public void SetRegimeTier(RegimeTier tier, decimal sizingMultiplier, bool blockCalls)
    {
        _tier            = tier;
        SizingMultiplier = sizingMultiplier;
        _blockCalls      = blockCalls;

        _logger.LogInformation(
            "Market regime manually overridden — Tier: {Tier} | Sizing: {Multiplier:P0} | BlockCalls: {BlockCalls}",
            tier, sizingMultiplier, blockCalls);
    }

    /// <summary>
    /// Applies a manual block calls override from the dashboard, independent of regime tier.
    /// When true, call entries are rejected regardless of the current regime.
    /// </summary>
    public void SetBlockCallsOverride(bool blockCalls)
    {
        _blockCallsOverride = blockCalls;
        _logger.LogInformation(
            "Block calls override {State} via dashboard",
            blockCalls ? "enabled" : "disabled");
    }

    /// <summary>
    /// Applies a manual high risk block override from the dashboard.
    /// When true, high risk entries (this-week expiry beyond 1DTE) are rejected.
    /// </summary>
    public void SetBlockHighOverride(bool blockHigh)
    {
        _blockHighOverride = blockHigh;
        _logger.LogInformation(
            "Block high risk override {State} via dashboard",
            blockHigh ? "enabled" : "disabled");
    }

    /// <summary>
    /// Applies a manual lotto block override from the dashboard.
    /// When true, lotto entries (0DTE/1DTE) are rejected.
    /// </summary>
    public void SetBlockLottoOverride(bool blockLotto)
    {
        _blockLottoOverride = blockLotto;
        _logger.LogInformation(
            "Block lotto override {State} via dashboard",
            blockLotto ? "enabled" : "disabled");
    }
}

/// <summary>
/// Three-tier market regime used for position sizing and directional gating.
/// Bullish = full size. Choppy = reduced size. Bearish = minimum size, calls optionally blocked.
/// </summary>
public enum RegimeTier
{
    Bullish,
    Choppy,
    Bearish
}