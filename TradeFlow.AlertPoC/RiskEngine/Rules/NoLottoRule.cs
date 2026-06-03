namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects lotto risk alerts when lotto trading is disabled via config or
/// when the morning market regime assessment classified the session as choppy.
/// Config flag acts as a permanent override; the isChoppy delegate is the dynamic daily layer.
/// </summary>
public class NoLottoRule : IRiskRule
{
    private readonly bool _configDisabled;
    private readonly Func<bool> _isChoppy;
    private readonly Func<int> _chopScore;

    public NoLottoRule(bool configDisabled, Func<bool> isChoppy, Func<int> chopScore)
    {
        _configDisabled = configDisabled;
        _isChoppy       = isChoppy;
        _chopScore      = chopScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        if (alert.Risk != "lotto")
            return RuleResult.Pass("Risk level acceptable");

        if (_configDisabled)
            return RuleResult.Fail("Rejected - lotto risk alerts are excluded (AllowLotto=false)");

        if (_isChoppy())
            return RuleResult.Fail(
                $"Rejected - lotto trades disabled (choppy market, chop score {_chopScore()}/4)");

        return RuleResult.Pass("Lotto trade permitted");
    }
}