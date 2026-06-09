using TradeFlow.AlertPoC.RiskEngine;

var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[ERROR] XTRADES_TOKEN environment variable is not set.");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"[INFO] Token loaded ({token.Length} chars)");

var allowedRanks = new List<string>
{
    "Top Analyst",
    "Analyst",
    "Junior Analyst",
    "Top Trader"
};
var approvedTraders = new List<string> { "Fibonaccizer", "Theo", "Avalace" };

var client = new AlertApiClient(token);
var normalizer = new AlertNormalizer();

var riskEngine = new RiskEngineService([
    new EntryOnlyRule(),
    new NoLottoRule(configDisabled: false, isChoppy: () => false, chopScore: () => 0),
    new MinXScoreRule(minimumScore: 60.0),
    new ApprovedTraderRule(approvedTraders),
]);
    
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // prevent the process from terminating immediately
    cts.Cancel();    // signal our token so async operations can wind down cleanly
    Console.WriteLine("\n[INFO] Cancellation requested — shutting down...");
};

List<Alert> alerts;

try
{
    alerts = await client.GetAlertsAsync(cts.Token);
}
catch (AlertApiException ex)
{
    // AlertApiException is our domain boundary — we don't leak HttpClient
    // or JSON details to the top level, just a clean failure message
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"\n[INFO] Alerts received: {alerts.Count}");

// Pipeline: validate -> normalize -> classify -> risk evaluate
var processed = alerts
    .Where(normalizer.IsProcessable) // Filter out any alerts missing required properties
    .Select(normalizer.Normalize)   // Normalize remaining alerts for consistent downstream processing
    .Select(a => (
        Alert: a, 
        Classification: AlertClassifier.Classify(a),
        RiskResult: riskEngine.Evaluate(a)
        ))
    .ToList();

Console.WriteLine($"[INFO] Processable: {processed.Count} / {alerts.Count}");
Console.WriteLine($"[INFO] Approved: {processed.Count(p => p.RiskResult.Approved)}");
Console.WriteLine($"[INFO] Rejected: {processed.Count(p => !p.RiskResult.Approved)}\n");
Console.WriteLine(new string('─', 60));

// Cap at 10 for POC readability — the full pipeline will persist all records
foreach (var (alert, classification, riskResult) in processed.Take(10))
{
    // Color-code the console output based on alert category for quick visual scanning.
    Console.ForegroundColor = classification.Category switch
    {
        AlertCategory.CallOptionEntry or
        AlertCategory.PutOptionEntry or
        AlertCategory.StockEntry => ConsoleColor.Green,
        AlertCategory.CallOptionExit or
        AlertCategory.PutOptionExit or
        AlertCategory.StockExit => ConsoleColor.Yellow,
        _ => ConsoleColor.Gray
    };

    Console.WriteLine($"  [{classification.Description}] " + $"{(riskResult.Approved ? " APPROVED" : $" REJECTED")}");
    Console.ResetColor();

    if (!riskResult.Approved)
    {
        // Show rejection reason so we can verify rules are firing correctly
        Console.WriteLine($" Reason : {riskResult.Reason}");
        Console.WriteLine($" Trader : {alert.UserName}");
        Console.WriteLine($" Symbol : {alert.Symbol}");
        Console.WriteLine(new string('─', 60));
        continue; // Skip details for rejected alerts to reduce noise in the POC output
    }

    Console.WriteLine($"  ID          : {alert.Id}");
    Console.WriteLine($"  Trader      : {alert.UserName}  (xScore: {alert.XScore})  |  Rank: {alert.DiscordRank ?? "Unranked"}");
    Console.WriteLine($"  Symbol      : {alert.Symbol}");
    Console.WriteLine($"  Side        : {alert.Side}  |  Risk: {alert.Risk}");
    Console.WriteLine($"  Direction   : {alert.Direction}");
    Console.WriteLine($"  Strike      : {alert.Strike?.ToString() ?? "—"}");
    Console.WriteLine($"  Expiry      : {alert.Expiration ?? "—"}");
    Console.WriteLine($"  Contract    : {alert.ContractDescription ?? "—"}");
    Console.WriteLine($"  Entry Price : {alert.PricePaid}");
    Console.WriteLine($"  Last Price  : {alert.LastCheckedPrice}");
    Console.WriteLine($"  Result      : {alert.Result}  ({alert.LastKnownPercentProfit:P2})");
    Console.WriteLine($"  Length      : {alert.FormattedLength}");
    Console.WriteLine($"  Message     : {alert.OriginalMessage}");
    Console.WriteLine(new string('─', 60));
}

// Summarize by result — quick sanity check on the data coming back
var entries = processed.Count(p => AlertClassifier.IsEntry(p.Classification));
var wins    = processed.Count(p => p.Alert.Result == "win");
var losses  = processed.Count(p => p.Alert.Result == "loss");
var active  = processed.Count(p => p.Alert.Result == "inProgress");

Console.WriteLine($"\n[INFO] Entries : {entries}  |  Exits: {processed.Count - entries}");
Console.WriteLine($"[INFO] Wins    : {wins}  |  Losses: {losses}  |  Active: {active}");

return 0;