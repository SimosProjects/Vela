namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects high risk alerts when high risk trading is disabled via config or
/// when the morning market regime assessment classified the session as choppy.
/// Also treats any 0DTE option as high risk regardless of the Xtrades risk label
/// Xtrades sometimes classifies same-day expiry options as "standard", which would
/// bypass AllowHigh=false without this override.
/// Config flag acts as a permanent override; the isChoppy delegate is the dynamic daily layer.
/// </summary>
public class NoHighRiskRule : IRiskRule
{
    private readonly bool _configDisabled;
    private readonly Func<bool> _isChoppy;
    private readonly Func<int> _chopScore;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public NoHighRiskRule(bool configDisabled, Func<bool> isChoppy, Func<int> chopScore)
    {
        _configDisabled = configDisabled;
        _isChoppy       = isChoppy;
        _chopScore      = chopScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var risk = alert.Risk?.ToLowerInvariant();

        // 0DTE options are always treated as at least high risk regardless of what
        // Xtrades reports — some platforms label same-day expiry as "standard".
        var isEffectivelyHigh = risk == "high" || IsZeroDte(alert);

        if (!isEffectivelyHigh)
            return RuleResult.Pass("Not a high risk trade");

        if (_configDisabled)
            return RuleResult.Fail(
                IsZeroDte(alert) && risk != "high"
                    ? "Rejected - 0DTE option treated as high risk (AllowHigh=false)"
                    : "Rejected - high risk trades are disabled (AllowHigh=false)");

        if (_isChoppy())
            return RuleResult.Fail(
                $"Rejected - high risk trades disabled (choppy market, chop score {_chopScore()}/4)");

        return RuleResult.Pass("High risk trade permitted");
    }

    // Returns true if this is an options alert expiring today in ET.
    private static bool IsZeroDte(Alert alert)
    {
        if (alert.Type?.ToLowerInvariant() != "options") return false;
        if (alert.Expiration is null) return false;
        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry)) return false;

        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        return DateOnly.FromDateTime(expiry.DateTime) == todayEt;
    }
}