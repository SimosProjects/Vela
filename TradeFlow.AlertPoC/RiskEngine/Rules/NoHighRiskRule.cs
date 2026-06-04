namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects high risk alerts when high risk trading is disabled via config or when the
/// morning market regime classified the session as choppy.
/// High risk is defined as any options alert expiring within the current trading week
/// but beyond 1DTE, Xtrades sometimes mislabels these as standard.
/// Lotto (0DTE/1DTE) is handled separately by NoLottoRule and is not blocked here.
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
        var isHigh = alert.Risk?.ToLowerInvariant() == "high" || IsDateBasedHigh(alert);

        if (!isHigh)
            return RuleResult.Pass("Not a high risk trade");

        if (_configDisabled)
            return RuleResult.Fail(
                IsDateBasedHigh(alert) && alert.Risk?.ToLowerInvariant() != "high"
                    ? "Rejected - this-week expiry option classified as high risk (AllowHigh=false)"
                    : "Rejected - high risk trades are disabled (AllowHigh=false)");

        if (_isChoppy())
            return RuleResult.Fail(
                $"Rejected - high risk trades disabled (choppy market, chop score {_chopScore()}/4)");

        return RuleResult.Pass("High risk trade permitted");
    }

    // Returns true if this options alert expires this trading week but beyond 1DTE.
    // 0DTE and 1DTE are lotto territory and are excluded here, NoLottoRule handles those.
    private static bool IsDateBasedHigh(Alert alert)
    {
        if (alert.Type?.ToLowerInvariant() != "options") return false;
        if (alert.Expiration is null) return false;
        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry)) return false;

        var todayEt    = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);
        var expiryDate = DateOnly.FromDateTime(expiry.DateTime);
        var weekEnd    = GetFridayOfWeek(todayEt);

        // Beyond 1DTE but still within this trading week
        return expiryDate > todayEt.AddDays(1) && expiryDate <= weekEnd;
    }

    // Returns the Friday of the week containing the given date.
    // Saturday wraps forward to the next Friday.
    private static DateOnly GetFridayOfWeek(DateOnly date)
    {
        var day = (int)date.DayOfWeek;
        var daysToFriday = day == 6 ? 6 : 5 - day;
        return date.AddDays(daysToFriday);
    }
}