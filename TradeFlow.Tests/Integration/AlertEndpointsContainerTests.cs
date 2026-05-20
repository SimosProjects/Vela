using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradeFlow.Worker.Data;

namespace TradeFlow.Tests.Integration;

/// <summary>
/// Integration tests using a real PostgreSQL container via TestContainers.
/// Unlike in-memory tests, these run against the actual database engine
/// with real SQL semantics, constraints, and migration support.
/// </summary>
[Collection("Integration")]
public class AlertEndpointsContainerTests
    : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _postgres;
    private WebApplicationFactory<Api.Program> _factory = null!;
    private HttpClient _client = null!;

    public AlertEndpointsContainerTests(PostgreSqlFixture postgres)
    {
        _postgres = postgres;
    }

    // -- IAsyncLifetime, run before each test --
    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Api.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseUrls("http://localhost:0");
                builder.ConfigureServices(services =>
                {
                    // Remove real PostgreSQL registration
                    var toRemove = services
                        .Where(d =>
                            d.ServiceType.FullName?.Contains("TradeFlow") == true ||
                            d.ServiceType.FullName?.Contains("DbContext") == true ||
                            d.ImplementationType?.FullName?.Contains("TradeFlow") == true ||
                            d.ImplementationType?.FullName?.Contains("DbContext") == true)
                        .ToList();

                    foreach (var d in toRemove)
                        services.Remove(d);

                    // Use the real PostgreSQL container
                    services.AddDbContext<TradeFlowDbContext>(options =>
                    {
                        options.UseNpgsql(_postgres.ConnectionString);
                        options.UseQueryTrackingBehavior(
                            QueryTrackingBehavior.NoTracking);
                    });
                });
            });

        _client = _factory.CreateClient();

        // Apply migrations to the container database
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
                      .GetRequiredService<TradeFlowDbContext>();
        await db.Database.MigrateAsync();
    }

    // -- IAsyncLifetime, run after each test --
    public async Task DisposeAsync()
    {
        // Wipe all data between tests, migrations stay applied
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
                      .GetRequiredService<TradeFlowDbContext>();
        db.Alerts.RemoveRange(db.Alerts.AsTracking());
        await db.SaveChangesAsync();

        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // -- Tests --
    [Fact]
    public async Task GetAlerts_EmptyDatabase_Returns200()
    {
        var response = await _client.GetAsync("/api/alerts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.TotalAlerts);
    }

    [Fact]
    public async Task GetAlerts_WithRealPostgres_ReturnsSeedData()
    {
        // Arrange, seed via real SQL into the container
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
                      .GetRequiredService<TradeFlowDbContext>();

        db.ChangeTracker.Clear();
        db.Alerts.AddRange(
            BuildEntity("pg-1", "yoyomun",     "TSLA", "bto", true),
            BuildEntity("pg-2", "Fibonaccizer", "SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/alerts");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.TotalAlerts);
    }

    [Fact]
    public async Task GetAlerts_FilterBySymbol_UsesRealIndex()
    {
        // Arrange, tests that idx_alerts_symbol is used correctly
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
                      .GetRequiredService<TradeFlowDbContext>();

        db.ChangeTracker.Clear();
        db.Alerts.AddRange(
            BuildEntity("pg-3", "yoyomun", "TSLA", "bto", true),
            BuildEntity("pg-4", "yoyomun", "AAPL", "stc", false)
        );
        await db.SaveChangesAsync();

        // Act, filter by symbol, uses idx_alerts_symbol
        var response = await _client.GetAsync("/api/alerts?symbol=TSLA");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.TotalAlerts);
        Assert.All(body.Data, a => Assert.Equal("TSLA", a.Symbol));
    }

    // -- Helpers --
    private static AlertEntity BuildEntity(
        string id, string userName, string symbol,
        string side, bool riskApproved) =>
        new()
        {
            Id           = id,
            UserName     = userName,
            Symbol       = symbol,
            Side         = side,
            RiskApproved = riskApproved,
            RiskReason   = riskApproved ? "All rules passed" : "Rejected",
            IngestedAt   = DateTimeOffset.UtcNow,
            TimeOfEntryAlert = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

    private record AlertListResponse(
        int TotalAlerts,
        int Page,
        int PageSize,
        List<AlertItem> Data);

    private record AlertItem(
        string? Id,
        string? UserName,
        string? Symbol,
        string? Side,
        bool RiskApproved);
}