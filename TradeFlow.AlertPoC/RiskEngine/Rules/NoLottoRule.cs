namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects lotto risk alerts when the session-level block is active.
/// Lotto is defined as any options alert expiring today (0DTE) or tomorrow (1DTE),
/// regardless of the risk label Xtrades assigns — some platforms mislabel these as standard.
/// The isBlocked delegate is driven by the dashboard toggle, seeded from the regime
/// on startup (Choppy or Bearish seeds ON; Bullish seeds OFF).
/// </summary>
public class NoLottoRule : IRiskRule
{
    private readonly Func<bool> _isBlocked;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public NoLottoRule(Func<bool> isBlocked)
    {
        _isBlocked = isBlocked;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var isLotto = alert.Risk?.ToLowerInvariant() == "lotto" || IsDateBasedLotto(alert);
        if (!isLotto)
            return RuleResult.Pass("Risk level acceptable");

        if (_isBlocked())
            return RuleResult.Fail(
                IsDateBasedLotto(alert) && alert.Risk?.ToLowerInvariant() != "lotto"
                    ? "Rejected — 0DTE/1DTE option classified as lotto (session block active)"
                    : "Rejected — lotto risk trades are blocked this session");

        return RuleResult.Pass("Lotto trade permitted");
    }

    // Returns true if this options alert expires today (0DTE) or tomorrow (1DTE).
    private static bool IsDateBasedLotto(Alert alert)
    {
        if (alert.Type?.ToLowerInvariant() != "options") return false;
        if (alert.Expiration is null) return false;
        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry)) return false;

        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        return DateOnly.FromDateTime(expiry.DateTime) <= todayEt.AddDays(1);
    }
}