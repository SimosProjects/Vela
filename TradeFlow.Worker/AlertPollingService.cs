using System.Diagnostics;
using TradeFlow.Worker.Metrics;

namespace TradeFlow.Worker;

/// <summary>
/// Polls the Xtrades API for new alerts on a regular interval and processes them
/// through the normalization, classification, and risk evaluation pipeline.
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
            // Scoped per cycle so the DbContext is never shared across poll cycles
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

            var alerts = await _client.GetAlertsAsync(stoppingToken);

            _metrics.AlertsFetched.Add(alerts.Count);
            _logger.LogDebug("Fetched {Count} alerts from API.", alerts.Count);

            // Single batch query then filter in memory, more efficient than one query per alert
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

            var processed = newAlerts
                .Where(_normalizer.IsProcessable)
                .Select(_normalizer.Normalize)
                .Select(alerts => (
                    Alert: alerts,
                    Classification: AlertClassifier.Classify(alerts),
                    RiskResult: _riskEngine.Evaluate(alerts)
                ))
                .ToList();

            var approved = processed.Where(p => p.RiskResult.Approved).ToList();
            var rejected = processed.Where(p => !p.RiskResult.Approved).ToList();

            _metrics.AlertsApproved.Add(approved.Count);
            _metrics.AlertsRejected.Add(rejected.Count);

            // Only log pipeline summary when there is something approved, otherwise too noisy
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

            // Exits are processed from all alerts, not just approved ones
            foreach (var (alert, _, _) in processed)
            {
                if (alert.Side?.ToLower() is "stc" or "btc")
                    await _execution.HandleExitAsync(alert, stoppingToken);
            }

            sw.Stop();
            _metrics.PollDurationMs.Record(sw.ElapsedMilliseconds,
                new TagList { { "result", "success" } });

            // Both approved and rejected are persisted so we can audit risk decisions later
            var entities = processed.Select(p => AlertMapper.ToEntity(p.Alert, p.RiskResult)).ToList();
            await repository.SaveManyAsync(entities, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
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