using Vela.Worker.Data;

namespace Vela.Worker.Formatting;

public static class IbSnapshotFormatter
{
    /// <summary>
    /// Line separating consecutive positions in the snapshot message. Shared with
    /// DiscordNotificationService so it can split a too-long message on exact position
    /// boundaries without duplicating the literal.
    /// </summary>
    public const string PositionSeparator = "-------------------------";

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
        // IBKR's reqPositions() emits a final Quantity == 0 update when a position closes —
        // these are not open positions and must never be displayed or flagged as unprotected.
        positions = positions.Where(p => p.Quantity != 0).ToList();

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
                    lines.Add(PositionSeparator);
            }
        }

        lines.Add("=========================");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns every position with no matching live stop order. Uses the same matching
    /// logic as BuildSnapshotMessage's stop loss display, via the shared GetMatchingStopOrders
    /// helper, so this and the Discord snapshot can never disagree about protection status.
    /// A position with an ambiguous stop match (multiple live stops) is NOT unprotected —
    /// it has too much protection to safely reason about, not too little — it only shows up
    /// via GetDuplicateProtectedPositions.
    /// </summary>
    public static List<IbkrPosition> GetUnprotectedPositions(
        List<IbkrPosition> positions, List<IbkrOpenOrder> orders)
    {
        // IBKR's reqPositions() emits a final Quantity == 0 update when a position closes —
        // these are not open positions and must never be displayed or flagged as unprotected.
        positions = positions.Where(p => p.Quantity != 0).ToList();

        return positions.Where(p =>
        {
            var (order, ambiguous) = GetMatchingStopOrders(p, orders);
            return order is null && !ambiguous;
        }).ToList();
    }

    /// <summary>
    /// Returns every position with more than one live matching stop order — the duplicate
    /// protection case Guardian's final verification pass checks for.
    /// </summary>
    public static List<IbkrPosition> GetDuplicateProtectedPositions(
        List<IbkrPosition> positions, List<IbkrOpenOrder> orders)
    {
        // IBKR's reqPositions() emits a final Quantity == 0 update when a position closes —
        // these are not open positions and must never be displayed or flagged as unprotected.
        positions = positions.Where(p => p.Quantity != 0).ToList();

        return positions.Where(p => GetMatchingStopOrders(p, orders).Ambiguous).ToList();
    }

    /// <summary>
    /// Parses a raw OCC-format LocalSymbol (e.g. "SPXW260714C07595000") into its expiration,
    /// right ("C" or "P"), and strike. Returns null if the string doesn't match this shape.
    /// </summary>
    public static (DateOnly Expiration, string Right, decimal Strike)? ParseOccContract(string? localSymbol)
    {
        if (localSymbol is null) return null;

        var symbol = localSymbol.Replace(" ", "");

        // Skip alphabetic root symbol prefix to reach the 6-digit YYMMDD expiry date —
        // same walk used by StartupReconciliationService's IsExpiringToday.
        var i = 0;
        while (i < symbol.Length && char.IsLetter(symbol[i])) i++;
        if (i == 0 || i + 6 > symbol.Length) return null;

        var datePart = symbol[i..(i + 6)];
        if (!DateOnly.TryParseExact(datePart, "yyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var expiration))
            return null;

        i += 6;
        if (i >= symbol.Length) return null;

        var right = symbol[i] switch
        {
            'C' => "C",
            'P' => "P",
            _ => null
        };
        if (right is null) return null;
        i += 1;

        var strikePart = symbol[i..];
        if (strikePart.Length != 8 || !long.TryParse(strikePart, out var strikeThousandths))
            return null;

        return (expiration, right, strikeThousandths / 1000m);
    }

    /// <summary>
    /// Formats a position's parsed OCC contract as a display line, e.g. "Jul 14 '26 $7595 Call".
    /// Returns null if the LocalSymbol doesn't parse (see ParseOccContract) so callers can omit
    /// the line entirely rather than falling back to a garbage placeholder. Shared by
    /// BuildSnapshotMessage and Vela.Guardian so both display contract details identically.
    /// </summary>
    public static string? FormatOccContractLine(string? localSymbol)
    {
        var contract = ParseOccContract(localSymbol);
        if (contract is null) return null;

        var (expiration, right, strike) = contract.Value;
        var dateLabel = $"{expiration:MMM dd} '{expiration:yy}";
        var strikeLabel = strike == Math.Truncate(strike) ? strike.ToString("F0") : strike.ToString("F2");
        var rightLabel = right == "C" ? "Call" : "Put";

        return $"{dateLabel} ${strikeLabel} {rightLabel}";
    }

    /// <summary>
    /// Returns the existing live take-profit LMT order for a position, if any — same
    /// matching key (LocalSymbol for options, Symbol for stocks) as BuildSnapshotMessage's
    /// target display and GetMatchingStopOrders' stop matching, restricted to genuinely
    /// live/working statuses so a cancelled/filled remnant still present in the
    /// reqAllOpenOrders snapshot is never mistaken for a real target. More than one live
    /// match is ambiguous — the caller must not guess which one is real via FirstOrDefault,
    /// it must treat this as "cannot safely proceed, manual cleanup required."
    /// </summary>
    public static (IbkrOpenOrder? Order, bool Ambiguous) GetMatchingTargetOrder(
        IbkrPosition position, List<IbkrOpenOrder> orders) =>
        GetMatchingTargetOrder(PositionKey(position), orders);

    /// <summary>
    /// Same matching as the IbkrPosition overload above, keyed directly by a pre-computed
    /// position key (see PositionKeyForOpenPosition) — for callers validating a stored
    /// open_positions DB row, which has no IbkrPosition to match against.
    /// </summary>
    public static (IbkrOpenOrder? Order, bool Ambiguous) GetMatchingTargetOrder(
        string positionKey, List<IbkrOpenOrder> orders)
    {
        var matches = orders.Where(o =>
            OrderKey(o) == positionKey &&
            LiveOrderStatuses.Contains(o.Status) &&
            o.OrderType == "LMT")
            .ToList();

        return matches.Count switch
        {
            0 => (null, false),
            1 => (matches[0], false),
            _ => (null, true)
        };
    }

    /// <summary>
    /// Computes the same order-matching key as PositionKey, but from an open_positions DB row
    /// instead of a live IbkrPosition — lets IbkrBrokerService.ReRegisterStopCallbacksAsync
    /// validate a stored stop/target order ID against a fresh open orders snapshot using the
    /// exact same symbol/LocalSymbol matching BuildSnapshotMessage and Guardian already rely on.
    /// </summary>
    public static string PositionKeyForOpenPosition(OpenPosition position) =>
        string.Equals(position.TradeType, "Options", StringComparison.OrdinalIgnoreCase)
            ? (position.OptionsContract ?? "").Replace(" ", "")
            : position.Symbol;

    // -- Helpers --

    // Appends the Symbol / Quantity / Avg Cost / Orders block for a single position.
    private static void AppendPositionLines(
        List<string> lines, IbkrPosition position, List<IbkrOpenOrder> orders)
    {
        var isOptions = position.SecType == "OPT";

        // Options avgCost is IBKR's per-contract cost (already x100), divide down to the
        // per-share premium — same convention as StartupReconciliationService.BuildManualPosition.
        var entryPrice = isOptions ? position.AvgCost / 100m : position.AvgCost;

        var (stopOrder, stopAmbiguous) = GetMatchingStopOrders(position, orders);
        var (targetOrder, targetAmbiguous) = GetMatchingTargetOrder(position, orders);

        lines.Add(position.Symbol);

        if (isOptions)
        {
            var contractLine = FormatOccContractLine(position.LocalSymbol);
            if (contractLine is not null)
                lines.Add(contractLine);
        }

        lines.Add($"{position.Quantity} {(isOptions ? "Contracts" : "Shares")}");
        lines.Add($"Avg Cost: ${entryPrice:F2}");
        lines.Add("Orders");

        if (stopAmbiguous)
        {
            lines.Add("⚠️ AMBIGUOUS — multiple live stop orders");
        }
        else if (stopOrder is not null)
        {
            lines.Add("✓ Stop Loss");
            lines.Add($"{stopOrder.Action} {stopOrder.Quantity:0} @ {stopOrder.AuxPrice ?? 0:F2}");
            lines.Add(stopOrder.Status);
        }
        else
        {
            lines.Add("⚠️ NO STOP LOSS FOUND");
        }

        if (targetAmbiguous)
        {
            lines.Add("⚠️ AMBIGUOUS — multiple live target orders");
        }
        else if (targetOrder is not null)
        {
            lines.Add("✓ Take Profit");
            lines.Add($"{targetOrder.Action} {targetOrder.Quantity:0} @ {targetOrder.LmtPrice ?? 0:F2}");
            lines.Add(targetOrder.Status);
        }
    }

    // Order statuses IBKR considers genuinely live/working — a matched order must be
    // something actually resting at the broker right now. reqAllOpenOrders can return
    // orders from prior sessions (see GetAllOpenOrdersAsync's own doc comment), including
    // historical remnants (Cancelled, Filled, Inactive, ...) that must never be mistaken
    // for real protection just because they still momentarily appear in the snapshot.
    // Public (not private) so IbkrBrokerService.ReRegisterStopCallbacksAsync and Vela.Guardian's
    // post-placement confirmation can validate order IDs against the same definition of "live".
    public static readonly HashSet<string> LiveOrderStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Submitted", "PreSubmitted", "ApiPending", "PendingSubmit"
    };

    // The single source of truth for "is this position protected" — matches by LocalSymbol
    // (options, spaces stripped) or Symbol (stocks) against OrderType containing TRAIL or STP,
    // restricted to live statuses (see LiveOrderStatuses). BuildSnapshotMessage's stop loss
    // display and Guardian's unprotected/duplicate detection both go through this so they
    // can never disagree with each other. More than one live match is ambiguous — the
    // caller must not guess which one is real via FirstOrDefault.
    // Public (not private) so IbkrBrokerService.ReRegisterStopCallbacksAsync can reuse the exact
    // same matching for the string-keyed overload below. An order's OrderId == 0 is not filtered
    // out here — IBKR reports 0 for orders placed outside any API client (confirmed for BROS/GE/V,
    // 2026-07-15), not for orders that don't exist. It's still a real, live, matchable order.
    public static (IbkrOpenOrder? Order, bool Ambiguous) GetMatchingStopOrders(
        IbkrPosition position, List<IbkrOpenOrder> orders) =>
        GetMatchingStopOrders(PositionKey(position), orders);

    /// <summary>
    /// Same matching as the IbkrPosition overload above, keyed directly by a pre-computed
    /// position key (see PositionKeyForOpenPosition) — for callers validating a stored
    /// open_positions DB row, which has no IbkrPosition to match against.
    /// </summary>
    public static (IbkrOpenOrder? Order, bool Ambiguous) GetMatchingStopOrders(
        string positionKey, List<IbkrOpenOrder> orders)
    {
        var matches = orders.Where(o =>
            OrderKey(o) == positionKey &&
            LiveOrderStatuses.Contains(o.Status) &&
            (o.OrderType.Contains("TRAIL", StringComparison.OrdinalIgnoreCase) ||
             o.OrderType.Contains("STP", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count switch
        {
            0 => (null, false),
            1 => (matches[0], false),
            _ => (null, true)
        };
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
