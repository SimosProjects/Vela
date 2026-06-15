namespace Vela.Worker.Models;

public enum OrderStatus
{
    Filled,
    PartialFill,
    Pending,
    Cancelled,
    Rejected,
    Simulated
}