namespace Vela.Worker.Formatting;

public static class IbSnapshotFormatter
{
    /// <summary>
    /// Builds a plain text account and open positions snapshot for the Discord snapshot alert.
    /// Stop loss orders are matched by LocalSymbol (options, spaces stripped) or Symbol (stocks)
    /// against OrderType containing TRAIL or STP, missing stop always prints a warning line since
    /// an unprotected position is the failure mode this message exists to surface.
    /// Take profit orders are matched the same way against OrderType "LMT" — most positions are
    /// trail-only by design, so a missing take profit block is simply omitted, not warned on.
    /// </summary>
    public static string BuildSnapshotMessage(
        AccountSnapshot account,
        List<IbkrPosition> positions,
        List<IbkrOpenOrder> orders)
    {
        var lines = new List<string>
        {
            "======== ACCOUNT ========",
            $"Net Liquidation: {FormatCurrency(account.NetLiquidation)}",
            $"Cash: {FormatCurrency(account.TotalCash)}",
            $"Buying Power: {FormatCurrency(account.BuyingPower)}",
            $"Today's P&L: {FormatSignedCurrency(account.TodayPnL)}",
            "=========================",
            "OPEN POSITIONS",
            "========================="
        };

        if (positions.Count == 0)
        {
            lines.Add("No open positions.");
        }
        else
        {
            for (var i = 0; i < positions.Count; i++)
            {
                AppendPositionLines(lines, positions[i], orders);
                if (i < positions.Count - 1)
                    lines.Add("-------------------------");
            }
        }

        lines.Add("=========================");

        return string.Join("\n", lines);
    }

    // -- Helpers --

    // Appends the Symbol / Quantity / Avg Cost / Orders block for a single position.
    private static void AppendPositionLines(
        List<string> lines, IbkrPosition position, List<IbkrOpenOrder> orders)
    {
        var isOptions = position.SecType == "OPT";

        // Options avgCost is IBKR's per-contract cost (already x100), divide down to the
        // per-share premium — same convention as StartupReconciliationService.BuildManualPosition.
        var entryPrice = isOptions ? position.AvgCost / 100m : position.AvgCost;
        var key = PositionKey(position);
        var matchingOrders = orders.Where(o => OrderKey(o) == key).ToList();

        var stopOrder = matchingOrders.FirstOrDefault(o =>
            o.OrderType.Contains("TRAIL", StringComparison.OrdinalIgnoreCase) ||
            o.OrderType.Contains("STP", StringComparison.OrdinalIgnoreCase));
        var targetOrder = matchingOrders.FirstOrDefault(o => o.OrderType == "LMT");

        lines.Add(position.Symbol);
        lines.Add($"{position.Quantity} {(isOptions ? "Contracts" : "Shares")}");
        lines.Add($"Avg Cost: ${entryPrice:F2}");
        lines.Add("Orders");

        if (stopOrder is not null)
        {
            lines.Add("✓ Stop Loss");
            lines.Add($"{stopOrder.Action} {stopOrder.Quantity:0} @ {stopOrder.AuxPrice ?? 0:F2}");
            lines.Add(stopOrder.Status);
        }
        else
        {
            lines.Add("⚠️ NO STOP LOSS FOUND");
        }

        if (targetOrder is not null)
        {
            lines.Add("✓ Take Profit");
            lines.Add($"{targetOrder.Action} {targetOrder.Quantity:0} @ {targetOrder.LmtPrice ?? 0:F2}");
            lines.Add(targetOrder.Status);
        }
    }

    private static string PositionKey(IbkrPosition position) =>
        position.SecType == "OPT" ? (position.LocalSymbol ?? "").Replace(" ", "") : position.Symbol;

    private static string OrderKey(IbkrOpenOrder order) =>
        order.SecType == "OPT" ? (order.LocalSymbol ?? "").Replace(" ", "") : order.Symbol;

    private static string FormatCurrency(decimal value) => $"${value:N0}";

    private static string FormatSignedCurrency(decimal value)
    {
        var sign = value < 0 ? "-" : "+";
        return $"{sign}${Math.Abs(value):N0}";
    }
}
