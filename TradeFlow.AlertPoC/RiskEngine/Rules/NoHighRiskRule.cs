namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects high risk alerts when high risk trading is disabled via config or
/// when the morning market regime assessment classified the session as choppy.
/// Config flag acts as a permanent override; the isChoppy delegate is the dynamic daily layer.
/// </summary>
public class NoHighRiskRule : IRiskRule
{
    private readonly bool _configDisabled;
    private readonly Func<bool> _isChoppy;
    private readonly Func<int> _chopScore;

    public NoHighRiskRule(bool configDisabled, Func<bool> isChoppy, Func<int> chopScore)
    {
        _configDisabled = configDisabled;
        _isChoppy       = isChoppy;
        _chopScore      = chopScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var risk = alert.Risk?.ToLowerInvariant();
        if (risk != "high")
            return RuleResult.Pass("Not a high risk trade");

        if (_configDisabled)
            return RuleResult.Fail("Rejected - high risk trades are disabled (AllowHigh=false)");

        if (_isChoppy())
            return RuleResult.Fail(
                $"Rejected - high risk trades disabled (choppy market, chop score {_chopScore()}/4)");

        return RuleResult.Pass("High risk trade permitted");
    }
}