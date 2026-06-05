namespace TradeFlow.AlertPoC.RiskEngine;

public class MinDiscordRankRule : IRiskRule
{
    private readonly IReadOnlyList<string> _allowedRanks;
    private readonly IReadOnlyList<string> _approvedTraders;

    public MinDiscordRankRule(IReadOnlyList<string> allowedRanks, IReadOnlyList<string> approvedTraders)
    {
        _allowedRanks    = allowedRanks;
        _approvedTraders = approvedTraders;
    }

    public RuleResult Evaluate(Alert alert)
    {
        if (_allowedRanks.Count == 0)
            return RuleResult.Pass("Discord rank check disabled");

        var userName = alert.UserName ?? string.Empty;
        if (_approvedTraders.Contains(userName))
            return RuleResult.Pass("Approved trader — rank check bypassed");

        var rank = alert.DiscordRank;

        if (string.IsNullOrEmpty(rank))
            return RuleResult.Fail("Trader has no Discord rank");

        var allowed = _allowedRanks.Any(r =>
            rank.StartsWith(r, StringComparison.OrdinalIgnoreCase));

        return allowed
            ? RuleResult.Pass($"Rank '{rank}' permitted")
            : RuleResult.Fail($"Rank '{rank}' not in allowed list");
    }
}