using TradeFlow.Worker;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Metrics;
using TradeFlow.Worker.Models;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((services, config) =>
    config.ReadFrom.Configuration(builder.Configuration)
          .Enrich.FromLogContext());

// Configuration
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
    ?? throw new InvalidOperationException("XTRADES_TOKEN environment variable is not set.");

builder.Services.Configure<IbkrOptions>(
    builder.Configuration.GetSection("Ibkr"));

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
    // NoTracking improves read performance, opt in with AsTracking() only when saving
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

// Named HTTP client for SignalRListenerService negotiate calls
builder.Services.AddHttpClient("SignalR", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();
builder.Services.AddSingleton<AlertMetrics>();

// Risk engine rules composed from options at startup.
// BlockedSymbolsRule is inserted first so blocked symbols short-circuit before any other evaluation.
builder.Services.AddSingleton<RiskEngineService>(sp =>
{
    var riskOptions = sp.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    var rules = new List<IRiskRule>
    {
        new EntryOnlyRule(),
        new ApprovedOrHighScoreRule(riskOptions.ApprovedTraders, riskOptions.MinXScore),
    };

    if (!riskOptions.AllowLotto)
        rules.Insert(1, new NoLottoRule());

    if (!riskOptions.AllowHigh)
        rules.Insert(1, new NoHighRiskRule());

    if (riskOptions.MinStockPriceDollars > 0)
        rules.Insert(1, new MinStockPriceRule(riskOptions.MinStockPriceDollars));

    // Block same-day expiry entries after the configured cutoff hour
    rules.Insert(1, new No0DTEAfterCutoffRule(riskOptions.ZeroDteEntryCutoffHour));

    // Blocked symbols inserted at position 0 so they short-circuit before all other rules
    if (riskOptions.BlockedSymbols.Count > 0)
        rules.Insert(0, new BlockedSymbolsRule(riskOptions.BlockedSymbols));

    return new RiskEngineService(rules);
});

builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IOpenPositionRepository, OpenPositionRepository>();
builder.Services.AddScoped<ITradeMetricsRepository, TradeMetricsRepository>();
builder.Services.AddSingleton<PositionSizer>();
builder.Services.AddSingleton<TradeGuard>();
builder.Services.AddSingleton<CsvTradeLogger>();
builder.Services.AddSingleton<BrokerExecutionService>();
builder.Services.AddHostedService<MarketSchedulerService>();

builder.Services.AddSingleton<IbkrConnectionService>();
builder.Services.AddSingleton<IbkrBrokerService>();

// Switch between paper/live trading and simulation via environment variable.
// Set IBKR_ENABLED=true in .env to activate IbkrBrokerService.
// Defaults to NullBrokerService when not set or set to anything else.
if (Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true")
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
if (Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true")
{
    var connection = host.Services.GetRequiredService<IbkrConnectionService>();
    connection.Connect();

    // Wait for nextValidId callback from Gateway, confirms session is fully initialized.
    // Timeout of 15s as fallback in case Gateway is slow to respond.
    await connection.WaitForNextValidIdAsync(TimeSpan.FromSeconds(15));

    var broker = host.Services.GetRequiredService<IbkrBrokerService>();
    broker.SyncOrderId();

    var guard = host.Services.GetRequiredService<TradeGuard>();
    guard.StartCacheRefresh(CancellationToken.None);
}

// Reload open positions and daily trade count from DB into TradeGuard on restart
using (var scope = host.Services.CreateScope())
{
    var repo    = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
    var metrics = scope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
    var guard   = host.Services.GetRequiredService<TradeGuard>();

    var positions = await repo.GetAllAsync();
    if (positions.Count > 0)
    {
        guard.LoadFromDatabase(positions);

        // Re-register stop/target callbacks for restored positions so broker-side
        // trail stop and target fills are detected correctly after a restart
        if (Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true")
        {
            var broker = host.Services.GetRequiredService<IbkrBrokerService>();
            broker.ReRegisterStopCallbacks(positions);
        }
    }

    // Seed daily trade count from trade_metrics so restarts within the same
    // trading day don't reset the counter and allow more than the daily limit
    var todayEt    = TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;
    var todayCount = await metrics.GetTodayTradeCountAsync(DateOnly.FromDateTime(todayEt));
    if (todayCount > 0)
        guard.SeedDailyCount(todayCount);
}

host.Run();