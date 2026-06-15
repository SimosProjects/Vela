namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Rejects any alert that is not a BTO entry signal, as indicated by the 'Side' property of the alert.
/// This is the first gate - exits, short sells, and covered calls are not actionable for our execution 
/// strategy, so we want to filter them out as early as possible to save resources and reduce noise in monitoring and debugging.
/// </summary>
public class EntryOnlyRule : IRiskRule
{
    public RuleResult Evaluate(Alert alert) => 
        alert.Side == "bto"
            ? RuleResult.Pass("Entry signal confirmed")
            : RuleResult.Fail($"Rejected - side '{alert.Side}' is not a BTO entry");
}