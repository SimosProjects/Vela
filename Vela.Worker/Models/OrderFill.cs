namespace Vela.Worker.Models;

/// <summary>
/// Carries the fill status and average fill price from an IBKR order callback.
/// Replaces direct use of IBApi.OrderState so fill price is not lost after the callback resolves.
/// LocalSymbol is IBKR's own resolved contract identifier (only populated by execDetails, which
/// receives the full Contract), used to correct alert-supplied options contract strings that
/// don't match IBKR's actual listing (e.g. Xtrades reporting "SPX" for a contract IBKR resolves
/// as "SPXW").
/// </summary>
public record OrderFill(string Status, decimal AvgFillPrice, int FilledQuantity, string? LocalSymbol = null);