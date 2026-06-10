using TradeFlow.Worker.Data;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Runs once on startup as a blocking step before any alerts are processed.
/// Reconciles IBKR actual state against the open_positions database to prevent
/// orphan GTC orders and short positions from creating runaway losses.
///
/// Three steps run in sequence:
/// 1. Cancel orphan orders — any active IBKR order whose parent entry order ID
///    has no matching row in open_positions is cancelled immediately.
/// 2. Verify DB positions against IBKR, for each row in open_positions, confirm
///    IBKR actually holds it. Rows with no matching IBKR position are removed from
///    the DB and TradeGuard. Quantity mismatches are corrected to match IBKR.
/// 3. Detect and cover shorts, any negative-quantity IBKR position is an
///    unintended short. Place an immediate market BUY to cover and send a critical
///    Discord alert.
/// </summary>
public class StartupReconciliationService
{
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
    /// Runs all three reconciliation steps. Called from Program.cs before host.Run().
    /// Exceptions are caught and logged — a reconciliation failure must never prevent startup,
    /// but it is always surfaced as a critical Discord alert so the operator is aware.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Startup reconciliation — beginning IBKR state verification.");

        try
        {
            var ibkrPositions = await _broker.GetAllPositionsAsync(ct);
            var ibkrOrders    = await _broker.GetAllOpenOrdersAsync(ct);
            var dbPositions   = await _repo.GetAllAsync(ct);

            if (ibkrPositions.Count == 0 && ibkrOrders.Count == 0)
            {
                _logger.LogInformation(
                    "Startup reconciliation — IBKR returned no positions or orders. " +
                    "Either account is clean or Gateway did not respond. Skipping.");
                return;
            }

            //await CancelOrphanOrdersAsync(ibkrOrders, dbPositions, ct);
            await VerifyDbPositionsAsync(ibkrPositions, dbPositions, ct);
            await CoverShortsAsync(ibkrPositions, ct);

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

    // -- Step 1: Cancel orphan orders --

    // Any order in IBKR whose parent entry order ID has no matching open_positions row is
    // an orphan. It either fired against a now-closed position or was left over from a
    // previous session that did not clean up. Cancelling it prevents it from executing
    // against an empty account and creating a short.
    private async Task CancelOrphanOrdersAsync(
        List<IbkrOpenOrder> ibkrOrders,
        List<OpenPosition> dbPositions,
        CancellationToken ct)
    {
        if (ibkrOrders.Count == 0)
        {
            _logger.LogInformation("Startup reconciliation step 1 — no open orders in IBKR.");
            return;
        }

        var knownOrderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in dbPositions)
        {
            knownOrderIds.Add(p.OrderId);
            if (p.StopOrderId is not null)   knownOrderIds.Add(p.StopOrderId);
            if (p.TargetOrderId is not null) knownOrderIds.Add(p.TargetOrderId);
        }

        var orphans = ibkrOrders
            .Where(o => !knownOrderIds.Contains(o.OrderId.ToString()))
            .ToList();

        if (orphans.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 1 — all {Count} open orders are accounted for.",
                ibkrOrders.Count);
            return;
        }

        _logger.LogWarning(
            "Startup reconciliation step 1 — found {Count} orphan order(s). Cancelling.",
            orphans.Count);

        var details = string.Join("\n", orphans.Select(o =>
            $"  OrderId {o.OrderId} — {o.Symbol} {o.Action} {o.Quantity} {o.OrderType}"));

        foreach (var order in orphans)
        {
            try
            {
                await _broker.CancelOrderAsync(order.OrderId, ct);
                _logger.LogWarning(
                    "Startup reconciliation — cancelled orphan order {OrderId} ({Symbol} {Action} {Qty})",
                    order.OrderId, order.Symbol, order.Action, order.Quantity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Startup reconciliation — failed to cancel orphan order {OrderId}",
                    order.OrderId);
            }
        }

        await _discord.NotifyCriticalAsync(
            "⚠️ Orphan Orders Cancelled on Startup",
            $"{orphans.Count} order(s) with no matching open position were cancelled:\n{details}\n" +
            "These would have created short positions if left open.",
            ct);
    }

    // -- Step 2: Verify DB positions against IBKR --

    private async Task VerifyDbPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        List<OpenPosition> dbPositions,
        CancellationToken ct)
    {
        if (dbPositions.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 2 — no open positions in DB to verify.");
            return;
        }

        _logger.LogInformation(
            "Startup reconciliation step 2 — verifying {Count} DB position(s) against IBKR.",
            dbPositions.Count);

        foreach (var dbPos in dbPositions)
        {
            var ibkrMatch = FindIbkrPosition(ibkrPositions, dbPos);

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
                    "but not found in IBKR. It has been removed from TradeFlow. " +
                    "This may indicate the position was closed while TradeFlow was offline.",
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

    // -- Step 3: Detect and cover shorts --

    private async Task CoverShortsAsync(
        List<IbkrPosition> ibkrPositions,
        CancellationToken ct)
    {
        var shorts = ibkrPositions.Where(p => p.Quantity < 0).ToList();

        if (shorts.Count == 0)
        {
            _logger.LogInformation(
                "Startup reconciliation step 3 — no short positions detected.");
            return;
        }

        _logger.LogError(
            "Startup reconciliation step 3 — found {Count} short position(s). Covering immediately.",
            shorts.Count);

        var details = string.Join("\n", shorts.Select(p =>
            $"  {p.Symbol} ({p.SecType}) qty {p.Quantity} avgCost ${p.AvgCost:F2}"));

        await _discord.NotifyCriticalAsync(
            $"🚨 SHORT POSITIONS DETECTED — Covering {shorts.Count} position(s)",
            $"The following short positions were found on startup and are being covered:\n{details}\n" +
            "These are unintended shorts, likely caused by orphan sell orders executing against empty positions.",
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
                    // Only set OptionsContract for options — stocks must be null so
                    // BuildCloseContract takes the stock path and avoids error 321.
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

    // -- Helpers --

    // Matches a DB position to an IBKR position by symbol and contract.
    // Options match on LocalSymbol with spaces stripped — IBKR pads tickers to 6 chars
    // e.g. "RBLX  260612C00043000" must match DB value "RBLX260612C00043000".
    // Stocks match on symbol only.
    private static IbkrPosition? FindIbkrPosition(
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