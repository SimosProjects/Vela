using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;
using System.Threading.Channels;

namespace TradeFlow.Worker;

/// <summary>
/// Connects to the Xtrades Azure SignalR feed and receives live trading alerts in real time.
/// Uses a two-step connection flow: POST to negotiate for a short-lived token, then connect
/// to Azure SignalR using that token. Decouples the SignalR callback from the processing
/// pipeline using a bounded Channel to handle burst traffic without blocking the connection.
/// </summary>
public class SignalRListenerService : BackgroundService
{
    private const string NegotiateUrl = "https://app.xtrades.net/api/v2/signalr/negotiate";
    private const string AlertEventName = "newAlert";
    private const string HubName = "notification";
    private const int ChannelCapacity = 500;

    private readonly IAlertNormalizer _normalizer;
    private readonly RiskEngineService _riskEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalRListenerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _token;
    private readonly DiscordNotificationService _discord;
    private readonly BrokerExecutionService _execution;

    // DropOldest prevents the SignalR callback from blocking on alert bursts.
    // A warning is logged when the channel is at capacity so bursts are visible in logs.
    private readonly Channel<JsonElement> _alertChannel =
        Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public SignalRListenerService(
        IAlertNormalizer normalizer,
        RiskEngineService riskEngine,
        IServiceScopeFactory scopeFactory,
        ILogger<SignalRListenerService> logger,
        DiscordNotificationService discord,
        BrokerExecutionService execution,
        IHttpClientFactory httpClientFactory)
    {
        _normalizer = normalizer;
        _riskEngine = riskEngine;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _discord = discord;
        _execution = execution;
        _httpClientFactory = httpClientFactory;

        _token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
            ?? throw new InvalidOperationException(
                "XTRADES_TOKEN environment variable is not set.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR listener service started.");

        await Task.WhenAll(
            RunConnectionLoopAsync(stoppingToken),
            RunProcessingLoopAsync(stoppingToken));

        _logger.LogInformation("SignalR listener service stopped.");
    }

    // Negotiates with Xtrades then connects to Azure SignalR. Reconnects with exponential backoff on failure.
    private async Task RunConnectionLoopAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            HubConnection? connection = null;
            try
            {
                var (hubUrl, signalRToken) = await NegotiateAsync(stoppingToken);

                connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.AccessTokenProvider =
                            () => Task.FromResult<string?>(signalRToken);
                    })
                    .WithAutomaticReconnect(new ExponentialBackoffRetryPolicy())
                    .ConfigureLogging(logging =>
                        logging.SetMinimumLevel(LogLevel.Warning))
                    .Build();

                connection.On<JsonElement>(AlertEventName, async alert =>
                {
                    _logger.LogDebug("SignalR newAlert received");
                    try
                    {
                        // Warn if channel is near capacity, indicates processing is falling behind
                        if (_alertChannel.Reader.Count >= ChannelCapacity - 1)
                            _logger.LogWarning(
                                "SignalR alert channel at capacity ({Count}/{Max}) — oldest alert will be dropped.",
                                _alertChannel.Reader.Count, ChannelCapacity);

                        await _alertChannel.Writer.WriteAsync(alert, stoppingToken);
                    }
                    catch (OperationCanceledException) { }
                });

                connection.Reconnecting += ex =>
                {
                    _logger.LogWarning("SignalR reconnecting. Reason: {Reason}", ex?.Message);
                    return Task.CompletedTask;
                };

                connection.Reconnected += _ =>
                {
                    attempt = 0;
                    _logger.LogInformation("SignalR reconnected.");
                    return Task.CompletedTask;
                };

                connection.Closed += ex =>
                {
                    _logger.LogWarning("SignalR closed. Reason: {Reason}", ex?.Message ?? "clean close");
                    return Task.CompletedTask;
                };

                await connection.StartAsync(stoppingToken);
                attempt = 0;

                _logger.LogInformation(
                    "SignalR connected. Hub: {Hub}, ConnectionId: {Id}",
                    HubName, connection.ConnectionId);

                while (connection.State != HubConnectionState.Disconnected
                       && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SignalR connection failed. Will retry with backoff.");
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync();
            }

            if (stoppingToken.IsCancellationRequested) break;

            await RetryWithBackoffAsync(attempt++, stoppingToken);
        }

        _alertChannel.Writer.Complete();
    }

    // Calls the Xtrades negotiate endpoint to get a short-lived Azure SignalR connection URL and access token
    private async Task<(string HubUrl, string Token)> NegotiateAsync(
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("SignalR");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);

        var response = await client.PostAsync(NegotiateUrl, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"SignalR negotiate failed: HTTP {(int)response.StatusCode} {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var url = root.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Negotiate response missing 'url'");
        var token = root.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("Negotiate response missing 'accessToken'");

        _logger.LogInformation("SignalR negotiate succeeded.");

        return (url, token);
    }

    // Reads alerts from the channel and processes them one by one
    private async Task RunProcessingLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var alertElement in _alertChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAlertAsync(alertElement, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process SignalR alert. Continuing.");
            }
        }
    }

    // Deserializes and processes a newAlert event through the full pipeline:
    // normalize, classify, risk evaluate, deduplicate, persist
    private async Task ProcessAlertAsync(
        JsonElement alertElement,
        CancellationToken stoppingToken)
    {
        _logger.LogDebug("Processing SignalR alert: {Raw}", alertElement.GetRawText());

        // Payload arrives as a JSON array, take the first element
        var element = alertElement.ValueKind == JsonValueKind.Array
            ? alertElement[0]
            : alertElement;

        var alert = JsonSerializer.Deserialize<Alert>(
            element.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (alert is null)
        {
            _logger.LogWarning("SignalR alert deserialized to null. Skipping.");
            return;
        }

        if (!_normalizer.IsProcessable(alert))
        {
            _logger.LogDebug("SignalR alert not processable (missing required fields). Skipping.");
            return;
        }

        var normalized = _normalizer.Normalize(alert);
        var classification = AlertClassifier.Classify(normalized);
        var riskResult = _riskEngine.Evaluate(normalized);

        // Side rejections (stc/btc) are expected, exits are not entries.
        // Only log at Information for genuine risk failures worth reviewing.
        var isSideRejection = !riskResult.Approved &&
            (riskResult.Reason?.Contains("stc", StringComparison.OrdinalIgnoreCase) == true ||
             riskResult.Reason?.Contains("btc", StringComparison.OrdinalIgnoreCase) == true ||
             riskResult.Reason?.Contains("BTO entry", StringComparison.OrdinalIgnoreCase) == true);

        if (isSideRejection)
        {
            _logger.LogDebug(
                "SignalR alert [{Category}] {Symbol} by {Trader} REJECTED: {Reason}",
                classification.Category, normalized.Symbol, normalized.UserName, riskResult.Reason);
        }
        else
        {
            _logger.LogInformation(
                "SignalR alert [{Category}] {Symbol} by {Trader} {Result}",
                classification.Category,
                normalized.Symbol,
                normalized.UserName,
                riskResult.Approved ? "APPROVED" : $"REJECTED: {riskResult.Reason}");
        }

        if (riskResult.Approved)
        {
            await _discord.NotifyApprovedAlertAsync(normalized, classification, stoppingToken);

            if (normalized.Side?.ToLower() is "bto")
                await _execution.HandleEntryAsync(normalized, classification, isAverage: false, stoppingToken);
            else if (normalized.Side?.ToLower() is "avg")
                await _execution.HandleEntryAsync(normalized, classification, isAverage: true, stoppingToken);
        }

        // Exits are processed regardless of risk approval
        if (normalized.Side?.ToLower() is "stc" or "btc")
            await _execution.HandleExitAsync(normalized, stoppingToken);

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var entity = AlertMapper.ToEntity(normalized, riskResult);

        var existingIds = await repository.GetExistingAlertIdsAsync([entity.Id], stoppingToken);

        if (existingIds.Contains(entity.Id))
        {
            _logger.LogDebug("SignalR alert {Id} already exists. Skipping.", entity.Id);
            return;
        }

        await repository.SaveManyAsync([entity], stoppingToken);
    }

    private async Task RetryWithBackoffAsync(int attempt, CancellationToken stoppingToken)
    {
        var maxDelay = TimeSpan.FromSeconds(60);
        var baseSeconds = Math.Pow(2, Math.Min(attempt, 6));
        var jitter = Random.Shared.NextDouble() * 0.3;
        var delay = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        if (delay > maxDelay) delay = maxDelay;

        _logger.LogInformation("Waiting {Delay:F1}s before reconnecting.", delay.TotalSeconds);

        await Task.Delay(delay, stoppingToken);
    }
}

/// <summary>
/// Exponential backoff with jitter for SignalR automatic reconnect.
/// Used for transient drops — the outer loop handles complete connection failures.
/// </summary>
public class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount >= 5)
            return null;

        var baseSeconds = Math.Pow(2, retryContext.PreviousRetryCount);
        var jitter = Random.Shared.NextDouble() * 0.3;
        var delay = TimeSpan.FromSeconds(baseSeconds * (1 + jitter));

        return delay < MaxDelay ? delay : MaxDelay;
    }
}