namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects high risk alerts when the session-level block is active.
/// High risk is defined as any options alert expiring within the current trading week
/// but beyond 1DTE. Xtrades sometimes mislabels these as standard.
/// Lotto (0DTE/1DTE) is handled separately by NoLottoRule.
/// The isBlocked delegate is driven by the dashboard toggle, seeded from the regime
/// on startup (Choppy or Bearish seeds ON; Bullish seeds OFF).
/// </summary>
public class NoHighRiskRule : IRiskRule
{
    private readonly Func<bool> _isBlocked;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public NoHighRiskRule(Func<bool> isBlocked)
    {
        _isBlocked = isBlocked;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var isHigh = alert.Risk?.ToLowerInvariant() == "high" || IsDateBasedHigh(alert);
        if (!isHigh)
            return RuleResult.Pass("Not a high risk trade");

        if (_isBlocked())
            return RuleResult.Fail(
                IsDateBasedHigh(alert) && alert.Risk?.ToLowerInvariant() != "high"
                    ? "Rejected — this-week expiry option classified as high risk (session block active)"
                    : "Rejected — high risk trades are blocked this session");

        return RuleResult.Pass("High risk trade permitted");
    }

    // Returns true if this options alert expires this trading week but beyond 1DTE.
    // 0DTE and 1DTE are lotto territory — NoLottoRule handles those.
    private static bool IsDateBasedHigh(Alert alert)
    {
        if (alert.Type?.ToLowerInvariant() != "options") return false;
        if (alert.Expiration is null) return false;
        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry)) return false;

        var todayEt    = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);
        var expiryDate = DateOnly.FromDateTime(expiry.DateTime);
        var weekEnd    = GetFridayOfWeek(todayEt);

        return expiryDate > todayEt.AddDays(1) && expiryDate <= weekEnd;
    }

    private static DateOnly GetFridayOfWeek(DateOnly date)
    {
        var day = (int)date.DayOfWeek;
        var daysToFriday = day == 6 ? 6 : 5 - day;
        return date.AddDays(daysToFriday);
    }
}