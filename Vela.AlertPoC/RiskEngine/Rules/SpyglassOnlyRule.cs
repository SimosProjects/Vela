namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Hard gate that rejects every alert not sourced from Spyglass. A numeric xScore threshold
/// cannot separate Spyglass (hardcoded XScore 100) from an Xtrades trader who also scores 100,
/// so identity is checked directly. Both the SPYGLASS username and the "spyglass-" Id prefix
/// minted by the Vela API must match, which closes the gap where an Xtrades user could name
/// themselves SPYGLASS.
/// </summary>
public class SpyglassOnlyRule : IRiskRule
{
    private const string SpyglassUserName = "SPYGLASS";
    private const string SpyglassIdPrefix = "spyglass-";

    public RuleResult Evaluate(Alert alert)
    {
        var isSpyglass =
            string.Equals(alert.UserName, SpyglassUserName, StringComparison.Ordinal) &&
            alert.Id is not null &&
            alert.Id.StartsWith(SpyglassIdPrefix, StringComparison.Ordinal);

        return isSpyglass
            ? RuleResult.Pass("Alert is Spyglass-sourced")
            : RuleResult.Fail(
                $"Rejected - Spyglass-only mode is enabled and '{alert.UserName ?? "unknown"}' is not a Spyglass alert");
    }
}
