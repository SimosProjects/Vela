using TradeFlow.Worker;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Metrics;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((services, config) =>
    config.ReadFrom.Configuration(builder.Configuration)
          .Enrich.FromLogContext());

// Configuration
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
    ?? throw new InvalidOperationException("XTRADES_TOKEN environment variable is not set.");

var ibkrEnabled = Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true";

builder.Services
    .AddOptions<IbkrOptions>()
    .Bind(builder.Configuration.GetSection("Ibkr"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<XtradesOptions>()
    .Bind(builder.Configuration.GetSection(XtradesOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<RiskEngineOptions>()
    .Bind(builder.Configuration.GetSection(RiskEngineOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<PollingOptions>()
    .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Database
var connectionString = builder.Configuration.GetConnectionString("TradeFlow")
    ?? throw new InvalidOperationException("TradeFlow connection string is not configured.");

builder.Services.AddDbContext<TradeFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
},
ServiceLifetime.Scoped);

// HTTP client for Xtrades API with resilience policies
builder.Services.AddHttpClient<IAlertApiClient, TradeFlow.Worker.Services.AlertApiClient>(client =>
{
    client.BaseAddress = new Uri("https://app.xtrades.net");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", token);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(100);
})
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
    options.Retry.MaxRetryAttempts = 3;
});

builder.Services.AddHttpClient("SignalR", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("Scheduler", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();
builder.Services.AddSingleton<AlertMetrics>();
builder.Services.AddSingleton<MarketRegimeService>();

// Risk engine rules composed from options at startup.
builder.Services.AddSingleton(sp =>
{
    var riskOptions = sp.GetRequiredService<IOptions<RiskEngineOptions>>().Value;
    var regime      = sp.GetRequiredService<MarketRegimeService>();

    var rules = new List<IRiskRule>
    {
        new EntryOnlyRule(),
        new ApprovedOrHighScoreRule(riskOptions.ApprovedTraders, riskOptions.MinXScore),
        new NoHighRiskRule(!riskOptions.AllowHigh, () => regime.IsChoppy, () => regime.ChopScore),
        new NoLottoRule(!riskOptions.AllowLotto,   () => regime.IsChoppy, () => regime.ChopScore),
    };

    if (riskOptions.RegimeBearishBlockCalls)
        rules.Add(new BearishCallBlockRule(() => regime.BlockCalls));

    if (riskOptions.MinStockPriceDollars > 0)
        rules.Insert(1, new MinStockPriceRule(riskOptions.MinStockPriceDollars));

    rules.Insert(1, new No0DTEAfterCutoffRule(riskOptions.ZeroDteEntryCutoffHour));

    if (riskOptions.BlockedSymbols.Count > 0)
        rules.Insert(0, new BlockedSymbolsRule(riskOptions.BlockedSymbols));

    return new RiskEngineService(rules);
});

builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IOpenPositionRepository, OpenPositionRepository>();
builder.Services.AddScoped<ITradeMetricsRepository, TradeMetricsRepository>();
builder.Services.AddSingleton(sp =>
    new PositionSizer(
        sp.GetRequiredService<IOptions<RiskEngineOptions>>(),
        sp.GetRequiredService<ILogger<PositionSizer>>(),
        sp.GetRequiredService<MarketRegimeService>()));
builder.Services.AddSingleton<TradeGuard>();
builder.Services.AddSingleton<CsvTradeLogger>();
builder.Services.AddSingleton<BrokerExecutionService>();
builder.Services.AddSingleton<MarketConditionsLogger>();
builder.Services.AddHostedService<MarketSchedulerService>();

builder.Services.AddSingleton<IbkrConnectionService>();
builder.Services.AddSingleton<IbkrBrokerService>();

builder.Services.AddSingleton<SystemStateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemStateService>());

// Switch between paper/live trading and simulation via environment variable.
// Set IBKR_ENABLED=true in .env to activate IbkrBrokerService.
// Defaults to NullBrokerService when not set or set to anything else.
if (ibkrEnabled)
    builder.Services.AddSingleton<IBrokerService>(sp =>
        sp.GetRequiredService<IbkrBrokerService>());
else
    builder.Services.AddSingleton<IBrokerService, NullBrokerService>();

// Hosted services
builder.Services.AddHostedService<PositionMonitorService>();
builder.Services.AddHostedService<AlertPollingService>();
builder.Services.AddHostedService<SignalRListenerService>();

var host = builder.Build();

// Startup sequence: connect to Gateway, wait for session ready, sync order IDs,
// then reload TradeGuard state from DB
if (ibkrEnabled)
{
    var connection = host.Services.GetRequiredService<IbkrConnectionService>();
    connection.Connect();

    // Wait for nextValidId callback from Gateway, confirms session is fully initialized.
    // Timeout of 15s as fallback in case Gateway is slow to respond.
    await connection.WaitForNextValidIdAsync(TimeSpan.FromSeconds(15));

    int maxDbOrderId;
    using (var metricsScope = host.Services.CreateScope())
    {
        var metrics  = metricsScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
        maxDbOrderId = await metrics.GetMaxOrderIdAsync();
    }

    var broker = host.Services.GetRequiredService<IbkrBrokerService>();
    broker.SyncOrderId(maxDbOrderId);

    var guard = host.Services.GetRequiredService<TradeGuard>();
    guard.StartCacheRefresh(CancellationToken.None);
}

// Reload open positions from DB into TradeGuard on restart, restoring xScore from
// trade_metrics so the value survives restarts and is written correctly on close.
using (var scope = host.Services.CreateScope())
{
    var repo  = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
    var db    = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();
    var guard = host.Services.GetRequiredService<TradeGuard>();

    var positions = await repo.GetAllAsync();
    if (positions.Count > 0)
    {
        var orderIds = positions
            .Select(p => p.OrderId)
            .ToHashSet();

        var xScores = await db.TradeMetrics
            .Where(t => orderIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.XScore ?? 0m);

        guard.LoadFromDatabase(positions, xScores);

        // Re-register stop/target callbacks for restored positions so broker-side
        // trail stop and target fills are detected correctly after a restart
        if (ibkrEnabled)
        {
            var broker = host.Services.GetRequiredService<IbkrBrokerService>();
            broker.ReRegisterStopCallbacks(positions);
        }
    }
}

// Reconcile IBKR actual state against the database before any alerts are processed.
// Cancels orphan orders, verifies DB positions against IBKR, and covers any shorts.
// Only runs when IBKR is enabled, NullBrokerService returns empty lists and skips gracefully.
if (ibkrEnabled)
{
    // Brief delay to let Gateway settle after connection and callback re-registration
    await Task.Delay(TimeSpan.FromSeconds(3));

    using var reconScope = host.Services.CreateScope();
    var broker  = host.Services.GetRequiredService<IBrokerService>();
    var repo    = reconScope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
    var guard   = host.Services.GetRequiredService<TradeGuard>();
    var discord = host.Services.GetRequiredService<DiscordNotificationService>();

    var reconciliation = new StartupReconciliationService(
        broker,
        repo,
        guard,
        discord,
        host.Services.GetRequiredService<ILogger<StartupReconciliationService>>());

    await reconciliation.RunAsync();
}

// Wire pause state propagation at the composition root.
// SystemStateService fires PauseStateChanged when is_paused flips in the database.
// BrokerExecutionService reacts without either service knowing about the other.
var systemState = host.Services.GetRequiredService<SystemStateService>();
var execution   = host.Services.GetRequiredService<BrokerExecutionService>();
systemState.PauseStateChanged += isPaused => execution.IsPaused = isPaused;

host.Run();