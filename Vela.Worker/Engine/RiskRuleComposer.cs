namespace Vela.Worker.Engine;

/// <summary>
/// Builds the ordered risk rule list from configuration. Order is significant because the
/// engine returns the first failure.
/// </summary>
public static class RiskRuleComposer
{
    public static List<IRiskRule> Compose(
        RiskEngineOptions riskOptions,
        Func<bool> blockHigh,
        Func<bool> blockLotto,
        Func<bool> blockCalls)
    {
        var rules = new List<IRiskRule>
        {
            new EntryOnlyRule(),
            new AllowOptionsRule(riskOptions.AllowOptions),
            new ApprovedOrHighScoreRule(riskOptions.ApprovedTraders, riskOptions.MinXScore),
            new NoHighRiskRule(blockHigh),
            new NoLottoRule(blockLotto),
        };

        if (riskOptions.RegimeBearishBlockCalls)
            rules.Add(new BearishCallBlockRule(blockCalls));

        if (riskOptions.MinStockPriceDollars > 0)
            rules.Insert(1, new MinStockPriceRule(riskOptions.MinStockPriceDollars));

        rules.Insert(1, new No0DTEAfterCutoffRule(riskOptions.ZeroDteEntryCutoffHour));

        // Sits directly after EntryOnlyRule so exits keep their side-rejection reason, which
        // SignalRListenerService uses to downgrade that log line to Debug.
        if (riskOptions.SpyglassOnlyMode)
            rules.Insert(rules.FindIndex(r => r is EntryOnlyRule) + 1, new SpyglassOnlyRule());

        if (riskOptions.BlockedSymbols.Count > 0)
            rules.Insert(0, new BlockedSymbolsRule(riskOptions.BlockedSymbols));

        return rules;
    }
}
