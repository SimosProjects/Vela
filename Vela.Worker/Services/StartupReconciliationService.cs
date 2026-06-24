using Vela.Worker.Engine;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Runs once on startup as a blocking step before any alerts are processed.
/// Reconciles IBKR actual state against the open_positions database to prevent
/// ghost positions, undetected shorts, and orphan orders from causing incorrect
/// trading behaviour.
///
/// Four steps run in sequence:
/// 1. Cover shorts, any negative-quantity IBKR position is an unintended short.
///    Place an immediate market BUY and send a critical Discord alert.
/// 2. Verify DB positions against IBKR, for each row in open_positions, confirm
///    IBKR actually holds it. Rows with no matching IBKR position are removed from
///    the DB and TradeGuard. Quantity mismatches are corrected to match IBKR.
/// 3. Detect untracked IBKR positions, IBKR longs not in the DB are manual trades.
///    Create a tracking record (is_manual=true) so they appear on the dashboard.
///    0DTE manual positions trigger a critical Discord warning.
/// 4. Classify open orders, orders not in Vela's stop/target map are unknown.
///    Log and Discord-alert so the operator can review and cancel if needed.
/// </summary>
public class StartupReconciliationService
{
    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly IBrokerService _broker;
    private readonly IOpenPositionRepository _repo;
    private readonly TradeGuard _guard;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<StartupReconciliationService> _logger;

    public StartupReconciliationService(
        IBrokerService broker,
        IOpenPositionRepository repo,
        TradeGuard guard,
        DiscordNotificationService discord,
        ILogger<StartupReconciliationService> logger)
    {
        _broker  = broker;
        _repo    = repo;
        _guard   = guard;
        _discord = discord;
        _logger  = logger;
    }

    /// <summary>
    /// Runs all reconciliation steps. Called from Program.cs before host.Run().
    /// Exceptions are caught and logged — a reconciliation failure must never prevent startup,
    /// but is always surfaced as a critical Discord alert so the operator is aware.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Startup reconciliation — beginning IBKR state verification.");

        try
        {
            var snapshot = await _broker.GetAllPositionsAsync(ct);

            if (snapshot.TimedOut)
            {
                _logger.LogError(
                    "Startup reconciliation — GetAllPositions timed out. " +
                    "Cannot verify account state. Aborting reconciliation.");

                await _discord.NotifyCriticalAsync(
                    "⚠️ Startup Reconciliation Aborted — Gateway Timeout",
                    "Could not retrieve IBKR positions within the timeout window. " +
                    "Account state could not be verified. Manual review required before trading.",
                    ct);
                return;
            }

            var dbPositions = await _repo.GetAllAsync(ct);

            _logger.LogInformation(
                "Startup reconciliation — IBKR: {IbkrCount} positions, DB: {DbCount} positions.",
                snapshot.Positions.Count, dbPositions.Count);

            await CoverShortsAsync(snapshot.Positions, ct);
            await VerifyDbPositionsAsync(snapshot.Positions, dbPositions, ct);
            await DetectManualPositionsAsync(snapshot.Positions, dbPositions, ct);
            await ClassifyOpenOrdersAsync(ct);

            _logger.LogInformation("Startup reconciliation — complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup reconciliation — unhandled error.");
            await _discord.NotifyCriticalAsync(
                "⚠️ Startup Reconciliation Failed",
                $"Reconciliation encountered an error on startup: {ex.Message}\n" +
                "Manual IBKR account verification required before trading.",
                ct);
        }
    }

    // -- Step 1: Cover shorts --

    private async Task CoverShortsAsync(
        List<IbkrPosition> ibkrPositions,
        CancellationToken ct)
    {
        var shorts = ibkrPositions.Where(p => p.Quantity < 0).ToList();

        if (shorts.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 1 — no short positions detected.");
            return;
        }

        _logger.LogError(
            "Startup reconciliation step 1 — found {Count} short position(s). Covering immediately.",
            shorts.Count);

        var details = string.Join("\n", shorts.Select(p =>
            $"  {p.Symbol} ({p.SecType}) qty {p.Quantity} avgCost ${p.AvgCost:F2}"));

        await _discord.NotifyCriticalAsync(
            $"🚨 SHORT POSITIONS DETECTED — Covering {shorts.Count} position(s)",
            $"The following short positions were found on startup and are being covered:\n{details}\n" +
            "These are unintended shorts, likely caused by sell orders executing against empty positions.",
            ct);

        foreach (var shortPos in shorts)
        {
            try
            {
                var coverQty  = Math.Abs(shortPos.Quantity);
                var isOptions = shortPos.SecType == "OPT";

                _logger.LogWarning(
                    "Startup reconciliation — covering short {Symbol} ({SecType}) with BUY {Qty}",
                    shortPos.Symbol, shortPos.SecType, coverQty);

                var coverTrade = new TradeRecord
                {
                    AlertId         = "RECONCILIATION",
                    OrderId         = "COVER",
                    StopOrderId     = null,
                    TargetOrderId   = null,
                    UserName        = "SYSTEM",
                    XScore          = 0,
                    DiscordRank     = null,
                    Symbol          = shortPos.Symbol,
                    TradeType       = isOptions ? TradeType.Options : TradeType.Stock,
                    OptionsContract = isOptions ? shortPos.LocalSymbol : null,
                    Direction       = null,
                    Strike          = null,
                    Expiration      = null,
                    Quantity        = coverQty,
                    EntryPrice      = shortPos.AvgCost,
                    EntryAmount     = shortPos.AvgCost * coverQty,
                    StopPrice       = 0m,
                    TargetPrice     = 0m,
                    OpenedAt        = DateTimeOffset.UtcNow,
                };

                await _broker.ClosePositionAsync(coverTrade, TradeOutcome.ForcedClose, ct);

                _logger.LogWarning(
                    "Startup reconciliation — cover order placed for {Symbol} qty {Qty}.",
                    shortPos.Symbol, coverQty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Startup reconciliation — failed to cover short {Symbol}. Manual intervention required.",
                    shortPos.Symbol);

                await _discord.NotifyCriticalAsync(
                    $"🚨 FAILED TO COVER SHORT — {shortPos.Symbol}",
                    $"Could not automatically cover short position in {shortPos.Symbol} " +
                    $"(qty {shortPos.Quantity}). MANUAL INTERVENTION REQUIRED.\nError: {ex.Message}",
                    ct);
            }
        }
    }

    // -- Step 2: Verify DB positions against IBKR --

    private async Task VerifyDbPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        List<OpenPosition> dbPositions,
        CancellationToken ct)
    {
        var managedPositions = dbPositions.Where(p => !p.IsManual).ToList();

        if (managedPositions.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 2 — no managed positions in DB to verify.");
            return;
        }

        _logger.LogInformation(
            "Startup reconciliation step 2 — verifying {Count} managed DB position(s) against IBKR.",
            managedPositions.Count);

        foreach (var dbPos in managedPositions)
        {
            var ibkrMatch = FindIbkrPositionForDb(ibkrPositions, dbPos);

            if (ibkrMatch is null)
            {
                _logger.LogWarning(
                    "Startup reconciliation — {Symbol} (OrderId {OrderId}) not found in IBKR. " +
                    "Removing from DB and TradeGuard — position closed while offline.",
                    dbPos.Symbol, dbPos.OrderId);

                await _repo.DeleteAsync(dbPos.OrderId, ct);
                _guard.RemovePosition(dbPos.OrderId);

                await _discord.NotifyCriticalAsync(
                    $"⚠️ Ghost Position Removed — {dbPos.Symbol}",
                    $"Position {dbPos.Symbol} (OrderId {dbPos.OrderId}) was in the database " +
                    "but not found in IBKR. It has been removed from Vela. " +
                    "This may indicate the position was closed while Vela was offline.",
                    ct);
                continue;
            }

            if (ibkrMatch.Quantity <= 0)
            {
                _logger.LogWarning(
                    "Startup reconciliation — {Symbol} (OrderId {OrderId}) has qty {Qty} in IBKR. " +
                    "Short or zero position — removing from DB and TradeGuard.",
                    dbPos.Symbol, dbPos.OrderId, ibkrMatch.Quantity);

                await _repo.DeleteAsync(dbPos.OrderId, ct);
                _guard.RemovePosition(dbPos.OrderId);
                continue;
            }

            if (ibkrMatch.Quantity != dbPos.Quantity)
            {
                _logger.LogWarning(
                    "Startup reconciliation — {Symbol} qty mismatch: DB={DbQty} IBKR={IbkrQty}. " +
                    "Correcting DB to match IBKR.",
                    dbPos.Symbol, dbPos.Quantity, ibkrMatch.Quantity);

                await _repo.UpdateQuantityAsync(dbPos.OrderId, ibkrMatch.Quantity, ct);
                _guard.UpdatePositionQuantity(dbPos.OrderId, ibkrMatch.Quantity);
            }
            else
            {
                _logger.LogDebug(
                    "Startup reconciliation — {Symbol} verified OK (qty {Qty}).",
                    dbPos.Symbol, ibkrMatch.Quantity);
            }
        }
    }

    // -- Step 3: Detect untracked IBKR longs (manual trades) --

    private async Task DetectManualPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        List<OpenPosition> dbPositions,
        CancellationToken ct)
    {
        var longPositions = ibkrPositions.Where(p => p.Quantity > 0).ToList();

        if (longPositions.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 3 — no IBKR long positions to check for manual entries.");
            return;
        }

        _logger.LogInformation(
            "Startup reconciliation step 3 — checking {Count} IBKR long position(s) for untracked entries.",
            longPositions.Count);

        foreach (var ibkrPos in longPositions)
        {
            if (HasDbMatch(ibkrPos, dbPositions)) continue;

            _logger.LogInformation(
                "Periodic reconciliation — new untracked position: {Symbol} {SecType} qty {Qty}. Creating manual tracking record.",
                ibkrPos.Symbol, ibkrPos.SecType, ibkrPos.Quantity);

            var manualPos = BuildManualPosition(ibkrPos);
            await _repo.SaveAsync(manualPos, ct);

            var is0Dte = IsExpiringToday(ibkrPos);

            var title = is0Dte
                ? $"⚠️ 0DTE Manual Position — {ibkrPos.Symbol}"
                : $"📋 Manual Position Detected — {ibkrPos.Symbol}";

            var message = $"Position in **{ibkrPos.Symbol}** " +
                $"(qty {ibkrPos.Quantity} @ ${ibkrPos.AvgCost:F2}) " +
                "is not managed by Vela. Tracking as MANUAL — " +
                "all stop and exit management must be done directly in IBKR." +
                (is0Dte ? " ⚠️ This position expires today." : "");

            await _discord.NotifyCriticalAsync(title, message, ct);
        }
    }

    // -- Step 4: Classify open orders --

    private async Task ClassifyOpenOrdersAsync(CancellationToken ct)
    {
        _logger.LogInformation("Startup reconciliation step 4 — classifying open orders.");

        var snapshot = await _broker.GetAllOpenOrdersAsync(ct);

        if (snapshot.TimedOut)
        {
            _logger.LogWarning(
                "Startup reconciliation step 4 — GetAllOpenOrders timed out. Order classification skipped.");
            return;
        }

        if (snapshot.Orders.Count == 0)
        {
            _logger.LogInformation("Startup reconciliation step 4 — no open orders found.");
            return;
        }

        var unknownOrders = snapshot.Orders
            .Where(o => !_broker.IsKnownOrder(o.OrderId))
            .ToList();

        var managedCount = snapshot.Orders.Count - unknownOrders.Count;

        _logger.LogInformation(
            "Startup reconciliation step 4 — {Total} open order(s): {Managed} managed, {Unknown} unknown.",
            snapshot.Orders.Count, managedCount, unknownOrders.Count);

        if (unknownOrders.Count == 0) return;

        foreach (var order in unknownOrders)
        {
            _logger.LogWarning(
                "Unknown order — OrderId: {OrderId} Symbol: {Symbol} Action: {Action} " +
                "Type: {Type} Qty: {Qty} Status: {Status}",
                order.OrderId, order.Symbol, order.Action,
                order.OrderType, order.Quantity, order.Status);
        }

        var details = string.Join("\n", unknownOrders.Select(o =>
            $"  OrderId {o.OrderId}: {o.Symbol} {o.Action} {o.OrderType} " +
            $"qty {o.Quantity} ({o.Status})"));

        await _discord.NotifyCriticalAsync(
            $"⚠️ Unknown Orders Detected — {unknownOrders.Count} order(s)",
            $"The following orders were not placed by Vela this session:\n{details}\n" +
            "These may be orphan orders from a previous session or manually placed orders. " +
            "Review in IBKR and cancel if not intentional.",
            ct);
    }

    // -- Helpers --

    private static OpenPosition BuildManualPosition(IbkrPosition ibkrPos)
    {
        var isOptions = ibkrPos.SecType == "OPT";

        // IBKR's position() callback returns avgCost for options as the per-contract cost,
        // which is already multiplied by 100 internally (e.g. a $10.15 premium returns avgCost 1015).
        // Divide by 100 to recover the per-share premium that matches how trade_metrics stores prices.
        // For stocks avgCost is already per-share so no adjustment is needed.
        var entryPrice  = isOptions ? ibkrPos.AvgCost / 100m : ibkrPos.AvgCost;
        var multiplier  = isOptions ? 100m : 1m;
        var entryAmount = entryPrice * ibkrPos.Quantity * multiplier;

        return new OpenPosition
        {
            OrderId         = $"MANUAL-{ibkrPos.Symbol.Replace(" ", "")}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            StopOrderId     = null,
            TargetOrderId   = null,
            AlertId         = "MANUAL",
            UserName        = "MANUAL",
            Symbol          = ibkrPos.Symbol,
            TradeType       = isOptions ? "Options" : "Stock",
            OptionsContract = isOptions ? ibkrPos.LocalSymbol?.Replace(" ", "") : null,
            Direction       = null,
            Strike          = null,
            Expiration      = null,
            Quantity        = ibkrPos.Quantity,
            EntryPrice      = entryPrice,
            EntryAmount     = entryAmount,
            StopPrice       = 0m,
            TargetPrice     = 0m,
            OpenedAt        = DateTimeOffset.UtcNow,
            IsAverage       = false,
            HasAveraged     = false,
            IsManual        = true,
        };
    }

    private static bool HasDbMatch(IbkrPosition ibkrPos, IEnumerable<OpenPosition> dbPositions)
    {
        if (ibkrPos.SecType == "OPT")
        {
            var localSymbol = ibkrPos.LocalSymbol?.Replace(" ", "") ?? "";
            return dbPositions.Any(db =>
                db.TradeType == "Options" &&
                db.OptionsContract is not null &&
                string.Equals(
                    db.OptionsContract.Replace(" ", ""),
                    localSymbol,
                    StringComparison.OrdinalIgnoreCase));
        }

        return dbPositions.Any(db =>
            db.TradeType != "Options" &&
            string.Equals(db.Symbol, ibkrPos.Symbol, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExpiringToday(IbkrPosition ibkrPos)
    {
        if (ibkrPos.SecType != "OPT" || ibkrPos.LocalSymbol is null) return false;

        var localSymbol = ibkrPos.LocalSymbol.Replace(" ", "");

        var i = 0;
        while (i < localSymbol.Length && char.IsLetter(localSymbol[i])) i++;
        if (i + 6 > localSymbol.Length) return false;

        var datePart = localSymbol[i..(i + 6)];
        if (!DateOnly.TryParseExact(datePart, "yyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var expiryDate))
            return false;

        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        return expiryDate == todayEt;
    }

    private static IbkrPosition? FindIbkrPositionForDb(
        List<IbkrPosition> ibkrPositions,
        OpenPosition dbPos)
    {
        if (dbPos.TradeType == "Options" && dbPos.OptionsContract is not null)
        {
            var dbContract = dbPos.OptionsContract.Replace(" ", "");
            return ibkrPositions.FirstOrDefault(p =>
                p.SecType == "OPT" &&
                string.Equals(
                    p.LocalSymbol?.Replace(" ", ""),
                    dbContract,
                    StringComparison.OrdinalIgnoreCase));
        }

        return ibkrPositions.FirstOrDefault(p =>
            p.SecType == "STK" &&
            string.Equals(p.Symbol, dbPos.Symbol, StringComparison.OrdinalIgnoreCase));
    }
}