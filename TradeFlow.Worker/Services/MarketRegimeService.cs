namespace TradeFlow.Worker.Services;

/// <summary>
/// Holds the current day's market regime assessment, set once at 9am ET by MarketConditionsLogger.
/// Consumed by risk rules and TradeGuard throughout the trading session.
/// </summary>
public class MarketRegimeService
{
    private volatile bool _isChoppy = false;
    private volatile int _chopScore = 0;

    private readonly ILogger<MarketRegimeService> _logger;

    public MarketRegimeService(ILogger<MarketRegimeService> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether the current session is classified as a choppy market.</summary>
    public bool IsChoppy => _isChoppy;

    /// <summary>Number of chop signals that fired this morning (0-4).</summary>
    public int ChopScore => _chopScore;

    /// <summary>
    /// Sets the market regime for the day based on the morning chop score.
    /// Called once at 9am ET by MarketConditionsLogger after fetching market data.
    /// A choppy regime automatically blocks high risk and lotto trades regardless
    /// of the AllowHigh and AllowLotto config flags.
    /// </summary>
    public void SetRegime(int chopScore, int minSignalsForChop)
    {
        _chopScore = chopScore;
        _isChoppy  = chopScore >= minSignalsForChop;

        if (_isChoppy)
        {
            _logger.LogWarning(
                "Market regime: CHOPPY (score {Score}/4, threshold {Min}) — " +
                "high risk and lotto trades blocked for the session",
                chopScore, minSignalsForChop);
        }
        else
        {
            _logger.LogInformation(
                "Market regime: NORMAL (score {Score}/4, threshold {Min}) — " +
                "risk tiers governed by config",
                chopScore, minSignalsForChop);
        }
    }
}