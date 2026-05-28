using System.ComponentModel.DataAnnotations;

namespace TradeFlow.Worker.Configuration;

public class RiskEngineOptions
{
    public const string SectionName = "RiskEngine";

    [Range(0, 100, ErrorMessage = "MinXScore must be between 0 and 100.")]
    public int MinXScore { get; init; } = 60;

    [MinLength(1, ErrorMessage = "At least one approved trader must be specified.")]
    public List<string> ApprovedTraders { get; init; } = [];

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

    [Range(1, 100, ErrorMessage = "MaxDailyTrades must be between 1 and 100.")]
    public int MaxDailyTrades { get; init; } = 25;

    // Hour (ET) after which same-day expiry option entries are blocked.
    // Defaults to 12 (noon ET), 0DTE entries after this time risk total loss
    // due to liquidity drying up near close.
    [Range(0, 23, ErrorMessage = "ZeroDteEntryCutoffHour must be between 0 and 23.")]
    public int ZeroDteEntryCutoffHour { get; init; } = 12;

    // Time (ET, HH:mm) at which any open same-day expiry options are force-closed.
    // Defaults to 15:30, gives 30 minutes buffer before close to exit while
    // liquidity still exists, preventing total loss from expiry.
    public string SameDayExpiryAutoCloseCutoff { get; init; } = "15:30";
}