namespace Vela.Worker.Models;

/// <summary>
/// A single daily OHLCV bar returned by IbkrBrokerService.GetHistoricalBarsAsync.
/// Used by MarketConditionsLogger to compute moving averages and ADX from Gateway data.
/// </summary>
public record HistoricalBar(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume
);