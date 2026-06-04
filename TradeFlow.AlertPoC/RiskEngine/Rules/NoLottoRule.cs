namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects lotto risk alerts when lotto trading is disabled via config or when the
/// morning market regime classified the session as choppy.
/// Lotto is defined as any options alert expiring today (0DTE) or tomorrow (1DTE),
/// regardless of the risk label Xtrades assigns, some platforms mislabel these as standard.
/// Config flag acts as a permanent override; the isChoppy delegate is the dynamic daily layer.
/// </summary>
public class NoLottoRule : IRiskRule
{
    private readonly bool _configDisabled;
    private readonly Func<bool> _isChoppy;
    private readonly Func<int> _chopScore;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public NoLottoRule(bool configDisabled, Func<bool> isChoppy, Func<int> chopScore)
    {
        _configDisabled = configDisabled;
        _isChoppy       = isChoppy;
        _chopScore      = chopScore;
    }

    public RuleResult Evaluate(Alert alert)
    {
        var isLotto = alert.Risk?.ToLowerInvariant() == "lotto" || IsDateBasedLotto(alert);

        if (!isLotto)
            return RuleResult.Pass("Risk level acceptable");

        if (_configDisabled)
            return RuleResult.Fail(
                IsDateBasedLotto(alert) && alert.Risk?.ToLowerInvariant() != "lotto"
                    ? "Rejected - 0DTE/1DTE option classified as lotto (AllowLotto=false)"
                    : "Rejected - lotto risk alerts are excluded (AllowLotto=false)");

        if (_isChoppy())
            return RuleResult.Fail(
                $"Rejected - lotto trades disabled (choppy market, chop score {_chopScore()}/4)");

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