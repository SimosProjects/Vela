AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerilog((services, config) =>
    config.ReadFrom.Configuration(builder.Configuration)
          .Enrich.FromLogContext());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("alerts", builder =>
        builder
            .Expire(TimeSpan.FromSeconds(30))
            .SetVaryByQuery("page", "pageSize", "userName", "symbol", "side", "riskApproved"));
});

var connectionString = builder.Configuration.GetConnectionString("TradeFlow")
    ?? throw new InvalidOperationException(
        "TradeFlow connection string is not configured.");

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql");

builder.Services.AddDbContext<TradeFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

builder.Services.AddScoped<IAlertRepository, AlertRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler();
}

// One structured log line per request (method, path, status, duration).
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseOutputCache();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapAlertEndpoints();
app.MapConfigEndpoints();
app.MapDashboardEndpoints();
app.MapFallbackToFile("index.html");

// Warm up the database connection pool so the first dashboard poll doesn't
// hit a cold pool and throw OperationCanceledException.
using (var warmupScope = app.Services.CreateScope())
{
    var db = warmupScope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();
    await db.Database.CanConnectAsync();
}

app.Run();

namespace TradeFlow.Api
{
    public partial class Program { }
}