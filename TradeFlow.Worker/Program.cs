using TradeFlow.Worker;
using TradeFlow.Worker.Data;
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

builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();
builder.Services.AddSingleton<AlertMetrics>();

// Risk engine, rules are composed from options at startup
builder.Services.AddSingleton<RiskEngineService>(sp =>
{
    var riskOptions = sp.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    var rules = new List<IRiskRule>
    {
        new EntryOnlyRule(),
        new MinXScoreRule(riskOptions.MinXScore),
        new ApprovedTraderRule(riskOptions.ApprovedTraders)
    };

    if (!riskOptions.AllowLotto)
        rules.Insert(1, new NoLottoRule());

    // Add penny stock filter if a minimum price is configured.
    // Inserted after EntryOnlyRule so non-entry alerts skip it cheaply,
    // but before the more expensive XScore and trader lookups.
    if (riskOptions.MinStockPriceDollars > 0)
        rules.Insert(1, new MinStockPriceRule(riskOptions.MinStockPriceDollars));

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

// Startup sequence: connect to Gateway, sync order IDs, then reload TradeGuard state from DB
if (Environment.GetEnvironmentVariable("IBKR_ENABLED") == "true")
{
    var connection = host.Services.GetRequiredService<IbkrConnectionService>();
    connection.Connect();

    // Wait for nextValidId callback to fire before syncing — it is async from Gateway
    await Task.Delay(5000);

    var broker = host.Services.GetRequiredService<IbkrBrokerService>();
    broker.SyncOrderId();
}

// Reload open positions from DB into TradeGuard so exit alerts work after restart
using (var scope = host.Services.CreateScope())
{
    var repo  = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
    var guard = host.Services.GetRequiredService<TradeGuard>();
    var positions = await repo.GetAllAsync();
    if (positions.Count > 0)
        guard.LoadFromDatabase(positions);
}

host.Run();