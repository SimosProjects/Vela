using System.Diagnostics;
using Vela.Worker.Metrics;
using AlertApiException = Vela.Worker.Services.AlertApiException;

namespace Vela.Worker;

/// <summary>
/// Polls the Xtrades REST API for new alerts on a regular interval and processes them
/// through the normalization, classification, and risk evaluation pipeline.
/// Two API calls are made per cycle: one for entry alerts sorted by entry time, and one
/// for exit alerts sorted by exit time. Combining them into one query causes exits to be
/// buried below entries and fall off the page, which was the root cause of missed STC
/// alerts during SignalR gaps.
///
/// Exit alerts bypass the ID deduplication check that applies to entries. Xtrades reuses
/// the same alert ID for a BTO entry and its corresponding STC exit, so an exit arriving
/// with a previously-saved BTO ID would be incorrectly filtered as a duplicate. The actual
/// safeguard against double-closes is HandleExitAsync's TryMarkClosing mechanism.
/// </summary>
public class AlertPollingService : BackgroundService
{
    private readonly IAlertApiClient _client;
    private readonly IAlertNormalizer _normalizer;
    private readonly RiskEngineService _riskEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PollingOptions _options;
    private readonly ILogger<AlertPollingService> _logger;
    private readonly AlertMetrics _metrics;
    private readonly DiscordNotificationService _discord;
    private readonly BrokerExecutionService _execution;

    public AlertPollingService(
        IAlertApiClient client,
        IAlertNormalizer normalizer,
        RiskEngineService riskEngine,
        IServiceScopeFactory scopeFactory,
        IOptions<PollingOptions> options,
        ILogger<AlertPollingService> logger,
        AlertMetrics metrics,
        DiscordNotificationService discord,
        BrokerExecutionService execution)
    {
        _client = client;
        _normalizer = normalizer;
        _riskEngine = riskEngine;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _discord = discord;
        _execution = execution;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Alert polling service started. Interval: {Interval}s",
            _options.IntervalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.IntervalSeconds),
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            _logger.LogInformation("Alert polling service stopped.");
        }
    }

    // Executes a single poll cycle. Exceptions are caught and logged so the loop always continues.
    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

            // Fetch entries and exits in parallel — independent sort orders, independent pages.
            var (entryAlerts, exitAlerts) = await FetchBothAsync(stoppingToken);

            _metrics.AlertsFetched.Add(entryAlerts.Count + exitAlerts.Count);
            _logger.LogDebug(
                "Fetched {EntryCount} entry alert(s) and {ExitCount} exit alert(s) from API.",
                entryAlerts.Count, exitAlerts.Count);

            // Query existing IDs for both entries and exits. Exits need this so we can avoid
            // re-inserting over an existing BTO row when the same ID is reused for the STC.
            var incomingEntryIds = entryAlerts
                .Where(a => a.Id is not null)
                .Select(a => a.Id!)
                .ToList();

            var incomingExitIds = exitAlerts
                .Where(a => a.Id is not null)
                .Select(a => a.Id!)
                .ToList();

            var existingIds = await repository.GetExistingAlertIdsAsync(
                incomingEntryIds.Concat(incomingExitIds).Distinct().ToList(), stoppingToken);

            // Entries are deduped: skip any ID already seen to avoid re-processing.
            var newEntries = entryAlerts
                .Where(a => a.Id is not null && !existingIds.Contains(a.Id!))
                .ToList();

            // Exits are NOT deduped: process all regardless of whether the ID exists.
            // Xtrades reuses the same alert ID for BTO and STC — filtering by existing ID
            // would silently swallow every exit whose entry was already persisted.
            var exits = exitAlerts.ToList();

            _metrics.AlertsNew.Add(newEntries.Count);
            _logger.LogDebug(
                "New entry alerts after deduplication: {New} / {Total}", newEntries.Count, entryAlerts.Count);

            if (exits.Count == 0 && newEntries.Count == 0)
                return;

            // Process exits
            var exitEntities = new List<AlertEntity>();
            foreach (var rawExit in exits)
            {
                try
                {
                    var normalized = _normalizer.Normalize(rawExit);
                    var riskResult = _riskEngine.Evaluate(normalized);

                    _logger.LogInformation(
                        "REST exit alert [{Side}] {Symbol} by {Trader} — routing to HandleExitAsync",
                        rawExit.Side, rawExit.Symbol, rawExit.UserName);

                    await _execution.HandleExitAsync(normalized, stoppingToken);

                    // Only save if genuinely new — skip if the ID already exists as a BTO row
                    // to avoid a primary key conflict or unintentionally overwriting entry data.
                    if (rawExit.Id is not null && !existingIds.Contains(rawExit.Id))
                        exitEntities.Add(AlertMapper.ToEntity(normalized, riskResult));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "REST exit alert processing failed for {Symbol}.", rawExit.Symbol);
                }
            }

            // Process entries through full pipeline
            var processed = newEntries
                .Where(_normalizer.IsProcessable)
                .Select(_normalizer.Normalize)
                .Select(a => (
                    Alert: a,
                    Classification: AlertClassifier.Classify(a),
                    RiskResult: _riskEngine.Evaluate(a)
                ))
                .ToList();

            var approved = processed.Where(p => p.RiskResult.Approved).ToList();
            var rejected = processed.Where(p => !p.RiskResult.Approved).ToList();

            _metrics.AlertsApproved.Add(approved.Count);
            _metrics.AlertsRejected.Add(rejected.Count);

            if (approved.Count > 0)
                _logger.LogInformation(
                    "Pipeline complete. Approved: {Approved}, Rejected: {Rejected}",
                    approved.Count, rejected.Count);
            else
                _logger.LogDebug(
                    "Pipeline complete. Approved: 0, Rejected: {Rejected}", rejected.Count);

            foreach (var (alert, classification, _) in approved)
            {
                _logger.LogInformation(
                    "APPROVED [{Category}] {Symbol} by {Trader} (xScore: {XScore})",
                    classification.Category, alert.Symbol, alert.UserName, alert.XScore);

                await _discord.NotifyApprovedAlertAsync(alert, classification, stoppingToken);

                if (alert.Side?.ToLower() is "bto")
                    await _execution.HandleEntryAsync(alert, classification, isAverage: false, stoppingToken);
                else if (alert.Side?.ToLower() is "avg")
                    await _execution.HandleEntryAsync(alert, classification, isAverage: true, stoppingToken);
            }

            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "success" } });

            var entryEntities = processed.Select(p => AlertMapper.ToEntity(p.Alert, p.RiskResult)).ToList();
            await repository.SaveManyAsync([..exitEntities, ..entryEntities], stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AlertApiException ex) when (ex.StatusCode is 401 or 403)
        {
            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "auth_error" } });

            _logger.LogError(
                "Xtrades REST API returned {StatusCode} — XTRADES_TOKEN has likely expired or been revoked. " +
                "No alerts will be received until the token is updated and the Worker is restarted.",
                ex.StatusCode);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "error" } });

            _logger.LogError(ex,
                "Poll cycle failed. Will retry in {Interval}s.", _options.IntervalSeconds);
        }
    }

    // -- Helpers --

    // Fetches entries and exits concurrently. If either call fails, the exception is
    // swallowed and an empty list is returned for that side so the other side still
    // processes normally. A partial failure is logged at Warning level.
    private async Task<(List<Alert> Entries, List<Alert> Exits)> FetchBothAsync(
        CancellationToken ct)
    {
        var entryTask = FetchWithFallbackAsync(
            () => _client.GetAlertsAsync(ct), "entry", ct);
        var exitTask = FetchWithFallbackAsync(
            () => _client.GetExitAlertsAsync(ct), "exit", ct);

        await Task.WhenAll(entryTask, exitTask);
        return (await entryTask, await exitTask);
    }

    private async Task<List<Alert>> FetchWithFallbackAsync(
        Func<Task<List<Alert>>> fetch,
        string label,
        CancellationToken ct)
    {
        try
        {
            return await fetch();
        }
        catch (AlertApiException ex) when (ex.StatusCode is 401 or 403)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Alert polling: {Label} fetch failed this cycle — entries still processing normally.",
                label);
            return [];
        }
    }
}