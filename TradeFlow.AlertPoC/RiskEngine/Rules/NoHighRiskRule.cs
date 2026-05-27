namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects alerts classified as high risk when high risk trading is disabled.
/// Controlled by the AllowHigh configuration flag in RiskEngineOptions.
/// Inserted into the rule chain only when AllowHigh is false.
/// </summary>
public class NoHighRiskRule : IRiskRule
{
    public RuleResult Evaluate(Alert alert)
    {
        var risk = alert.Risk?.ToLowerInvariant();

        return risk == "high"
            ? RuleResult.Fail("Rejected - high risk trades are disabled (AllowHigh=false)")
            : RuleResult.Pass("Not a high risk trade");
    }
}