namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Enforces a minimum XScore threshold for alerts to be accepted. Alerts with an XScore below the configured minimum are rejected.
/// XScore is a proprietary risk score provided by Xtrades that estimates the quality of the alert signal based on various factors.
/// Traders below the threshold have insufficient track records to automate.
/// </summary>
public class MinXScoreRule : IRiskRule
{
    private readonly double _minimumScore;

    // Threshold is injected rather than hardcoded so it can be configured
    // per environment via IOptions in the Worker Service
    public MinXScoreRule(double minimumScore = 60.0)
    {
        _minimumScore = minimumScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var score = alert.XScore ?? 0.0; // Treat null as 0 for safety

        return score >= _minimumScore
            ? RuleResult.Pass($"XScore {score} meets minimum threshold of {_minimumScore}")
            : RuleResult.Fail($"Rejected - XScore {score} is below minimum threshold of {_minimumScore}");
    }
}