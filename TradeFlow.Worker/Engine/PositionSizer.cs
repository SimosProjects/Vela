using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Converts an approved Alert into a TradeOrder with sizing, stop, target, and trail prices.
// Fixed sizing rules for paper trading phase:
//   Options: $2,000 initial, $1,000 average
//   Stocks:  $3,000 initial, $1,500 average
//
// Risk tier is auto-classified for options based on expiry:
//   Expires today          → lotto (regardless of Xtrades label)
//   Expires this week      → high  (regardless of Xtrades label)
//   Expires beyond week    → use Xtrades risk label as-is
//
// Trailing stop percentages are risk-tiered and configurable via RiskEngineOptions.
//
// Trader restrictions — if a trader appears in RestrictedTraders, their budget is scaled
// by the configured allocation percentage (0 = blocked, 25 = 25% of normal budget, etc).
public class PositionSizer
{
    private readonly RiskEngineOptions _options;

    private const decimal OptionsStopMultiplier = 0.50m; // -50% initial bracket stop
    private const decimal StockStopMultiplier   = 0.85m; // -15% initial bracket stop

    private const int MinQuantity = 1;

    public PositionSizer(IOptions<RiskEngineOptions> options)
    {
        _options = options.Value;
    }

    public TradeOrder? Size(Alert alert, AlertClassification classification, bool isAverage = false)
    {
        var price = alert.PricePaid;
        if (price is null or <= 0)
            return null;

        var tradeType = classification.Category switch
        {
            AlertCategory.CallOptionEntry or
            AlertCategory.PutOptionEntry  => TradeType.Options,
            AlertCategory.StockEntry      => TradeType.Stock,
            _                             => (TradeType?)null
        };

        if (tradeType is null)
            return null;

        var isOptions = tradeType == TradeType.Options;

        var effectiveRisk = isOptions
            ? ClassifyOptionsRisk(alert)
            : (alert.Risk?.ToLowerInvariant() ?? "standard");

        var budget = isOptions
            ? effectiveRisk == "lotto"
                ? (isAverage ? _options.OptionsLottoAverageBudget : _options.OptionsLottoBudget)
                : (isAverage ? _options.OptionsAverageBudget : _options.OptionsInitialBudget)
            : (isAverage ? _options.StockAverageBudget : _options.StockInitialBudget);

        // Apply trader restriction if configured to scale budget by allocation percentage.
        var userName = alert.UserName ?? string.Empty;
        if (_options.RestrictedTraders.TryGetValue(userName, out var allocationPct))
        {
            if (allocationPct <= 0)
                return null;

            budget = budget * allocationPct / 100m;
        }

        var quantity = isOptions
            ? (int)(budget / (price.Value * 100))
            : (int)(budget / price.Value);

        if (quantity < MinQuantity)
            return null;

        var stopPrice = isOptions
            ? price.Value * OptionsStopMultiplier
            : price.Value * StockStopMultiplier;

        var targetPrice = isOptions
            ? price.Value * (decimal)_options.OptionsTargetMultiple
            : price.Value * (decimal)_options.StockTargetMultiple;

        var trailPercent = ResolveTrailPercent(isOptions, effectiveRisk);

        var budgetUsed = isOptions
            ? quantity * price.Value * 100
            : quantity * price.Value;

        return new TradeOrder(
            AlertId:               alert.Id ?? string.Empty,
            UserName:              alert.UserName ?? string.Empty,
            Symbol:                alert.Symbol   ?? string.Empty,
            TradeType:             tradeType.Value,
            OptionsContractSymbol: alert.OptionsContractSymbol,
            Direction:             alert.Direction,
            Strike:                alert.Strike,
            Expiration:            alert.Expiration,
            Quantity:              quantity,
            EstimatedEntryPrice:   price.Value,
            BudgetUsed:            budgetUsed,
            StopPrice:             stopPrice,
            TargetPrice:           targetPrice,
            TrailPercent:          trailPercent,
            IsAverage:             isAverage);
    }

    // -- Helpers --

    // Auto-classifies options risk based on expiry relative to today ET.
    // Overrides the Xtrades risk label for near-term expiries.
    private static string ClassifyOptionsRisk(Alert alert)
    {
        if (alert.Expiration is null)
            return alert.Risk?.ToLowerInvariant() ?? "standard";

        if (!DateTimeOffset.TryParse(alert.Expiration, out var expiry))
            return alert.Risk?.ToLowerInvariant() ?? "standard";

        var et         = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var todayEt    = DateOnly.FromDateTime(et.DateTime);
        var expiryDate = DateOnly.FromDateTime(expiry.DateTime);

        if (expiryDate == todayEt)
            return "lotto";

        var endOfWeekEt = todayEt.AddDays((int)DayOfWeek.Friday - (int)todayEt.DayOfWeek);
        if (expiryDate <= endOfWeekEt)
            return "high";

        return alert.Risk?.ToLowerInvariant() ?? "standard";
    }

    private double ResolveTrailPercent(bool isOptions, string effectiveRisk) =>
        (isOptions, effectiveRisk) switch
        {
            (true,  "lotto") => _options.OptionsLottoTrailPct,
            (true,  "high")  => _options.OptionsHighTrailPct,
            (true,  _)       => _options.OptionsStandardTrailPct,
            (false, "lotto") => _options.StockLottoTrailPct,
            (false, "high")  => _options.StockHighTrailPct,
            (false, _)       => _options.StockStandardTrailPct,
        };
}