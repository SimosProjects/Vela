namespace TradeFlow.AlertPoC.RiskEngine;

/// <summary>
/// Rejects same-day expiry option entries after a configurable ET hour.
/// 0DTE options entered late in the day have near-zero liquidity near close,
/// making trail stops ineffective and risking total loss from expiry.
/// </summary>
public class No0DTEAfterCutoffRule : IRiskRule
{
    private readonly int _cutoffHour;
    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public No0DTEAfterCutoffRule(int cutoffHour)
    {
        _cutoffHour = cutoffHour;
    }

    public RuleResult Evaluate(Alert alert)
    {
        // Only applies to option entries
        if (alert.Side is not ("bto" or "sto"))
            return RuleResult.Pass("Not an option entry");

        if (string.IsNullOrWhiteSpace(alert.Expiration))
            return RuleResult.Pass("No expiration date");

        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry))
            return RuleResult.Pass("Could not parse expiration date");

        var et = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime);
        var todayEt = DateOnly.FromDateTime(et.DateTime);
        var expiryDate = DateOnly.FromDateTime(expiry.DateTime);

        if (expiryDate != todayEt)
            return RuleResult.Pass("Expiry is not today");

        if (et.Hour < _cutoffHour)
            return RuleResult.Pass($"Same-day expiry entry allowed before {_cutoffHour}:00 ET");

        return RuleResult.Fail(
            $"Rejected - same-day expiry entry blocked after {_cutoffHour}:00 ET");
    }
}