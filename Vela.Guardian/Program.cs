using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Formatting;
using Vela.Worker.Models;
using Vela.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Only Error+ from framework services (IbkrConnectionService/IbkrEWrapper etc.) prints —
// their routine Info/Warning narration (Connected, keepalive started, EReader thread
// exiting, ...) is diagnostic noise for this console tool. Guardian's own status messages
// are plain Console.WriteLine below, not routed through ILogger, so this level doesn't
// affect them.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Error);

// Reads IBKR__HOST / IBKR__PORT / IBKR__ACCOUNTID from the environment, same as
// Vela.Worker's IbkrOptions binding — missing values fall back to IbkrOptions' own
// defaults (Host=127.0.0.1, Port=4002).
builder.Services
    .AddOptions<IbkrOptions>()
    .Bind(builder.Configuration.GetSection("Ibkr"));

// GUARDIAN_CLIENT_ID must differ from Vela.Worker's ClientId so both can hold
// independent sessions against the same Gateway without one kicking the other off.
builder.Services.PostConfigure<IbkrOptions>(options =>
{
    options.ClientId = int.TryParse(
        Environment.GetEnvironmentVariable("GUARDIAN_CLIENT_ID"), out var clientId)
        ? clientId
        : 5;
});

builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddSingleton<IbkrConnectionService>();
builder.Services.AddSingleton<IbkrBrokerService>();

// RiskEngineOptions and the DB connection string come from Vela.Worker's own
// appsettings.json, linked into Guardian's own build output (see Vela.Guardian.csproj)
// so this resolves correctly regardless of the working directory the tool is launched
// from. Read independently of builder.Configuration so environment variables (Ibkr__*)
// still take precedence for IbkrOptions above, unaffected by whatever this file contains.
var workerConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var connectionString = workerConfig.GetConnectionString("Vela")
    ?? throw new InvalidOperationException("Vela connection string is not configured.");

builder.Services
    .AddOptions<RiskEngineOptions>()
    .Bind(workerConfig.GetSection(RiskEngineOptions.SectionName));

builder.Services.AddDbContext<VelaDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});
builder.Services.AddScoped<IOpenPositionRepository, OpenPositionRepository>();
builder.Services.AddScoped<ITradeMetricsRepository, TradeMetricsRepository>();

var host = builder.Build();

var connection = host.Services.GetRequiredService<IbkrConnectionService>();
var broker = host.Services.GetRequiredService<IbkrBrokerService>();

Console.WriteLine("Connecting to IB Gateway...");

if (!connection.Connect())
{
    Console.WriteLine("ERROR: Failed to connect to IB Gateway. Aborting.");
    return;
}

Console.WriteLine("Connected.");

try
{
    // Replicates Vela.Worker's Program.cs startup sequence: wait for Gateway's real
    // nextValidId, then seed IbkrBrokerService's order ID counter from it (plus the DB's
    // historical high-water mark). Without this, a freshly constructed IbkrBrokerService
    // starts _nextOrderId at its class default of 1, so its first placed order always
    // requests order ID 11 — a near-guaranteed collision with whatever Vela.Worker (or an
    // earlier Guardian run) already used this Gateway session. Unlike
    // IbkrConnectionService.WaitForNextValidIdAsync, which logs a warning and proceeds
    // anyway on timeout, Guardian cannot tolerate placing orders with an unseeded counter,
    // so this uses its own timeout and aborts instead.
    Console.WriteLine("Waiting for Gateway session to initialize (nextValidId)...");

    int gatewayNextValidId;
    try
    {
        using var nextIdCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        gatewayNextValidId = await connection.Wrapper.WaitForNextValidIdAsync().WaitAsync(nextIdCts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine(
            "ERROR: Timed out waiting for nextValidId from Gateway — order ID counter is " +
            "unseeded. Aborting before any order placement.");
        return;
    }

    int maxDbOrderId;
    using (var metricsScope = host.Services.CreateScope())
    {
        var metricsRepo = metricsScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
        maxDbOrderId = await metricsRepo.GetMaxOrderIdAsync();
    }

    broker.SyncOrderId(maxDbOrderId);
    broker.SyncReqId(gatewayNextValidId);

    Console.WriteLine("Fetching account data...");

    var account = await broker.GetAccountSnapshotAsync();
    var positions = await broker.GetAllPositionsAsync();
    var orders = await broker.GetAllOpenOrdersAsync();

    if (account.TimedOut || positions.TimedOut || orders.TimedOut)
    {
        Console.WriteLine(
            "WARNING: One or more IB queries timed out (Account={0}, Positions={1}, Orders={2}) " +
            "— refusing to show a possibly-incomplete summary.",
            account.TimedOut, positions.TimedOut, orders.TimedOut);
        return;
    }

    Console.WriteLine(
        IbSnapshotFormatter.BuildSnapshotMessage(account, positions.Positions, orders.Orders));

    Console.WriteLine();
    Console.WriteLine("======== UNPROTECTED POSITIONS ========");

    var unprotected = IbSnapshotFormatter.GetUnprotectedPositions(positions.Positions, orders.Orders);
    if (unprotected.Count == 0)
    {
        Console.WriteLine("None — all positions protected.");
    }
    else
    {
        foreach (var position in unprotected)
            Console.WriteLine($"{Label(position)} — qty {position.Quantity} @ avgCost ${position.AvgCost:F2}");
    }

    // -- Interactive remediation --

    var riskOptions = host.Services.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    List<OpenPosition> dbPositions;
    using (var scope = host.Services.CreateScope())
    {
        var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
        dbPositions = await repo.GetAllAsync();
    }

    foreach (var ibkrPosition in unprotected)
    {
        var matched = FindDbMatch(ibkrPosition, dbPositions);

        if (matched is null)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"⚠️  ORPHANED — {ibkrPosition.Symbol}: no matching open_positions record. " +
                "Skipping — investigate manually.");
            continue;
        }

        var tradeType = Enum.Parse<TradeType>(matched.TradeType);
        var suggestedTrailPct = tradeType == TradeType.Options
            ? riskOptions.OptionsStandardTrailPct
            : riskOptions.StockStandardTrailPct;

        Console.WriteLine();
        Console.WriteLine(
            $"{ibkrPosition.Symbol} — qty {ibkrPosition.Quantity} @ avgCost ${ibkrPosition.AvgCost:F2}");

        if (ibkrPosition.SecType == "OPT")
        {
            var contractLine = IbSnapshotFormatter.FormatOccContractLine(ibkrPosition.LocalSymbol);
            if (contractLine is not null)
                Console.WriteLine(contractLine);
        }

        Console.Write(
            $"Configure trail stop? [Y/n/or type a custom %] (suggested: {suggestedTrailPct}%): ");

        var input = Console.ReadLine()?.Trim() ?? "";

        double trailPercent;
        if (input.Length == 0 || string.Equals(input, "y", StringComparison.OrdinalIgnoreCase))
        {
            trailPercent = suggestedTrailPct;
        }
        else if (string.Equals(input, "n", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Skipped {ibkrPosition.Symbol}.");
            continue;
        }
        else if (double.TryParse(input, out var customPercent) && customPercent is >= 0.1 and <= 100)
        {
            trailPercent = customPercent;
        }
        else
        {
            Console.WriteLine($"Invalid input '{input}' — skipping {ibkrPosition.Symbol}.");
            continue;
        }

        // If this position already has a resting take-profit LMT order, place a genuine OCA
        // trail+target pair instead of a bare stop — a bare stop would leave the existing
        // target dangling, uncoordinated with the new stop. No existing target means the
        // current bare-stop path is unchanged.
        var (existingTarget, targetAmbiguous) =
            IbSnapshotFormatter.GetMatchingTargetOrder(ibkrPosition, orders.Orders);

        if (targetAmbiguous)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"❌ {ibkrPosition.Symbol} has multiple live target/stop orders — cannot safely " +
                "determine which is real. Manual cleanup required in IB before this position can be remediated.");
            continue;
        }

        string? newOrderId;
        string? newTargetOrderId = null;

        if (existingTarget is not null && existingTarget.LmtPrice.HasValue)
        {
            (newOrderId, newTargetOrderId) = await broker.PlaceProtectiveStopWithTargetAsync(
                matched.Symbol,
                tradeType,
                matched.OptionsContract,
                matched.Direction,
                matched.Strike,
                matched.Expiration,
                ibkrPosition.Quantity,
                trailPercent,
                existingTarget.OrderId.ToString(),
                (decimal)existingTarget.LmtPrice.Value);
        }
        else
        {
            newOrderId = await broker.PlaceProtectiveStopAsync(
                matched.Symbol,
                tradeType,
                matched.OptionsContract,
                matched.Direction,
                matched.Strike,
                matched.Expiration,
                ibkrPosition.Quantity,
                trailPercent);
        }

        if (newOrderId is not null)
        {
            // A non-null return from PlaceProtectiveStopAsync is not proof the order is
            // actually live — see the 2026-07-15 BROS incident, where a Guardian-placed
            // order ID was written to the database and trusted, but was never durably
            // reserved against Gateway's own order counter and was later recycled for an
            // unrelated trade, silently closing BROS's trade record. Re-fetching a fresh
            // open orders snapshot and confirming the ID actually appears there, live,
            // before printing success or touching the database closes that gap.
            var stopConfirmed = await ConfirmOrderIsLiveAsync(broker, newOrderId);
            var targetConfirmed = newTargetOrderId is null || await ConfirmOrderIsLiveAsync(broker, newTargetOrderId);

            if (!stopConfirmed || !targetConfirmed)
            {
                Console.WriteLine(
                    $"❌ {ibkrPosition.Symbol} — IBKR returned OrderId {newOrderId}" +
                    (newTargetOrderId is not null ? $" / Target OrderId {newTargetOrderId}" : "") +
                    " but it does not appear live in a fresh open orders snapshot. " +
                    "Database NOT updated — manual verification required in IB before trusting this order.");
            }
            else
            {
                using var scope = host.Services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();

                if (newTargetOrderId is not null)
                {
                    await repo.UpdateStopAndTargetOrderIdsAsync(matched.OrderId, newOrderId, newTargetOrderId);
                    Console.WriteLine(
                        $"✅ Stop placed — OrderId {newOrderId}, Target OrderId {newTargetOrderId}. " +
                        "Confirmed live at IB. Database updated.");
                }
                else
                {
                    await repo.UpdateStopOrderIdAsync(matched.OrderId, newOrderId);
                    Console.WriteLine($"✅ Stop placed — OrderId {newOrderId}. Confirmed live at IB. Database updated.");
                }
            }
        }
        else if (existingTarget is not null && existingTarget.LmtPrice.HasValue)
        {
            // Distinct from the generic bare-stop rejection below — the existing target was
            // already cancelled as part of this attempt, so this is not a normal skip, the
            // position may currently have neither the old target nor any new protection.
            Console.WriteLine(
                $"❌ Failed to safely re-pair {ibkrPosition.Symbol} — existing target order may " +
                "still be live alongside no new protection. Manual check required in IB.");
        }
        else
        {
            Console.WriteLine(
                "❌ IBKR rejected the order. Nothing placed, database not touched. " +
                "Manual placement required.");
        }
    }

    // -- Final verification --

    Console.WriteLine();
    Console.WriteLine("======== FINAL VERIFICATION ========");
    Console.WriteLine("Fetching account data...");

    var finalAccount = await broker.GetAccountSnapshotAsync();
    var finalPositions = await broker.GetAllPositionsAsync();
    var finalOrders = await broker.GetAllOpenOrdersAsync();

    if (finalAccount.TimedOut || finalPositions.TimedOut || finalOrders.TimedOut)
    {
        Console.WriteLine(
            "WARNING: One or more IB queries timed out on final verification (Account={0}, " +
            "Positions={1}, Orders={2}) — cannot confirm current state.",
            finalAccount.TimedOut, finalPositions.TimedOut, finalOrders.TimedOut);
    }
    else
    {
        Console.WriteLine(
            IbSnapshotFormatter.BuildSnapshotMessage(finalAccount, finalPositions.Positions, finalOrders.Orders));

        Console.WriteLine();
        Console.WriteLine("STILL UNPROTECTED (needs manual attention):");
        var stillUnprotected = IbSnapshotFormatter.GetUnprotectedPositions(finalPositions.Positions, finalOrders.Orders);
        if (stillUnprotected.Count == 0)
            Console.WriteLine("None.");
        else
            foreach (var position in stillUnprotected)
                Console.WriteLine($"{Label(position)} — qty {position.Quantity} @ avgCost ${position.AvgCost:F2}");

        Console.WriteLine();
        Console.WriteLine("⚠️  DUPLICATE STOPS DETECTED (needs manual cleanup):");
        var duplicates = IbSnapshotFormatter.GetDuplicateProtectedPositions(finalPositions.Positions, finalOrders.Orders);
        if (duplicates.Count == 0)
            Console.WriteLine("None.");
        else
            foreach (var position in duplicates)
                Console.WriteLine($"{Label(position)} — qty {position.Quantity} @ avgCost ${position.AvgCost:F2}");
    }
}
finally
{
    Console.WriteLine();
    Console.WriteLine("Disconnecting...");
    connection.Dispose();
    Console.WriteLine("Done.");
}

// Display label for a position: LocalSymbol (options, e.g. "TSLA260620C00450000") or Symbol (stocks).
static string Label(IbkrPosition position) =>
    position.SecType == "OPT" ? position.LocalSymbol ?? position.Symbol : position.Symbol;

// Confirms a just-placed order ID is actually live at IB, via a fresh reqAllOpenOrders
// snapshot, rather than trusting PlaceProtectiveStopAsync's return value on its own.
static async Task<bool> ConfirmOrderIsLiveAsync(IbkrBrokerService broker, string orderId)
{
    if (!int.TryParse(orderId, out var id))
        return false;

    var snapshot = await broker.GetAllOpenOrdersAsync();
    if (snapshot.TimedOut)
        return false;

    return snapshot.Orders.Any(o =>
        o.OrderId == id && IbSnapshotFormatter.LiveOrderStatuses.Contains(o.Status));
}

// Same matching logic as PeriodicReconciliationService's private HasDbMatch helper, duplicated
// here rather than shared since it's a few lines and the two callers have no other coupling.
// Options match by OptionsContract == LocalSymbol with spaces stripped, stocks match by Symbol.
static OpenPosition? FindDbMatch(IbkrPosition ibkrPosition, List<OpenPosition> dbPositions)
{
    if (ibkrPosition.SecType == "OPT")
    {
        var localSymbol = ibkrPosition.LocalSymbol?.Replace(" ", "") ?? "";
        return dbPositions.FirstOrDefault(db =>
            db.TradeType == "Options" &&
            db.OptionsContract is not null &&
            string.Equals(
                db.OptionsContract.Replace(" ", ""),
                localSymbol,
                StringComparison.OrdinalIgnoreCase));
    }

    return dbPositions.FirstOrDefault(db =>
        db.TradeType != "Options" &&
        string.Equals(db.Symbol, ibkrPosition.Symbol, StringComparison.OrdinalIgnoreCase));
}
