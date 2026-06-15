namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Combines trader approval and XScore checks into a single rule.
/// Approved traders bypass the XScore requirement entirely, allowing
/// traders with a 0 XScore to trade if they are explicitly approved.
/// Unknown traders must meet the minimum XScore threshold to proceed.
/// </summary>
public class ApprovedOrHighScoreRule : IRiskRule
{
    private readonly HashSet<string> _approvedTraders;
    private readonly double _minimumScore;

    public ApprovedOrHighScoreRule(IEnumerable<string> approvedTraders, double minimumScore)
    {
        _approvedTraders = approvedTraders
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet();

        _minimumScore = minimumScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var trader = alert.UserName?.ToLowerInvariant();

        if (trader is null)
            return RuleResult.Fail("Rejected - alert has no trader name");

        // Approved traders bypass the XScore check entirely
        if (_approvedTraders.Contains(trader))
            return RuleResult.Pass($"Trader '{alert.UserName}' is approved");

        // Unknown traders must meet the minimum XScore threshold
        var score = alert.XScore ?? 0.0;

        return score >= _minimumScore
            ? RuleResult.Pass($"XScore {score} meets minimum threshold of {_minimumScore}")
            : RuleResult.Fail(
                $"Rejected - trader '{alert.UserName}' is not approved and XScore {score} is below {_minimumScore}");
    }
}