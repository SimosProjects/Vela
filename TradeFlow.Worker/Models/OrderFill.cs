namespace TradeFlow.Worker.Models;

/// <summary>
/// Carries the fill status and average fill price from an IBKR order callback.
/// Replaces direct use of IBApi.OrderState so fill price is not lost after the callback resolves.
/// </summary>
public record OrderFill(string Status, decimal AvgFillPrice, int FilledQuantity);