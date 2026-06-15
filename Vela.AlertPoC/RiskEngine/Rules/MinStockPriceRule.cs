namespace Vela.AlertPoC.RiskEngine;

/// <summary>
/// Rejects stock entry alerts where the alert price is below a configured minimum.
/// Prevents trading penny stocks and low-liquidity equities that carry outsized
/// gap-down risk and are prone to stream corruption in IB Gateway's market data feed.
/// Only applies to commons (stock) entries — options are not subject to this rule
/// since a low option premium does not indicate the same structural risk.
/// </summary>
public class MinStockPriceRule : IRiskRule
{
    private readonly decimal _minimumPrice;

    // Threshold injected so it can be configured per environment via IOptions<RiskEngineOptions>.
    // Defaults to $3.00 which excludes OTC penny stocks while allowing small-caps.
    public MinStockPriceRule(decimal minimumPrice = 3.00m)
    {
        _minimumPrice = minimumPrice;
    }

    public RuleResult Evaluate(Alert alert)
    {
        // Only applies to stock entries — options use a different risk profile
        var type = alert.Type?.ToLowerInvariant();
        if (type != "commons")
            return RuleResult.Pass("Not a stock alert — price floor does not apply");

        var price = alert.ActualPriceAtTimeOfAlert ?? alert.PricePaid;

        if (price is null)
            return RuleResult.Fail("Rejected — stock alert has no price to evaluate");

        return price >= _minimumPrice
            ? RuleResult.Pass($"Stock price ${price:F2} meets minimum threshold of ${_minimumPrice:F2}")
            : RuleResult.Fail($"Rejected — stock price ${price:F2} is below minimum ${_minimumPrice:F2} (penny stock filter)");
    }
}