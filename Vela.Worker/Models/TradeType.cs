namespace Vela.Worker.Models;

/// <summary>
/// Discriminates between options and stock trades throughout the
/// broker execution pipeline. Determines position sizing rules,
/// stop/target percentages, and which CSV file the trade is logged to.
/// </summary>
public enum TradeType
{
    /// <summary>
    /// Options contract trade. Sizing: $1,000 initial, $500 average.
    /// Stop: -50%. Target: +200%. Logged to options_trades.csv.
    /// </summary>
    Options,

    /// <summary>
    /// Common stock trade. Sizing: $3,000 initial, $1,500 average.
    /// Stop: -15%. Target: +30%. Logged to stocks_trades.csv.
    /// </summary>
    Stock
}