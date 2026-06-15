namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Represents the result of evaluating a risk rule against an alert.
/// Is immutable and self-contained, allowing rules to be pure functions without side effects.
/// </summary>
/// <param name="Passed"></param>
/// <param name="Reason"></param>
public record RuleResult(bool Passed, string Reason)
{
        public static RuleResult Pass(string reason) => new(true, reason);
        public static RuleResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Represents the overall risk assessment for an alert after evaluating all applicable rules.
/// Approved is true only if all rules passed; Reason provides context for any rejection to aid in debugging and monitoring.
/// </summary>
/// <param name="Approved"></param>
/// <param name="Reason"></param>
public record RiskResult(bool Approved, string Reason)
{
    public static RiskResult Accept() => new(true, "All rules passed");
    public static RiskResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Defines the contract for a risk rule that can be evaluated against an alert.
/// Each rule has a single responsibility (e.g. check if the alert's underlying symbol is in a watchlist, or if the alert's entry price exceeds a threshold).
/// </summary>
public interface IRiskRule
{
    RuleResult Evaluate(Alert alert);
}