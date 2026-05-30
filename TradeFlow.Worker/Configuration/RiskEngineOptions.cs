using System.ComponentModel.DataAnnotations;

namespace TradeFlow.Worker.Configuration;

public class RiskEngineOptions
{
    public const string SectionName = "RiskEngine";

    public bool TradingPaused { get; init; } = false;

    [Range(0, 100, ErrorMessage = "MinXScore must be between 0 and 100.")]
    public int MinXScore { get; init; } = 60;

    [MinLength(1, ErrorMessage = "At least one approved trader must be specified.")]
    public List<string> ApprovedTraders { get; init; } = [];

    // Trader allocation restrictions, keyed by trader username, value is allocation percentage.
    // 100 = full budget
    //  50 = 50% of normal budget
    //  25 = 25% of normal budget
    //   0 = fully blocked, no trades allowed
    // Useful for restricting underperforming traders without removing them entirely.
    public Dictionary<string, int> RestrictedTraders { get; init; } = [];

    public bool AllowLotto { get; init; } = false;

    public bool AllowHigh { get; init; } = true;

    public bool SkipTradeOnSlippageTimeout { get; init; } = true;

    // Symbols blocked from trading regardless of other rules.
    // Used to exclude cash-settled index options (SPX, NDX, RUT, VIX, DJX)
    // which have elevated margin requirements incompatible with the account size.
    public List<string> BlockedSymbols { get; init; } = [];

    // Minimum stock price in dollars. Stock entry alerts below this threshold are rejected.
    // Defaults to $3.00 to exclude penny stocks and OTC equities with high gap-down risk.
    // Set to 0 to disable the filter entirely.
    [Range(0, 10000, ErrorMessage = "MinStockPriceDollars must be between 0 and 10000.")]
    public decimal MinStockPriceDollars { get; init; } = 3.00m;

    // Trailing stop percentages by risk tier — sent to IBKR when placing OCA orders.
    // Options trail more aggressively than stocks due to higher price volatility.
    [Range(1, 100, ErrorMessage = "OptionsStandardTrailPct must be between 1 and 100.")]
    public double OptionsStandardTrailPct { get; init; } = 40.0;

    [Range(1, 100, ErrorMessage = "OptionsHighTrailPct must be between 1 and 100.")]
    public double OptionsHighTrailPct { get; init; } = 50.0;

    public decimal OptionsLottoBudget        { get; init; } = 500m;
    public decimal OptionsLottoAverageBudget { get; init; } = 250m;

    [Range(1, 100, ErrorMessage = "OptionsLottoTrailPct must be between 1 and 100.")]
    public double OptionsLottoTrailPct { get; init; } = 50.0;

    [Range(1, 100, ErrorMessage = "StockStandardTrailPct must be between 1 and 100.")]
    public double StockStandardTrailPct { get; init; } = 10.0;

    [Range(1, 100, ErrorMessage = "StockHighTrailPct must be between 1 and 100.")]
    public double StockHighTrailPct { get; init; } = 15.0;

    [Range(1, 100, ErrorMessage = "StockLottoTrailPct must be between 1 and 100.")]
    public double StockLottoTrailPct { get; init; } = 20.0;

    [Range(0, 100, ErrorMessage = "MaxEntrySlippagePct must be between 0 and 100.")]
    public decimal MaxEntrySlippagePct { get; init; } = 5.0m;

    // Maximum percentage of effective account balance that can be deployed per day.
    // When open positions value reaches this threshold, new entries are blocked.
    // Defaults to 30% — limits daily exposure to $22,500 on a $75,000 account.
    [Range(1, 100, ErrorMessage = "MaxDailyExposurePct must be between 1 and 100.")]
    public double MaxDailyExposurePct { get; init; } = 30.0;

    // Optional margin multiplier applied to account balance before calculating exposure.
    // Set to 0 for cash accounts. Set to 1.0 to allow up to 2x margin deployment.
    [Range(0, 10, ErrorMessage = "MarginPct must be between 0 and 10.")]
    public double MarginPct { get; init; } = 0.0;

    // Hour (ET) after which same-day expiry option entries are blocked.
    // Defaults to 12 (noon ET) — 0DTE entries after this time risk total loss
    // due to liquidity drying up near close.
    [Range(0, 23, ErrorMessage = "ZeroDteEntryCutoffHour must be between 0 and 23.")]
    public int ZeroDteEntryCutoffHour { get; init; } = 12;

    // Time (ET, HH:mm) at which any open same-day expiry options are force-closed.
    // Defaults to 15:30 — gives 30 minutes buffer before close to exit while
    // liquidity still exists, preventing total loss from expiry.
    public string SameDayExpiryAutoCloseCutoff { get; init; } = "15:30";

    [Range(1, 100, ErrorMessage = "OptionsTargetMultiple must be between 1 and 100.")]
    public double OptionsTargetMultiple { get; init; } = 3.0;

    [Range(1, 100, ErrorMessage = "StockTargetMultiple must be between 1 and 100.")]
    public double StockTargetMultiple { get; init; } = 1.3;

    public decimal OptionsInitialBudget { get; init; } = 2_000m;
    public decimal OptionsAverageBudget { get; init; } = 1_000m;
    public decimal StockInitialBudget   { get; init; } = 3_000m;
    public decimal StockAverageBudget   { get; init; } = 1_500m;
}