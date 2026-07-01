using Vela.Worker.Models;

namespace Vela.Worker.Engine;

// Converts an approved Alert into a TradeOrder with sizing, stop, target, and trail prices.
// Position sizing uses independent budgets per risk tier, all configurable:
//   Options standard: OptionsInitialBudget / OptionsAverageBudget
//   Options high:     OptionsHighBudget    / OptionsHighAverageBudget
//   Options lotto:    OptionsLottoBudget   / OptionsLottoAverageBudget
//   Stocks:           StockInitialBudget   / StockAverageBudget
//
// Risk tier is auto-classified for options based on expiry:
//   Expires today          -> lotto (regardless of Xtrades label)
//   Expires this week      -> high  (regardless of Xtrades label)
//   Expires beyond week    -> use Xtrades risk label as-is
//
// Trailing stop percentages are risk-tiered and configurable via RiskEngineOptions.
//
// Limit price is computed per risk tier from the per-risk slippage thresholds.
// Lotto always uses a market order (LimitPrice = null).
// When a threshold is 0, that tier falls back to market order.
//
// Trader restrictions — if a trader appears in RestrictedTraders, their budget is scaled
// by the configured allocation percentage (0 = blocked, 25 = 25% of normal budget, etc).
//
// MaxBudget fallback — when OptionsMaxBudget or StockMaxBudget is > 0 and the
// regime-adjusted budget cannot afford any contracts, the sizer falls back to exactly
// 1 contract if the single-contract cost is at or below the configured ceiling.
// Regime scaling is bypassed on this path since the goal is guaranteed execution.
//
// Price target — when Alert.PriceTarget is non-null and above the entry price, it is used
// directly as the target instead of the configured multiplier. Populated by Spyglass based
// on setup type; always null for Xtrades alerts which use the multiplier as before.
public class PositionSizer
{
    private readonly RiskEngineOptions _options;
    private readonly MarketRegimeService? _regime;
    private readonly ILogger<PositionSizer> _logger;

    private const decimal OptionsStopMultiplier = 0.50m;
    private const decimal StockStopMultiplier = 0.85m;
    private const int MinQuantity = 1;

    public PositionSizer(IOptions<RiskEngineOptions> options, ILogger<PositionSizer> logger, MarketRegimeService? regime = null)
    {
        _options = options.Value;
        _regime = regime;
        _logger = logger;
    }

    public TradeOrder? Size(Alert alert, AlertClassification classification, bool isAverage = false)
    {
        var price = alert.PricePaid;
        if (price is null or <= 0)
        {
            _logger.LogWarning(
                "PositionSizer null — {Symbol}: price is null or zero (PricePaid={Paid}, ActualPrice={Actual})",
                alert.Symbol, alert.PricePaid, alert.ActualPriceAtTimeOfAlert);
            return null;
        }

        var tradeType = classification.Category switch
        {
            AlertCategory.CallOptionEntry or
            AlertCategory.PutOptionEntry => TradeType.Options,
            AlertCategory.StockEntry => TradeType.Stock,
            _ => (TradeType?)null
        };

        if (tradeType is null)
        {
            _logger.LogWarning(
                "PositionSizer null — {Symbol}: unrecognised classification {Category}",
                alert.Symbol, classification.Category);
            return null;
        }

        var isOptions = tradeType == TradeType.Options;

        var effectiveRisk = isOptions
            ? ClassifyOptionsRisk(alert)
            : (alert.Risk?.ToLowerInvariant() ?? "standard");

        var budget = isOptions
            ? effectiveRisk switch
            {
                "lotto" => isAverage ? _options.OptionsLottoAverageBudget : _options.OptionsLottoBudget,
                "high" => isAverage ? _options.OptionsHighAverageBudget : _options.OptionsHighBudget,
                _ => isAverage ? _options.OptionsAverageBudget : _options.OptionsInitialBudget,
            }
            : (isAverage ? _options.StockAverageBudget : _options.StockInitialBudget);

        // Apply regime-aware sizing multiplier, set once at market open by MarketConditionsLogger.
        // Multiplier is 1.0 (Bullish), 0.5 (Choppy), or 0.25 (Bearish) by default.
        // Lotto budget is not scaled, it is already sized for maximum risk tolerance.
        if (effectiveRisk != "lotto" && _regime is not null)
            budget = budget * _regime.SizingMultiplier;

        // Apply trader restriction if configured to scale budget by allocation percentage.
        var userName = alert.UserName ?? string.Empty;
        if (_options.RestrictedTraders.TryGetValue(userName, out var allocationPct))
        {
            if (allocationPct <= 0)
            {
                _logger.LogWarning(
                    "PositionSizer null — {Symbol}: trader {Trader} has 0% allocation",
                    alert.Symbol, userName);
                return null;
            }

            budget = budget * allocationPct / 100m;
        }

        var quantity = isOptions
            ? (int)(budget / (price.Value * 100))
            : (int)(budget / price.Value);

        if (quantity < MinQuantity)
        {
            var contractCost = isOptions ? price.Value * 100 : price.Value;
            var maxBudget = isOptions ? _options.OptionsMaxBudget : _options.StockMaxBudget;

            if (maxBudget > 0 && contractCost <= maxBudget)
            {
                // Normal budget (after regime scaling) cannot afford any contracts, but the
                // single-contract cost is within the configured ceiling — execute 1 contract.
                // Regime scaling is intentionally bypassed on this path.
                _logger.LogInformation(
                    "PositionSizer: {Symbol} below normal budget — executing 1 contract via MaxBudget " +
                    "(cost:{Cost:F0} <= max:{Max:F0}, risk={Risk}, trader={Trader})",
                    alert.Symbol,
                    contractCost,
                    maxBudget,
                    effectiveRisk,
                    string.IsNullOrEmpty(userName) ? "unknown" : userName);
                quantity = MinQuantity;
            }
            else
            {
                if (isOptions)
                {
                    _logger.LogWarning(
                        "PositionSizer null — {Symbol}: ${Price:F2}/contract = ${Cost:F0} per contract, " +
                        "budget is ${Budget:F0} (risk={Risk}, trader={Trader}). Need ${Need:F0} for 1 contract.",
                        alert.Symbol,
                        price.Value,
                        price.Value * 100,
                        budget,
                        effectiveRisk,
                        string.IsNullOrEmpty(userName) ? "unknown" : userName,
                        price.Value * 100);
                }
                else
                {
                    _logger.LogWarning(
                        "PositionSizer null — {Symbol}: ${Price:F2}/share, budget is ${Budget:F0}. " +
                        "Need ${Need:F0} for 1 share.",
                        alert.Symbol, price.Value, budget, price.Value);
                }

                return null;
            }
        }

        var stopPrice = isOptions
            ? price.Value * OptionsStopMultiplier
            : price.Value * StockStopMultiplier;

        // Use the alert's computed price target when provided and above entry price.
        // Falls back to the configured multiplier for all Xtrades alerts (PriceTarget is always null)
        // and any Spyglass alert where the setup cannot produce a reliable projection.
        var targetPrice = alert.PriceTarget.HasValue && alert.PriceTarget.Value > price.Value
            ? alert.PriceTarget.Value
            : isOptions
                ? price.Value * (decimal)_options.OptionsTargetMultiple
                : price.Value * (decimal)_options.StockTargetMultiple;

        if (alert.PriceTarget.HasValue && alert.PriceTarget.Value > price.Value)
            _logger.LogDebug(
                "PositionSizer: {Symbol} using alert-supplied target {Target:F2} (multiplier would give {Multiplier:F2})",
                alert.Symbol,
                alert.PriceTarget.Value,
                isOptions
                    ? price.Value * (decimal)_options.OptionsTargetMultiple
                    : price.Value * (decimal)_options.StockTargetMultiple);

        var trailPercent = ResolveTrailPercent(isOptions, effectiveRisk);
        var limitPrice = ComputeLimitPrice(isOptions, effectiveRisk, price.Value);

        var budgetUsed = isOptions
            ? quantity * price.Value * 100
            : quantity * price.Value;

        return new TradeOrder(
            AlertId: alert.Id ?? string.Empty,
            UserName: alert.UserName ?? string.Empty,
            Symbol: alert.Symbol ?? string.Empty,
            TradeType: tradeType.Value,
            OptionsContractSymbol: alert.OptionsContractSymbol,
            Direction: alert.Direction,
            Strike: alert.Strike,
            Expiration: alert.Expiration,
            Quantity: quantity,
            EstimatedEntryPrice: price.Value,
            BudgetUsed: budgetUsed,
            StopPrice: stopPrice,
            TargetPrice: targetPrice,
            TrailPercent: trailPercent,
            LimitPrice: limitPrice,
            IsAverage: isAverage,
            XScore: (decimal)(alert.XScore ?? 0),
            DiscordRank: alert.DiscordRank);
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

        var et = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var todayEt = DateOnly.FromDateTime(et.DateTime);
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
            (true, "lotto") => _options.OptionsLottoTrailPct,
            (true, "high") => _options.OptionsHighTrailPct,
            (true, _) => _options.OptionsStandardTrailPct,
            (false, "lotto") => _options.StockLottoTrailPct,
            (false, "high") => _options.StockHighTrailPct,
            (false, _) => _options.StockStandardTrailPct,
        };

    // Returns the limit order ceiling price for the given risk tier and entry price.
    // Lotto always returns null, lotto entries use market orders.
    // Returns null when the configured threshold is 0 (disabled for that tier).
    private decimal? ComputeLimitPrice(bool isOptions, string effectiveRisk, decimal price)
    {
        if (effectiveRisk == "lotto")
            return null;

        var threshold = isOptions
            ? effectiveRisk == "high"
                ? _options.OptionsHighMaxSlippagePct
                : _options.OptionsStandardMaxSlippagePct
            : _options.StockMaxSlippagePct;

        if (threshold <= 0)
            return null;

        return Math.Round(price * (1 + threshold / 100), 2);
    }
}