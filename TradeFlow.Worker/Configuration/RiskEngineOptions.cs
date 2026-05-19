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

    // Minimum stock price in dollars. Stock entry alerts below this threshold are rejected.
    // Defaults to $3.00 to exclude penny stocks and OTC equities with high gap-down risk.
    // Set to 0 to disable the filter entirely.
    [Range(0, 10000, ErrorMessage = "MinStockPriceDollars must be between 0 and 10000.")]
    public decimal MinStockPriceDollars { get; init; } = 3.00m;
}