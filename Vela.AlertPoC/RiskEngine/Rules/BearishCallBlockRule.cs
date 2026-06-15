namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Rejects bullish call option entries when the morning regime assessment has flagged
/// the session as Bearish and RegimeBearishBlockCalls is enabled in config.
/// The blockCalls delegate is evaluated live so the decision reflects the regime
/// set at market open without requiring a service restart.
/// Puts and stock entries are not affected by this rule.
/// </summary>
public class BearishCallBlockRule : IRiskRule
{
    private readonly Func<bool> _blockCalls;

    public BearishCallBlockRule(Func<bool> blockCalls)
    {
        _blockCalls = blockCalls;
    }

    public RuleResult Evaluate(Alert alert)
    {
        if (!_blockCalls())
            return RuleResult.Pass("Bearish call block not active");

        if (alert.Type?.ToLowerInvariant() != "options")
            return RuleResult.Pass("Not an options alert");

        var isCall = alert.IsBullish == true
                  || alert.Direction?.ToLowerInvariant() == "call";

        if (!isCall)
            return RuleResult.Pass("Put entry — not affected by bearish call block");

        return RuleResult.Fail("Rejected — call entries blocked during Bearish regime");
    }
}