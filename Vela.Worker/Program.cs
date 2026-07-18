using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Vela.Worker;
using Vela.Worker.Engine;
using Vela.Worker.Metrics;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);

// Read connection string before Serilog setup so WorkerLogSink can capture it via closure.
var connectionString = builder.Configuration.GetConnectionString("Vela")
    ?? throw new InvalidOperationException("Vela connection string is not configured.");

builder.Services.AddSerilog((_, config) =>
    config.ReadFrom.Configuration(builder.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Sink(new WorkerLogSink(connectionString)));

// Configuration
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
    ?? throw new InvalidOperationException("XTRADES_TOKEN environment variable is not set.");

var ibkrEnabled = Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true";

builder.Services
    .AddOptions<IbkrOptions>()
    .Bind(builder.Configuration.GetSection("Ibkr"))
    .ValidateDataAnnotations()
    .Validate(
        o => !ibkrEnabled || !string.IsNullOrWhiteSpace(o.AccountId),
        "Ibkr:AccountId must be set when IBKR_ENABLED=true.")
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

// Cross-field invariants that DataAnnotations cannot express, enforced at startup.
builder.Services.AddSingleton<IValidateOptions<RiskEngineOptions>, RiskEngineOptionsValidator>();

builder.Services
    .AddOptions<PollingOptions>()
    .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Database
builder.Services.AddDbContext<VelaDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
},
ServiceLifetime.Scoped);

// HTTP client for Xtrades API with resilience policies
builder.Services.AddHttpClient<IAlertApiClient, Vela.Worker.Services.AlertApiClient>(client =>
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

builder.Services.AddHttpClient("MarketConditions", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
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
        new NoHighRiskRule(() => regime.BlockHigh),
        new NoLottoRule(() => regime.BlockLotto),
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
builder.Services.AddHostedService<SpyglassAlertConsumerService>();

// Consumes dashboard force-close requests from force_close_requests and runs them through
// BrokerExecutionService. Lives in the Worker because the single IBKR session is here.
builder.Services.AddHostedService<ForceCloseConsumerService>();

// Periodic position reconciliation, IBKR only, runs every 30 min during market hours
if (ibkrEnabled)
    builder.Services.AddHostedService<PeriodicReconciliationService>();

var host = builder.Build();

// Clear worker_logs so the dashboard log panel starts fresh each session.
// Runs synchronously before the host starts so no race with the dashboard poll.
try
{
    await using var logConn = new NpgsqlConnection(connectionString);
    await logConn.OpenAsync();
    await using var logCmd = new NpgsqlCommand("DELETE FROM worker_logs", logConn);
    await logCmd.ExecuteNonQueryAsync();
}
catch { /* table absent on very first run; WorkerLogSink creates it */ }

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
    var db    = scope.ServiceProvider.GetRequiredService<VelaDbContext>();
    var guard = host.Services.GetRequiredService<TradeGuard>();

    var positions = await repo.GetAllAsync();

    // Manual positions are tracking-only, they must not enter TradeGuard.
    // BrokerExecutionService and PositionMonitorService only manage positions in TradeGuard,
    // so manual positions are never accidentally acted on.
    var managedPositions = positions.Where(p => !p.IsManual).ToList();

    if (managedPositions.Count > 0)
    {
        var orderIds = managedPositions
            .Select(p => p.OrderId)
            .ToHashSet();

        var xScores = await db.TradeMetrics
            .Where(t => orderIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.XScore ?? 0m);

        guard.LoadFromDatabase(managedPositions, xScores);

        // Re-register stop/target callbacks for restored positions so broker-side
        // trail stop and target fills are detected correctly after a restart
        if (ibkrEnabled)
        {
            var broker = host.Services.GetRequiredService<IbkrBrokerService>();
            await broker.ReRegisterStopCallbacksAsync(managedPositions);
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

// Patch trader lists from appsettings into risk_config_overrides so the Api can serve them from DB
{
    var riskOpts = host.Services.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    using var traderScope = host.Services.CreateScope();
    var traderDb = traderScope.ServiceProvider.GetRequiredService<VelaDbContext>();
    var existing = await traderDb.RiskConfigOverrides.FirstOrDefaultAsync(r => r.Id == 1);

    var config = existing?.ConfigJson is not null
        ? JsonNode.Parse(existing.ConfigJson)?.AsObject() ?? new JsonObject()
        : new JsonObject();

    config["approvedTraders"] = JsonSerializer.SerializeToNode(
        riskOpts.ApprovedTraders ?? []);

    config["restrictedTraders"] = JsonSerializer.SerializeToNode(
        (riskOpts.RestrictedTraders ?? new Dictionary<string, int>())
            .Select(kvp => new { name = kvp.Key, allotmentPct = kvp.Value })
            .ToList());

    var json = config.ToJsonString();
    var now  = DateTimeOffset.UtcNow;

    await traderDb.Database.ExecuteSqlAsync(
        $"""
        INSERT INTO risk_config_overrides (id, config_json, updated_at)
        VALUES (1, {json}, {now})
        ON CONFLICT (id) DO UPDATE
          SET config_json = EXCLUDED.config_json,
              updated_at  = EXCLUDED.updated_at
        """,
        CancellationToken.None);
}

// Wire cross-service state propagation at the composition root.
// SystemStateService fires events when DB values change; subscribers react
// without either service holding a direct reference to the other.
var systemState = host.Services.GetRequiredService<SystemStateService>();
var execution = host.Services.GetRequiredService<BrokerExecutionService>();
var regime = host.Services.GetRequiredService<MarketRegimeService>();
systemState.PauseStateChanged += isPaused => execution.IsPaused = isPaused;
systemState.BlockCallsOverrideChanged += regime.SetBlockCallsOverride;
systemState.BlockHighOverrideChanged  += blockHigh  => regime.SetBlockHighOverride(blockHigh);
systemState.BlockLottoOverrideChanged += blockLotto => regime.SetBlockLottoOverride(blockLotto);

host.Run();