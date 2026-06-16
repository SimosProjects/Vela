using System.ComponentModel.DataAnnotations;

namespace Vela.Worker.Configuration;

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
    public Dictionary<string, int> RestrictedTraders { get; init; } = [];

    public bool AllowLotto { get; init; } = false;

    public bool AllowHigh { get; init; } = true;

    public List<string> BlockedSymbols { get; init; } = [];

    [Range(0, 10000, ErrorMessage = "MinStockPriceDollars must be between 0 and 10000.")]
    public decimal MinStockPriceDollars { get; init; } = 3.00m;

    [Range(1, 100, ErrorMessage = "OptionsStandardTrailPct must be between 1 and 100.")]
    public double OptionsStandardTrailPct { get; init; } = 40.0;

    [Range(1, 100, ErrorMessage = "OptionsHighTrailPct must be between 1 and 100.")]
    public double OptionsHighTrailPct { get; init; } = 50.0;
    public decimal OptionsLottoBudget { get; init; } = 500m;
    public decimal OptionsLottoAverageBudget { get; init; } = 250m;
    public decimal OptionsHighBudget { get; init; } = 2_000m;
    public decimal OptionsHighAverageBudget { get; init; } = 1_000m;

    [Range(1, 100, ErrorMessage = "OptionsLottoTrailPct must be between 1 and 100.")]
    public double OptionsLottoTrailPct { get; init; } = 50.0;

    [Range(1, 100, ErrorMessage = "StockStandardTrailPct must be between 1 and 100.")]
    public double StockStandardTrailPct { get; init; } = 10.0;

    [Range(1, 100, ErrorMessage = "StockHighTrailPct must be between 1 and 100.")]
    public double StockHighTrailPct { get; init; } = 15.0;

    [Range(1, 100, ErrorMessage = "StockLottoTrailPct must be between 1 and 100.")]
    public double StockLottoTrailPct { get; init; } = 20.0;

    // 0 = disabled. Rejects alerts where PricePaid exceeds ActualPriceAtTimeOfAlert by more than this %.
    // Catches already-running trades before any IBKR interaction.
    // Applied to options entries. For stocks use StockAlertStalenessMaxSlippagePct.
    [Range(0, 100, ErrorMessage = "AlertStalenessMaxSlippagePct must be between 0 and 100.")]
    public decimal AlertStalenessMaxSlippagePct { get; init; } = 25.0m;

    // 0 = disabled (falls back to AlertStalenessMaxSlippagePct).
    // Stocks move less than options; a tighter threshold rejects entries where the
    // price has already moved away from the alerted level before we can fill.
    // The goal is to enter near the alerted price or not at all.
    [Range(0, 100, ErrorMessage = "StockAlertStalenessMaxSlippagePct must be between 0 and 100.")]
    public decimal StockAlertStalenessMaxSlippagePct { get; init; } = 2.0m;

    // Maximum acceptable fill price above PricePaid per risk tier.
    // Used to compute the limit order ceiling: PricePaid * (1 + threshold / 100).
    // 0 = disabled for that tier (falls back to market order).
    [Range(0, 100, ErrorMessage = "OptionsStandardMaxSlippagePct must be between 0 and 100.")]
    public decimal OptionsStandardMaxSlippagePct { get; init; } = 15.0m;

    [Range(0, 100, ErrorMessage = "OptionsHighMaxSlippagePct must be between 0 and 100.")]
    public decimal OptionsHighMaxSlippagePct { get; init; } = 20.0m;

    [Range(0, 100, ErrorMessage = "StockMaxSlippagePct must be between 0 and 100.")]
    public decimal StockMaxSlippagePct { get; init; } = 5.0m;

    // Post-fill slippage threshold above which the trail stop is tightened.
    // Compared against (fillPrice - alertedPrice) / alertedPrice * 100.
    // 0 = disabled; no tightening applied regardless of fill quality.
    [Range(0, 100, ErrorMessage = "PostFillSlippageWarningPct must be between 0 and 100.")]
    public double PostFillSlippageWarningPct { get; init; } = 10.0;

    // Trail percentage applied when PostFillSlippageWarningPct is exceeded.
    // Should be tighter than the risk-tier trail percentages.
    // 0 = disabled; original trail remains even when threshold is crossed.
    [Range(0, 100, ErrorMessage = "HighSlippageTrailPct must be between 0 and 100.")]
    public double HighSlippageTrailPct { get; init; } = 25.0;

    [Range(1, 100, ErrorMessage = "MaxDailyExposurePct must be between 1 and 100.")]
    public double MaxDailyExposurePct { get; init; } = 30.0;

    [Range(0, 100, ErrorMessage = "StockDailyAllocationPct must be between 0 and 100.")]
    public double StockDailyAllocationPct { get; init; } = 0.0;

    [Range(0, 10, ErrorMessage = "MarginPct must be between 0 and 10.")]
    public double MarginPct { get; init; } = 0.0;

    [Range(0, 23, ErrorMessage = "ZeroDteEntryCutoffHour must be between 0 and 23.")]
    public int ZeroDteEntryCutoffHour { get; init; } = 12;

    // Maximum open positions allowed per symbol across all traders.
    // Stocks and options are counted independently when MaxOptionsPositionsPerSymbol
    // or MaxStockPositionsPerSymbol are set; falls back to this value if they are 0.
    [Range(1, 10, ErrorMessage = "MaxPositionsPerSymbol must be between 1 and 10.")]
    public int MaxPositionsPerSymbol { get; init; } = 1;

    // Per-type symbol cap for options. 0 = fall back to MaxPositionsPerSymbol.
    // When set, options and stocks on the same underlying count against separate caps.
    [Range(0, 10, ErrorMessage = "MaxOptionsPositionsPerSymbol must be between 0 and 10.")]
    public int MaxOptionsPositionsPerSymbol { get; init; } = 0;

    // Per-type symbol cap for stocks. 0 = fall back to MaxPositionsPerSymbol.
    [Range(0, 10, ErrorMessage = "MaxStockPositionsPerSymbol must be between 0 and 10.")]
    public int MaxStockPositionsPerSymbol { get; init; } = 0;

    public string SameDayExpiryAutoCloseCutoff { get; init; } = "15:30";

    [Range(1, 100, ErrorMessage = "OptionsTargetMultiple must be between 1 and 100.")]
    public double OptionsTargetMultiple { get; init; } = 3.0;

    [Range(1, 100, ErrorMessage = "StockTargetMultiple must be between 1 and 100.")]
    public double StockTargetMultiple { get; init; } = 1.3;

    public decimal OptionsInitialBudget { get; init; } = 2_000m;
    public decimal OptionsAverageBudget { get; init; } = 1_000m;
    public decimal StockInitialBudget   { get; init; } = 3_000m;
    public decimal StockAverageBudget   { get; init; } = 1_500m;

    [Range(0, 1000, ErrorMessage = "OptionCloseThresholdPct must be between 0 and 1000.")]
    public double OptionCloseThresholdPct { get; init; } = 50.0;

    [Range(0.01, 1.0, ErrorMessage = "OptionPartialCloseRatio must be between 0.01 and 1.0.")]
    public double OptionPartialCloseRatio { get; init; } = 0.5;

    public decimal DailyLossLimit { get; init; } = 0m;

    // -- Choppy market regime config --

    // VIX day-over-day spike % that counts as a chop signal.
    [Range(0, 100)]
    public double ChopVixSpikePct { get; init; } = 3.0;

    // SPY ADX below this threshold counts as a chop signal (no clear trend).
    [Range(0, 100)]
    public double ChopAdxThreshold { get; init; } = 20.0;

    // SPY above its 50MA by more than this % counts as a chop signal (extended, pullback risk).
    [Range(0, 100)]
    public double ChopSpyExtendedPct { get; init; } = 7.0;

    // VIX level above this counts as a chop signal (elevated fear).
    [Range(0, 200)]
    public double ChopVixLevel { get; init; } = 20.0;

    // Number of chop signals required to declare a choppy regime (0-4).
    [Range(1, 4)]
    public int ChopMinSignals { get; init; } = 2;

    // Daily loss limit applied when the session is classified as choppy.
    // Must be negative to activate. Set to 0 to disable.
    public decimal ChopDailyLossLimit { get; init; } = 0m;

    // Minimum difference between -DI and +DI to flag as a strong bearish trend.
    public double ChopBearishDiDiff { get; init; } = 5.0;

    // Minimum % below 50MA to trigger the bearish structure signal (0 = any amount below).
    public double ChopSpyBelowMaPct { get; init; } = 0.0;

    // Position sizing multiplier when regime is Bullish (SPY above 20MA, VIX calm).
    // 1.0 = full budget. Applied to both options and stock initial/average budgets.
    [Range(0.1, 1.0)]
    public double RegimeBullishSizingPct { get; init; } = 1.0;

    // Position sizing multiplier when regime is Choppy (SPY near 20MA or VIX elevated).
    [Range(0.1, 1.0)]
    public double RegimeChoppySizingPct { get; init; } = 0.5;

    // Position sizing multiplier when regime is Bearish (SPY below 20MA, VIX high).
    [Range(0.1, 1.0)]
    public double RegimeBearishSizingPct { get; init; } = 0.25;

    // When true and regime is Bearish, call option entries are blocked for the session.
    public bool RegimeBearishBlockCalls { get; init; } = true;

    // VIX level that, combined with SPY below 20MA, triggers the Bearish regime.
    [Range(0, 100)]
    public double RegimeVixBearishThreshold { get; init; } = 20.0;

    // VIX level that triggers Choppy when SPY is above 20MA but fear is elevated.
    [Range(0, 100)]
    public double RegimeVixChoppyThreshold { get; init; } = 18.0;

    // % deviation below 50MA that triggers the Bearish regime master cap
    // regardless of 20MA position. 0 = disabled.
    [Range(0, 10)]
    public double RegimeSpyBelow50MaPct { get; init; } = 0.0;

    // SPY intraday drop percentage from the session open that triggers a one-tier step-down
    // at the next checkpoint, independent of the MA cascade. 0 = disabled.
    // Example: 2.0 means SPY dropping 2% from 9:20am forces Bullish to Choppy or Choppy to Bearish.
    [Range(0, 20)]
    public double RegimeSpyShockDownPct { get; init; } = 2.0;

    // VIX intraday spike percentage from the session open that triggers a one-tier step-down
    // at the next checkpoint, independent of the MA cascade. 0 = disabled.
    // Example: 20.0 means VIX spiking 20% from 9:20am opening level forces a tier step-down.
    [Range(0, 100)]
    public double RegimeVixShockSpikePct { get; init; } = 20.0;
}