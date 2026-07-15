using Vela.Worker.Configuration;
using Vela.Worker.Engine;
using Vela.Worker.Formatting;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Background service that fires scheduled health checks, position summaries,
/// and analytics reports at fixed times throughout the trading day.
/// Only runs on market days, skipping weekends and fixed US market holidays.
/// All scheduled times are in ET.
/// </summary>
public class MarketSchedulerService : BackgroundService
{
    private readonly DiscordNotificationService _discord;
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly BrokerExecutionService _execution;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketSchedulerService> _logger;
    private readonly IConfiguration _config;
    private readonly RiskEngineOptions _riskOptions;
    private readonly MarketConditionsLogger _marketConditions;
    private readonly IAlertApiClient _alertApiClient;

    private readonly string? _healthWebhookUrl;
    private readonly string? _summaryWebhookUrl;
    private readonly HttpClient _httpClient;
    private readonly CsvTradeLogger _csv;

    // Fixed US market holidays that fall on the same date every year
    private static readonly HashSet<(int Month, int Day)> FixedHolidays =
    [
        (1, 1),   // New Year's Day
        (6, 19),  // Juneteenth
        (7, 4),   // Independence Day
        (12, 25), // Christmas Day
    ];

    public MarketSchedulerService(
        DiscordNotificationService discord,
        TradeGuard guard,
        IBrokerService broker,
        BrokerExecutionService execution,
        IServiceScopeFactory scopeFactory,
        ILogger<MarketSchedulerService> logger,
        IConfiguration config,
        CsvTradeLogger csv,
        IOptions<RiskEngineOptions> riskOptions,
        MarketConditionsLogger marketConditions,
        IHttpClientFactory httpClientFactory,
        IAlertApiClient alertApiClient)
    {
        _discord          = discord;
        _guard            = guard;
        _broker           = broker;
        _execution        = execution;
        _scopeFactory     = scopeFactory;
        _logger           = logger;
        _config           = config;
        _csv              = csv;
        _riskOptions      = riskOptions.Value;
        _httpClient       = httpClientFactory.CreateClient("Scheduler");
        _marketConditions = marketConditions;
        _alertApiClient   = alertApiClient;

        _healthWebhookUrl  = Environment.GetEnvironmentVariable("DISCORD_HEALTH_WEBHOOK_URL");
        _summaryWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_SUMMARY_WEBHOOK_URL");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Market scheduler service started.");

        var schedule = BuildSchedule();

        var firedToday      = new HashSet<string>();
        var lastCheckedDate = DateOnly.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var et    = GetEasternTime();
            var today = DateOnly.FromDateTime(et.DateTime);

            if (today != lastCheckedDate)
            {
                firedToday.Clear();
                lastCheckedDate = today;
            }

            if (!IsMarketDay(et))
                continue;

            foreach (var (hour, minute, task) in schedule)
            {
                var key = $"{today}::{hour}:{minute:D2}::{task}";

                if (firedToday.Contains(key))
                    continue;

                if (et.Hour == hour && et.Minute == minute)
                {
                    firedToday.Add(key);
                    await FireTaskAsync(task, et, stoppingToken);
                }
            }
        }

        _logger.LogInformation("Market scheduler service stopped.");
    }

    private async Task FireTaskAsync(string task, DateTimeOffset et, CancellationToken ct)
    {
        try
        {
            switch (task)
            {
                case "HealthCheck":
                    await SendHealthCheckAsync(ct);
                    break;

                case "PositionSummary":
                    await SendPositionSummaryAsync(ct);
                    break;

                case "SameDayExpiryClose":
                    await CloseSameDayExpiryPositionsAsync(ct);
                    break;

                case "OneDteProfitClose":
                    await HandleOneDteProfitCloseAsync(ct);
                    break;

                case "OneDteLottoConvert":
                    await HandleOneDteLottoConvertAsync(ct);
                    break;

                case "PauseTrading":
                    _execution.IsPaused = true;
                    _logger.LogInformation("Trading paused by scheduler — no new entries will be placed.");
                    break;

                case "MarketConditions":
                    await _marketConditions.LogMarketConditionsAsync(ct);
                    break;

                case "WeeklyReport":
                    if (et.DayOfWeek == DayOfWeek.Friday)
                        await GenerateAnalyticsReportAsync("weekly", ct);
                    break;

                case "MonthlyReport":
                    if (IsLastTradingDayOfMonth(et))
                        await GenerateAnalyticsReportAsync("monthly", ct);
                    break;

                case "WeeklyArchive":
                    if (et.DayOfWeek == DayOfWeek.Friday)
                        await _csv.ArchiveWeekAsync(ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market scheduler failed to execute task {Task}.", task);
        }
    }

    // Closes all open option positions expiring today before liquidity dries up near close.
    // Duplicate entries on the same contract share a TradeGuard match key, the contract
    // deduplication in TradeGuard.CheckAsync prevents this, but InvariantCulture parsing
    // ensures the expiration comparison is locale-independent regardless.
    private async Task CloseSameDayExpiryPositionsAsync(CancellationToken ct)
    {
        var et      = GetEasternTime();
        var todayEt = DateOnly.FromDateTime(et.DateTime);

        var sameDayPositions = _guard.GetOpenTrades()
            .Where(t =>
                t.TradeType == TradeType.Options &&
                t.Expiration is not null &&
                DateTimeOffset.TryParse(
                    t.Expiration,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var expiry) &&
                DateOnly.FromDateTime(expiry.DateTime) == todayEt)
            .ToList();

        if (sameDayPositions.Count == 0)
        {
            _logger.LogInformation("Same-day expiry close: no positions to close.");
            return;
        }

        _logger.LogInformation(
            "Same-day expiry close: force closing {Count} position(s) expiring today.",
            sameDayPositions.Count);

        foreach (var trade in sameDayPositions)
            await _execution.ForceCloseAsync(trade, TradeOutcome.ForcedClose, ct);
    }

    // Fires at 3pm ET, checks options expiring tomorrow.
    // In profit above threshold AND qty > 1 — partial close + remove stop (lotto remainder).
    // In profit below threshold OR qty == 1 — force close entirely.
    // In the red — do nothing, handled at 3:55pm.
    private async Task HandleOneDteProfitCloseAsync(CancellationToken ct)
    {
        var et         = GetEasternTime();
        var tomorrowEt = DateOnly.FromDateTime(et.DateTime).AddDays(1);

        var positions = _guard.GetOpenTrades()
            .Where(t =>
                t.TradeType == TradeType.Options &&
                t.Expiration is not null &&
                DateTimeOffset.TryParse(
                    t.Expiration,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var expiry) &&
                DateOnly.FromDateTime(expiry.DateTime) == tomorrowEt)
            .ToList();

        if (positions.Count == 0)
        {
            _logger.LogInformation("1DTE profit close: no positions expiring tomorrow.");
            return;
        }

        _logger.LogInformation(
            "1DTE profit close: reviewing {Count} position(s) expiring tomorrow.", positions.Count);

        foreach (var trade in positions)
        {
            var currentPrice = await _broker.GetCurrentMarketPriceAsync(
                trade.Symbol,
                trade.TradeType,
                trade.Direction,
                trade.Strike,
                trade.Expiration,
                ct);

            if (currentPrice <= 0)
            {
                _logger.LogWarning(
                    "1DTE profit close: could not get price for {Symbol} — skipping", trade.Symbol);
                continue;
            }

            var multiplier    = 100m;
            var currentPnl    = (currentPrice - trade.EntryPrice) * trade.Quantity * multiplier;
            var currentPnlPct = trade.EntryAmount > 0
                ? currentPnl / trade.EntryAmount * 100
                : 0m;

            if (currentPnlPct <= 0)
            {
                _logger.LogInformation(
                    "1DTE profit close: {Symbol} in red ({PnlPct:F1}%) — skipping, will convert to lotto at close",
                    trade.Symbol, currentPnlPct);
                continue;
            }

            var threshold = (decimal)_riskOptions.OptionCloseThresholdPct;

            if (currentPnlPct > threshold && trade.Quantity > 1)
            {
                var qtyToClose = Math.Max(1, (int)Math.Floor(trade.Quantity * _riskOptions.OptionPartialCloseRatio));
                _logger.LogInformation(
                    "1DTE profit close: {Symbol} +{PnlPct:F1}% > {Threshold}% threshold — partial close {QtyClose}/{TotalQty} contracts",
                    trade.Symbol, currentPnlPct, threshold, qtyToClose, trade.Quantity);

                await _execution.PartialCloseAndRemoveStopAsync(trade, qtyToClose, ct);
            }
            else
            {
                _logger.LogInformation(
                    "1DTE profit close: {Symbol} +{PnlPct:F1}% — force closing full position (qty {Qty} or below threshold)",
                    trade.Symbol, currentPnlPct, trade.Quantity);

                await _execution.ForceCloseAsync(trade, TradeOutcome.ForcedClose, ct);
            }
        }
    }

    // Fires at 3:55pm ET, checks options expiring tomorrow still open and in the red.
    // Cancels the trail stop so the position rides overnight as a lotto play.
    private async Task HandleOneDteLottoConvertAsync(CancellationToken ct)
    {
        var et         = GetEasternTime();
        var tomorrowEt = DateOnly.FromDateTime(et.DateTime).AddDays(1);

        var positions = _guard.GetOpenTrades()
            .Where(t =>
                t.TradeType == TradeType.Options &&
                t.Expiration is not null &&
                t.StopOrderId is not null &&
                DateTimeOffset.TryParse(
                    t.Expiration,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var expiry) &&
                DateOnly.FromDateTime(expiry.DateTime) == tomorrowEt)
            .ToList();

        if (positions.Count == 0)
        {
            _logger.LogInformation("1DTE lotto convert: no positions expiring tomorrow with active stops.");
            return;
        }

        _logger.LogInformation(
            "1DTE lotto convert: converting {Count} position(s) to lotto overnight hold.", positions.Count);

        foreach (var trade in positions)
        {
            if (!int.TryParse(trade.StopOrderId, out var stopId))
                continue;

            await _broker.CancelOrderAsync(stopId, ct);
            _guard.UpdateAfterPartialClose(
                trade.UserName,
                trade.OptionsContract,
                trade.Symbol,
                trade.Quantity);

            _logger.LogInformation(
                "1DTE lotto convert: {Symbol} stop cancelled — holding {Qty} contracts overnight as lotto",
                trade.Symbol, trade.Quantity);
        }
    }

    /// <summary>
    /// Generates an analytics report for the given period by spawning Vela.Analytics
    /// as a subprocess. Times out after 5 minutes to prevent a hung process blocking the scheduler.
    /// </summary>
    private async Task GenerateAnalyticsReportAsync(string reportType, CancellationToken ct)
    {
        _logger.LogInformation(
            "Market scheduler generating {ReportType} analytics report.", reportType);

        System.Diagnostics.Process? process = null;

        try
        {
            var reportsDir = _config["Analytics:ReportsDirectory"];

            reportsDir = reportsDir is not null
                ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, reportsDir))
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "reports"));

            var analyticsDll = Path.Combine(AppContext.BaseDirectory, "Vela.Analytics.dll");

            string fileName, arguments;
            if (File.Exists(analyticsDll))
            {
                fileName  = "dotnet";
                arguments = $"{analyticsDll} --report {reportType} --output \"{reportsDir}\"";
            }
            else
            {
                var projectPath = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory,
                        "..", "..", "..", "..",
                        "Vela.Analytics",
                        "Vela.Analytics.csproj"));

                fileName  = "dotnet";
                arguments = $"run --project \"{projectPath}\" -- --report {reportType} --output \"{reportsDir}\"";
            }

            process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = fileName,
                    Arguments              = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                }
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "{ReportType} analytics process exceeded 5 minute timeout — killing process.",
                    reportType);

                try { process.Kill(); } catch { /* ignore kill errors */ }
                return;
            }

            if (process.ExitCode == 0)
                _logger.LogInformation(
                    "{ReportType} analytics report generated successfully.", reportType);
            else
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning(
                    "{ReportType} analytics report exited with code {Code}. Error: {Error}",
                    reportType, process.ExitCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate {ReportType} analytics report.", reportType);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task SendHealthCheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_healthWebhookUrl))
        {
            _logger.LogWarning("DISCORD_HEALTH_WEBHOOK_URL not set. Skipping health check.");
            return;
        }

        _logger.LogInformation("Market scheduler firing health check.");

        var workerStatus   = "✅ Running";
        var ibkrStatus     = await CheckIbkrAsync(ct);
        var postgresStatus = await CheckPostgresAsync(ct);
        var xtradesStatus  = await CheckXtradesAsync(ct);
        var signalrStatus  = "✅ Connected";

        var fields = new[]
        {
            Field("Worker",       workerStatus),
            Field("IB Gateway",   ibkrStatus),
            Field("PostgreSQL",   postgresStatus),
            Field("Xtrades REST", xtradesStatus),
            Field("SignalR",      signalrStatus),
        };

        var embed = new
        {
            title  = "🔍 SYSTEM HEALTH CHECK",
            color  = 0x3498DB,
            fields,
            footer = new { text = "Vela Health" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        await PostToWebhookAsync(_healthWebhookUrl, embed, ct);
    }

    private async Task SendPositionSummaryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_summaryWebhookUrl))
        {
            _logger.LogWarning("DISCORD_SUMMARY_WEBHOOK_URL not set. Skipping position summary.");
            return;
        }

        _logger.LogInformation("Market scheduler firing position summary.");

        var account   = await _broker.GetAccountSnapshotAsync(ct);
        var positions = await _broker.GetAllPositionsAsync(ct);
        var orders    = await _broker.GetAllOpenOrdersAsync(ct);

        if (account.TimedOut || positions.TimedOut || orders.TimedOut)
        {
            _logger.LogWarning("Position summary skipped — one or more IB queries timed out.");
            return;
        }

        var message = IbSnapshotFormatter.BuildSnapshotMessage(
            account, positions.Positions, orders.Orders);

        await _discord.NotifyIbSnapshotAsync(message, ct);
    }

    private async Task<string> CheckIbkrAsync(CancellationToken ct)
    {
        try
        {
            var balance = await _broker.GetAccountBalanceAsync(ct);
            return balance > 0 ? "✅ Connected" : "⚠️ Connected (no balance)";
        }
        catch
        {
            return "❌ Disconnected";
        }
    }

    private async Task<string> CheckPostgresAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            await repo.GetExistingAlertIdsAsync([], ct);
            return "✅ Connected";
        }
        catch
        {
            return "❌ Disconnected";
        }
    }

    private async Task<string> CheckXtradesAsync(CancellationToken ct)
    {
        // Uses the authenticated Xtrades client so a dead token reports as expired
        // rather than falsely reporting the service as reachable.
        try
        {
            var connected = await _alertApiClient.CheckConnectionAsync(ct);
            return connected ? "✅ Connected" : "❌ Unreachable";
        }
        catch (AlertApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return "❌ Token Expired";
        }
        catch
        {
            return "❌ Unreachable";
        }
    }

    private async Task PostToWebhookAsync(string url, object embed, CancellationToken ct)
    {
        try
        {
            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(url, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post to Discord webhook.");
        }
    }

    private (int Hour, int Minute, string Task)[] BuildSchedule()
    {
        var cutoff = TimeOnly.TryParse(_riskOptions.SameDayExpiryAutoCloseCutoff, out var t)
            ? t
            : new TimeOnly(15, 30);

        return
        [
            (9,           0,            "HealthCheck"),
            (9,           10,           "PositionSummary"),   
            (9,           20,           "MarketConditions"),
            (11,          0,            "MarketConditions"),
            (11,          10,           "PositionSummary"),   
            (11,          23,           "HealthCheck"),
            (13,          0,            "MarketConditions"),
            (13,          10,           "PositionSummary"),   
            (13,          17,           "HealthCheck"),
            (cutoff.Hour, cutoff.Minute,"SameDayExpiryClose"),
            (14,          0,            "MarketConditions"),
            (14,          10,           "PositionSummary"),   
            (15,          0,            "OneDteProfitClose"),
            (15,          55,           "OneDteLottoConvert"),
            (16,          5,            "HealthCheck"),
            (16,          10,           "PositionSummary"), 
            (16,          20,           "WeeklyReport"),
            (16,          25,           "MonthlyReport"),
            (16,          30,           "WeeklyArchive"),
        ];
    }

    private static bool IsLastTradingDayOfMonth(DateTimeOffset et)
    {
        var today       = DateOnly.FromDateTime(et.DateTime);
        var lastOfMonth = new DateOnly(today.Year, today.Month,
            DateTime.DaysInMonth(today.Year, today.Month));

        for (var d = today.AddDays(1); d <= lastOfMonth; d = d.AddDays(1))
        {
            var dow = d.DayOfWeek;
            if (dow is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            if (FixedHolidays.Contains((d.Month, d.Day)))
                continue;
            return false;
        }

        return true;
    }

    private static bool IsMarketDay(DateTimeOffset et)
    {
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (FixedHolidays.Contains((et.Month, et.Day)))
            return false;

        return true;
    }

    private static DateTimeOffset GetEasternTime() =>
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static object Field(string name, string value) =>
        new { name, value, inline = false };
}