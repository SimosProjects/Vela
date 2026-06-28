using Vela.Worker.Configuration;

namespace Vela.Api.Models;

/// <summary>
/// All dashboard-editable risk engine configuration values.
/// Returned by GET /api/config/risk (baseline from appsettings, overridden by DB save).
/// Accepted by POST /api/config/risk (persisted to risk_config_overrides).
/// </summary>
public record RiskConfigDto(
    // -- Scoring --
    double MinXScore,
    // -- Options budgets --
    decimal OptionsInitialBudget,
    decimal OptionsAverageBudget,
    decimal OptionsHighBudget,
    decimal OptionsHighAverageBudget,
    decimal OptionsLottoBudget,
    decimal OptionsLottoAverageBudget,
    decimal OptionsMaxBudget,
    // -- Options trails and target --
    double OptionsStandardTrailPct,
    double OptionsHighTrailPct,
    double OptionsLottoTrailPct,
    double OptionsTargetMultiple,
    // -- Stock budgets --
    decimal StockInitialBudget,
    decimal StockAverageBudget,
    decimal StockMaxBudget,
    // -- Stock trails and target --
    double StockStandardTrailPct,
    double StockHighTrailPct,
    double StockLottoTrailPct,
    double StockTargetMultiple,
    // -- Limits --
    decimal DailyLossLimit,
    decimal ChopDailyLossLimit
)
{
    /// <summary>Builds a DTO from the baseline appsettings values.</summary>
    public static RiskConfigDto FromOptions(RiskEngineOptions o) => new(
        MinXScore:                 o.MinXScore,
        OptionsInitialBudget:      o.OptionsInitialBudget,
        OptionsAverageBudget:      o.OptionsAverageBudget,
        OptionsHighBudget:         o.OptionsHighBudget,
        OptionsHighAverageBudget:  o.OptionsHighAverageBudget,
        OptionsLottoBudget:        o.OptionsLottoBudget,
        OptionsLottoAverageBudget: o.OptionsLottoAverageBudget,
        OptionsMaxBudget:          o.OptionsMaxBudget,
        OptionsStandardTrailPct:   o.OptionsStandardTrailPct,
        OptionsHighTrailPct:       o.OptionsHighTrailPct,
        OptionsLottoTrailPct:      o.OptionsLottoTrailPct,
        OptionsTargetMultiple:     o.OptionsTargetMultiple,
        StockInitialBudget:        o.StockInitialBudget,
        StockAverageBudget:        o.StockAverageBudget,
        StockMaxBudget:            o.StockMaxBudget,
        StockStandardTrailPct:     o.StockStandardTrailPct,
        StockHighTrailPct:         o.StockHighTrailPct,
        StockLottoTrailPct:        o.StockLottoTrailPct,
        StockTargetMultiple:       o.StockTargetMultiple,
        DailyLossLimit:            o.DailyLossLimit,
        ChopDailyLossLimit:        o.ChopDailyLossLimit
    );
}