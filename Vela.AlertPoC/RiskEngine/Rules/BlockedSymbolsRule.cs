namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Rejects alerts for symbols that are explicitly blocked in configuration.
/// Used to exclude cash-settled index options (SPX, NDX, RUT, VIX) which have
/// elevated margin requirements incompatible with the account size.
/// </summary>
public class BlockedSymbolsRule : IRiskRule
{
    private readonly HashSet<string> _blockedSymbols;

    public BlockedSymbolsRule(IEnumerable<string> blockedSymbols)
    {
        _blockedSymbols = blockedSymbols
            .Select(s => s.Trim().ToUpperInvariant())
            .ToHashSet();
    }

    public RuleResult Evaluate(Alert alert)
    {
        var symbol = (alert.Symbol ?? "").Trim().ToUpperInvariant();

        return _blockedSymbols.Contains(symbol)
            ? RuleResult.Fail($"Rejected - {alert.Symbol} is in the blocked symbols list")
            : RuleResult.Pass($"{alert.Symbol} is not blocked");
    }
}