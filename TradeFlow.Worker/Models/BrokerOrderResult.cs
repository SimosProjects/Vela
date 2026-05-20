namespace TradeFlow.Worker.Models;

/// <summary>
/// Result returned from the broker after placing or closing an order.
/// </summary>
public record BrokerOrderResult(
    string OrderId,
    string? StopOrderId,
    string? TargetOrderId,
    decimal FillPrice,
    int FillQuantity,
    decimal FillAmount,
    OrderStatus Status,
    DateTimeOffset FilledAt,
    string? RejectionReason = null
);