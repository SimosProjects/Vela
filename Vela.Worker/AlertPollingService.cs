using System.Diagnostics;
using Vela.Worker.Metrics;
using AlertApiException = Vela.Worker.Services.AlertApiException;

namespace Vela.Worker;

/// <summary>
/// Polls the Xtrades API for new alerts on a regular interval and processes them
/// through the normalization, classification, and risk evaluation pipeline.
/// Exit alerts (STC/BTC) are separated from the entry pipeline and routed directly
/// to HandleExitAsync without risk evaluation, matching the SignalR processing path.
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

            var alerts = await _client.GetAlertsAsync(stoppingToken);

            _metrics.AlertsFetched.Add(alerts.Count);
            _logger.LogDebug("Fetched {Count} alerts from API.", alerts.Count);

            var incomingIds = alerts
                .Where(a => a.Id is not null)
                .Select(a => a.Id!)
                .ToList();

            var existingIds = await repository.GetExistingAlertIdsAsync(incomingIds, stoppingToken);

            var newAlerts = alerts
                .Where(a => a.Id is not null && !existingIds.Contains(a.Id!))
                .ToList();

            _metrics.AlertsNew.Add(newAlerts.Count);
            _logger.LogDebug("New alerts after deduplication: {New} / {Total}", newAlerts.Count, alerts.Count);

            if (newAlerts.Count == 0)
                return;

            // Separate exit alerts before the entry pipeline. STC/BTC alerts don't need risk
            // evaluation or the IsProcessable gate, they only need to match symbol and trader
            // to an open position via HandleExitAsync. Processing them here ensures they are
            // never filtered by normalizer checks that apply to entry alerts.
            //
            // IMPORTANT: this path only fires if GetAlertsAsync returns STC/BTC type alerts
            // from the Xtrades REST endpoint. If exit alerts are not appearing in logs, review
            // IAlertApiClient to verify the API query includes exit alert types. The confirmed
            // bug (Jun 23 2026) is that Fibonaccizer's SPY STC arrived during a 37-min SignalR
            // gap and was never received by the REST poller, root cause under investigation.
            var exitAlerts  = newAlerts.Where(a => a.Side?.ToLower() is "stc" or "btc").ToList();
            var entryAlerts = newAlerts.Where(a => a.Side?.ToLower() is not ("stc" or "btc")).ToList();

            // Process exits
            var exitEntities = new List<AlertEntity>();
            foreach (var rawExit in exitAlerts)
            {
                try
                {
                    var normalized = _normalizer.Normalize(rawExit);
                    var riskResult = _riskEngine.Evaluate(normalized);
                    exitEntities.Add(AlertMapper.ToEntity(normalized, riskResult));

                    _logger.LogInformation(
                        "REST exit alert [{Side}] {Symbol} by {Trader} — routing to HandleExitAsync",
                        rawExit.Side, rawExit.Symbol, rawExit.UserName);

                    await _execution.HandleExitAsync(normalized, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "REST exit alert processing failed for {Symbol}.", rawExit.Symbol);
                }
            }

            // Process entries through full pipeline
            var processed = entryAlerts
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
                _logger.LogInformation("Pipeline complete. Approved: {Approved}, Rejected: {Rejected}",
                    approved.Count, rejected.Count);
            else
                _logger.LogDebug("Pipeline complete. Approved: 0, Rejected: {Rejected}", rejected.Count);

            foreach (var (alert, classification, _) in approved)
            {
                _logger.LogInformation("APPROVED [{Category}] {Symbol} by {Trader} (xScore: {XScore})",
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

            // Persist both exits and entries for deduplication on future poll cycles
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
}