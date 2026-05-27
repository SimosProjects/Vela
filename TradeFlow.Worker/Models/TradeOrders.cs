namespace TradeFlow.Worker.Models;

/// <summary>
/// Built by PositionSizer from an approved Alert.
/// </summary>
public record TradeOrder(
    string    AlertId,
    string    UserName,
    string    Symbol,
    TradeType TradeType,

    // OCC symbol e.g. TSLA260620C00450000. Primary key for exit matching.
    string? OptionsContractSymbol,

    string? Direction,
    decimal? Strike,
    string? Expiration,

    // Contracts (options) or shares (stocks). Rounded down from budget / price.
    int Quantity,

    // From alert pricePaid. Actual fill may differ.
    decimal EstimatedEntryPrice,

    // Options: $1,000 initial / $500 average. Stocks: $3,000 / $1,500.
    decimal BudgetUsed,

    // StopPrice is the initial trail reference price, not a fixed level.
    decimal StopPrice,

    // Options: entry x 3.00 (+200%). Stocks: entry x 1.30 (+30%).
    decimal TargetPrice,

    // Trailing stop percentage sent to IBKR. Risk-tiered by PositionSizer.
    double TrailPercent,

    bool IsAverage = false
);