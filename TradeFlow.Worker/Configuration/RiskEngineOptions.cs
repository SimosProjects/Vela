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
    public Dictionary<string, int> RestrictedTraders { get; init; } = [];

    public bool AllowLotto { get; init; } = false;

    public bool AllowHigh { get; init; } = true;

    public bool SkipTradeOnSlippageTimeout { get; init; } = true;

    public List<string> BlockedSymbols { get; init; } = [];

    [Range(0, 10000, ErrorMessage = "MinStockPriceDollars must be between 0 and 10000.")]
    public decimal MinStockPriceDollars { get; init; } = 3.00m;

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

    [Range(0, 100, ErrorMessage = "PostFillMaxSlippagePct must be between 0 and 100.")]
    public decimal PostFillMaxSlippagePct { get; init; } = 10.0m;

    [Range(1, 100, ErrorMessage = "MaxDailyExposurePct must be between 1 and 100.")]
    public double MaxDailyExposurePct { get; init; } = 30.0;

    [Range(0, 100, ErrorMessage = "StockDailyAllocationPct must be between 0 and 100.")]
    public double StockDailyAllocationPct { get; init; } = 0.0;

    [Range(0, 10, ErrorMessage = "MarginPct must be between 0 and 10.")]
    public double MarginPct { get; init; } = 0.0;

    [Range(0, 23, ErrorMessage = "ZeroDteEntryCutoffHour must be between 0 and 23.")]
    public int ZeroDteEntryCutoffHour { get; init; } = 12;

    [Range(1, 10, ErrorMessage = "MaxPositionsPerSymbol must be between 1 and 10.")]
    public int MaxPositionsPerSymbol { get; init; } = 1;

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
}