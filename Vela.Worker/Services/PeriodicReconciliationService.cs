using Vela.Worker.Engine;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Background service that periodically reconciles IBKR account state against
/// TradeGuard and the database during market hours.
///
/// Runs every 30 minutes between 9:30am and 4:00pm ET. Three checks per cycle:
/// 1. Managed positions, verify each TradeGuard position still exists in IBKR.
///    Consecutive misses are tracked, after two missed cycles (one hour) the
///    position is auto-removed from TradeGuard and the DB. A single miss only
///    warns so transient Gateway delays do not trigger premature cleanup.
/// 2. New manual positions, detect IBKR longs not in the DB and create tracking
///    records (is_manual=true) so they appear on the dashboard.
/// 3. Closed manual positions, remove tracking records for manual positions that
///    are no longer in IBKR and Discord-note the closure.
///
/// Does not cover shorts (startup concern) or classify orders (startup concern).
/// </summary>
public class PeriodicReconciliationService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Number of consecutive reconciliation cycles a managed position must be absent
    // from IBKR before it is auto-removed. Each cycle is 30 minutes, so 2 = 1 hour.
    private const int AutoCleanupAfterMisses = 2;

    private readonly IBrokerService _broker;
    private readonly TradeGuard _guard;
    private readonly DiscordNotificationService _discord;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PeriodicReconciliationService> _logger;

    // Tracks consecutive IBKR misses per OrderId. Cleared when the position is found.
    private readonly Dictionary<string, int> _missedChecks = new();

    public PeriodicReconciliationService(
        IBrokerService broker,
        TradeGuard guard,
        DiscordNotificationService discord,
        IServiceScopeFactory scopeFactory,
        ILogger<PeriodicReconciliationService> logger)
    {
        _broker       = broker;
        _guard        = guard;
        _discord      = discord;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Periodic reconciliation service started — checks every {Minutes} minutes during market hours.",
            CheckInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            if (!IsMarketOpen()) continue;

            _logger.LogInformation("Periodic reconciliation — running position check.");

            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic reconciliation — unhandled error.");
            }
        }
    }

    // -- Reconciliation steps --

    private async Task RunAsync(CancellationToken ct)
    {
        var snapshot = await _broker.GetAllPositionsAsync(ct);

        if (snapshot.TimedOut)
        {
            _logger.LogWarning(
                "Periodic reconciliation — GetAllPositions timed out. Skipping this cycle.");
            return;
        }

        await CheckManagedPositionsAsync(snapshot.Positions, ct);
        await DetectNewManualPositionsAsync(snapshot.Positions, ct);
        await CleanClosedManualPositionsAsync(snapshot.Positions, ct);
    }

    // Verifies each TradeGuard-managed position still exists in IBKR.
    // A single miss warns via Discord, the position might have closed without
    // a fill callback reaching the Worker (trail stop, manual close in IBKR).
    // After AutoCleanupAfterMisses consecutive misses the position is removed
    // from TradeGuard and the DB so it does not block future entries on the
    // same contract or consume risk budget.
    private async Task CheckManagedPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        CancellationToken ct)
    {
        var openTrades = _guard.GetOpenTrades();
        if (openTrades.Count == 0) return;

        foreach (var trade in openTrades)
        {
            if (FindIbkrPositionForTrade(ibkrPositions, trade) is not null)
            {
                // Position confirmed — clear any previous miss streak
                _missedChecks.Remove(trade.OrderId);
                continue;
            }

            _missedChecks.TryGetValue(trade.OrderId, out var misses);
            _missedChecks[trade.OrderId] = ++misses;

            if (misses < AutoCleanupAfterMisses)
            {
                _logger.LogWarning(
                    "Periodic reconciliation — {Symbol} (OrderId {OrderId}) not found in IBKR " +
                    "({Misses}/{Threshold} consecutive misses). May have closed without a fill callback.",
                    trade.Symbol, trade.OrderId, misses, AutoCleanupAfterMisses);

                await _discord.NotifyCriticalAsync(
                    $"⚠️ Position Discrepancy — {trade.Symbol}",
                    $"Managed position **{trade.Symbol}** (OrderId {trade.OrderId}) is open in " +
                    "Vela but not found in IBKR. It may have closed without Vela detecting the fill. " +
                    $"Will auto-remove after {AutoCleanupAfterMisses - misses} more missed cycle(s) " +
                    "if still absent.",
                    ct);

                continue;
            }

            // Threshold reached, remove from TradeGuard and DB
            _logger.LogWarning(
                "Periodic reconciliation — auto-removing {Symbol} (OrderId {OrderId}) after " +
                "{Misses} consecutive missed cycles. Position assumed closed at IBKR.",
                trade.Symbol, trade.OrderId, misses);

            _missedChecks.Remove(trade.OrderId);
            _guard.RemovePosition(trade.OrderId);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.DeleteAsync(trade.OrderId, ct);
            }

            await _discord.NotifyCriticalAsync(
                $"🧹 Ghost Position Removed — {trade.Symbol}",
                $"Managed position **{trade.Symbol}** (OrderId {trade.OrderId}) was absent " +
                $"from IBKR for {misses} consecutive reconciliation cycles and has been removed " +
                "from Vela tracking. If this position still exists at IBKR, manual reconciliation " +
                "is required.",
                ct);
        }
    }

    // Detects IBKR long positions not tracked in the DB or TradeGuard.
    // These are manual trades placed directly in IBKR. Creates tracking records
    // (is_manual=true) so they appear on the dashboard — no management, tracking only.
    private async Task DetectNewManualPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        CancellationToken ct)
    {
        var longPositions = ibkrPositions.Where(p => p.Quantity > 0).ToList();
        if (longPositions.Count == 0) return;

        List<OpenPosition> allDbPositions;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            allDbPositions = await repo.GetAllAsync(ct);
        }

        var openTrades = _guard.GetOpenTrades();

        foreach (var ibkrPos in longPositions)
        {
            if (HasDbMatch(ibkrPos, allDbPositions)) continue;
            if (HasTradeGuardMatch(ibkrPos, openTrades)) continue;

            _logger.LogInformation(
                "Periodic reconciliation — new untracked position: {Symbol} {SecType} qty {Qty}. " +
                "Creating manual tracking record.",
                ibkrPos.Symbol, ibkrPos.SecType, ibkrPos.Quantity);

            var manualPos = BuildManualPosition(ibkrPos);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.SaveAsync(manualPos, ct);
            }

            var is0Dte  = IsExpiringToday(ibkrPos);
            var title   = is0Dte
                ? $"⚠️ 0DTE Manual Position Detected — {ibkrPos.Symbol}"
                : $"📋 Manual Position Detected — {ibkrPos.Symbol}";
            var message = $"Position in **{ibkrPos.Symbol}** " +
                $"(qty {ibkrPos.Quantity} @ ${ibkrPos.AvgCost:F2}) " +
                "is not managed by Vela. Tracking as MANUAL — " +
                "all stop and exit management must be done directly in IBKR." +
                (is0Dte ? " ⚠️ This position expires today." : "");

            await _discord.NotifyCriticalAsync(title, message, ct);
        }
    }

    // Removes manual tracking records for positions that are no longer in IBKR.
    // The user closed them via IBKR — Vela removes the dashboard entry and notes the closure.
    private async Task CleanClosedManualPositionsAsync(
        List<IbkrPosition> ibkrPositions,
        CancellationToken ct)
    {
        List<OpenPosition> allDbPositions;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            allDbPositions = await repo.GetAllAsync(ct);
        }

        var manualPositions = allDbPositions.Where(p => p.IsManual).ToList();
        if (manualPositions.Count == 0) return;

        foreach (var manual in manualPositions)
        {
            if (FindIbkrPositionForDb(ibkrPositions, manual) is not null) continue;

            _logger.LogInformation(
                "Periodic reconciliation — manual position {Symbol} no longer in IBKR. " +
                "Removing tracking record.",
                manual.Symbol);

            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
                await repo.DeleteAsync(manual.OrderId, ct);
            }

            await _discord.NotifyCriticalAsync(
                $"📋 Manual Position Closed — {manual.Symbol}",
                $"Manual tracking position **{manual.Symbol}** " +
                $"(qty {manual.Quantity}) is no longer in IBKR. Tracking record removed.",
                ct);
        }
    }

    // -- Helpers --

    private static OpenPosition BuildManualPosition(IbkrPosition ibkrPos)
    {
        var isOptions  = ibkrPos.SecType == "OPT";
        var multiplier = isOptions ? 100m : 1m;

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
            EntryPrice      = ibkrPos.AvgCost,
            EntryAmount     = ibkrPos.AvgCost * ibkrPos.Quantity * multiplier,
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

    private static bool HasTradeGuardMatch(
        IbkrPosition ibkrPos,
        IReadOnlyList<TradeRecord> openTrades)
    {
        return openTrades.Any(trade => FindIbkrPositionForTrade([ibkrPos], trade) is not null);
    }

    private static IbkrPosition? FindIbkrPositionForTrade(
        List<IbkrPosition> ibkrPositions,
        TradeRecord trade)
    {
        if (trade.TradeType == TradeType.Options && trade.OptionsContract is not null)
        {
            var contract = trade.OptionsContract.Replace(" ", "");
            return ibkrPositions.FirstOrDefault(p =>
                p.SecType == "OPT" &&
                string.Equals(
                    p.LocalSymbol?.Replace(" ", ""),
                    contract,
                    StringComparison.OrdinalIgnoreCase));
        }

        return ibkrPositions.FirstOrDefault(p =>
            p.SecType == "STK" &&
            string.Equals(p.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase));
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

    private static bool IsExpiringToday(IbkrPosition ibkrPos)
    {
        if (ibkrPos.SecType != "OPT" || ibkrPos.LocalSymbol is null) return false;

        var localSymbol = ibkrPos.LocalSymbol.Replace(" ", "");

        // Skip alphabetic symbol prefix to reach the 6-digit YYMMDD expiry date
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

    private static bool IsMarketOpen()
    {
        var et = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = et.TimeOfDay;
        return t >= new TimeSpan(9, 30, 0) && t < new TimeSpan(16, 0, 0);
    }
}