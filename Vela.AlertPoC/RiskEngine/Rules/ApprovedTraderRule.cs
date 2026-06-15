using System.ComponentModel;

namespace Vela.AlertPoC.RiskEngine;

public class ApprovedTraderRule : IRiskRule
{
    private readonly HashSet<string> _approvedTraders;

    // HashSet gives O(1) lookup for efficient evaluation.
    // Important when this rule runs on every alert in a high-frequency polling loop
    public ApprovedTraderRule(IEnumerable<string> approvedTraders)
    {
        _approvedTraders = approvedTraders
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet();
    }

    public RuleResult Evaluate(Alert alert)
    {
        var trader = alert.UserName?.ToLowerInvariant();

        if (trader is null)
        {
            return RuleResult.Fail("Rejected - alert has no trader name");
        }

        return _approvedTraders.Contains(trader)
            ? RuleResult.Pass($"Trader '{alert.UserName}' is approved")
            : RuleResult.Fail($"Rejected - trader '{alert.UserName}' is not in the approved list");
    }
}