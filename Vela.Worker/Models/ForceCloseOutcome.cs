namespace Vela.Worker.Models;

/// <summary>
/// Result of a force-close attempt. Written back to the force_close_requests row by
/// ForceCloseConsumerService so the dashboard can distinguish a clean close from a case
/// that needs manual reconciliation.
/// </summary>
public enum ForceCloseOutcome
{
    Closed,
    Pending,
    AlreadyClosing,
    Failed,
    NotFound
}