namespace Vela.Worker.Models;

/// <summary>
/// Result returned from the broker after placing or closing an order.
/// LocalSymbol is IBKR's own resolved options contract identifier, captured from execDetails
/// when available. Null for stock orders and for fills where execDetails never resolved.
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
    string? RejectionReason = null,
    string? LocalSymbol = null
);