using IBApi;

namespace TradeFlow.Worker.Services;

// Manages the socket connection to IB Gateway.
// The IBApi EClient is not thread-safe so all calls must go through this service.
// Singleton so only one connection shared across the application.
public class IbkrConnectionService : IDisposable
{
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrConnectionService> _logger;
    private readonly EClientSocket _client;
    private readonly IbkrEWrapper _wrapper;
    private readonly EReaderSignal _signal;
    private readonly DiscordNotificationService _discord;
    private bool _connected = false;
    private CancellationTokenSource? _keepaliveCts;

    public bool IsConnected => _connected && _client.IsConnected();

    public IbkrConnectionService(
        IOptions<IbkrOptions> options,
        ILogger<IbkrConnectionService> logger,
        ILogger<IbkrEWrapper> wrapperLogger,
        DiscordNotificationService discord)
    {
        _options = options.Value;
        _logger  = logger;
        _wrapper = new IbkrEWrapper(wrapperLogger);
        _signal  = new EReaderMonitorSignal();
        _client  = new EClientSocket(_wrapper, _signal);
        _discord = discord;

        // When Gateway drops the connection, update _connected so IsConnected
        // reflects the real state immediately without waiting for the next API call
        _wrapper.SetConnectionClosedCallback(() =>
        {
            _connected = false;
            _logger.LogWarning("IB Gateway connection closed unexpectedly.");
            _ = _discord.NotifyCriticalAsync(
                "🔴 IB Gateway Disconnected",
                "TradeFlow lost connection to IB Gateway. Orders cannot be placed until reconnected.");
        });
    }

    /// <summary>
    /// Connects to IB Gateway via the TWS socket API. Must be called before any broker API calls.
    /// Starts the EReader message processing thread on successful connection.
    /// Also starts a keepalive loop that pings Gateway every 60 seconds to prevent
    /// idle disconnection during quiet market periods.
    /// </summary>
    /// <returns>True if connected successfully, false otherwise.</returns>
    public bool Connect()
    {
        if (IsConnected)
            return true;

        try
        {
            _client.eConnect(_options.Host, _options.Port, _options.ClientId);

            // Start the reader thread, processes incoming messages from IB Gateway
            var reader = new EReader(_client, _signal);
            reader.Start();

            // Background thread that processes messages as they arrive.
            // The try/catch inside the loop is critical. Without it, a single bad packet
            // from an illiquid OTC stock (EndOfStreamException) kills this thread silently,
            // leaving Gateway in a zombie state that eventually crashes the process entirely.
            var readerThread = new Thread(() =>
            {
                while (_client.IsConnected())
                {
                    try
                    {
                        _signal.waitForSignal();
                        reader.processMsgs();
                    }
                    catch (Exception ex)
                    {
                        // Log and continue, one bad packet must not kill the reader thread.
                        // If the connection is genuinely lost, _client.IsConnected() will
                        // return false on the next loop iteration and exit cleanly.
                        _logger.LogError(ex,
                            "EReader thread caught exception while processing messages. " +
                            "Continuing — connection state: {Connected}", _client.IsConnected());
                    }
                }

                _logger.LogWarning("EReader thread exiting — client no longer connected.");
            })
            {
                IsBackground = true,
                Name = "IbkrReaderThread"
            };
            readerThread.Start();

            // Give the connection a moment to establish before checking state
            Thread.Sleep(1000);

            _connected = _client.IsConnected();

            if (_connected)
            {
                _logger.LogInformation(
                    "Connected to IB Gateway at {Host}:{Port} | ClientId: {ClientId}",
                    _options.Host, _options.Port, _options.ClientId);

                _ = _discord.NotifyCriticalAsync(
                    "🟢 IB Gateway Connected",
                    $"TradeFlow connected to IB Gateway at {_options.Host}:{_options.Port}");

                StartKeepalive();
            }
            else
            {
                _logger.LogError(
                    "Failed to connect to IB Gateway at {Host}:{Port}",
                    _options.Host, _options.Port);
            }

            return _connected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception connecting to IB Gateway");
            return false;
        }
    }

    /// <summary>
    /// Waits for Gateway to send the nextValidId callback, confirming the session is fully
    /// initialized and ready for order placement. Falls back after the given timeout.
    /// </summary>
    public async Task WaitForNextValidIdAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _wrapper.WaitForNextValidIdAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Timed out waiting for nextValidId from Gateway after {Timeout}s — proceeding anyway.",
                timeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Disconnects from IB Gateway and resets the connection state.
    /// </summary>
    public void Disconnect()
    {
        StopKeepalive();

        if (_client.IsConnected())
        {
            _client.eDisconnect();
            _connected = false;
            _logger.LogInformation("Disconnected from IB Gateway.");
        }
    }

    // Expose the client for API calls in IbkrBrokerService
    public EClientSocket Client => _client;

    // Expose the wrapper for subscribing to callbacks
    public IbkrEWrapper Wrapper => _wrapper;

    public void Dispose()
    {
        Disconnect();
        _client.Close();
    }

    // Sends reqCurrentTime() every 60 seconds to keep the socket alive during
    // quiet periods when no alerts are qualifying for broker execution.
    // Gateway will drop idle connections and this prevents that without any
    // meaningful API overhead. currentTime() callback is a no-op in IbkrEWrapper.
    private void StartKeepalive()
    {
        StopKeepalive();

        _keepaliveCts = new CancellationTokenSource();
        var ct = _keepaliveCts.Token;

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("IB Gateway keepalive started (60s interval).");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);

                    if (IsConnected)
                    {
                        _client.reqCurrentTime();
                        _logger.LogDebug("IB Gateway keepalive ping sent.");
                    }
                    else
                    {
                        _logger.LogDebug("IB Gateway keepalive skipped — not connected.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a keepalive failure crash the loop
                    _logger.LogWarning(ex, "IB Gateway keepalive ping failed.");
                }
            }

            _logger.LogInformation("IB Gateway keepalive stopped.");
        }, ct);
    }

    private void StopKeepalive()
    {
        if (_keepaliveCts is not null)
        {
            _keepaliveCts.Cancel();
            _keepaliveCts.Dispose();
            _keepaliveCts = null;
        }
    }
}