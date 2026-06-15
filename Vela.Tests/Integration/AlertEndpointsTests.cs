using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Vela.Worker.Data;

namespace Vela.Tests.Integration;

public class TestApiFactory : WebApplicationFactory<Api.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseUrls("http://localhost:0");
        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d =>
                    d.ServiceType.FullName?.Contains("Vela") == true ||
                    d.ServiceType.FullName?.Contains("DbContext") == true ||
                    d.ImplementationType?.FullName?.Contains("Vela") == true ||
                    d.ImplementationType?.FullName?.Contains("DbContext") == true)
                .ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            services.AddDbContext<VelaDbContext>(options =>
                options.UseInMemoryDatabase("integration-test-alerts"));
        });
    }

    public void ResetDatabase()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();
        db.Alerts.RemoveRange(db.Alerts);
        db.SaveChanges();
    }
}

/// <summary>
/// Integration tests for the Alert API endpoints.
/// Uses WebApplicationFactory to spin up the full API pipeline
/// in memory with an in-memory database replacing PostgreSQL.
/// </summary>
[Collection("Integration")]
public class AlertEndpointsTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;

    public AlertEndpointsTests(TestApiFactory factory)
    {
        _factory = factory;
        _factory.ResetDatabase();
        _client = _factory.CreateClient();
    }

    // -- GET /api/alerts --

    [Fact]
    public async Task GetAlerts_EmptyDatabase_Returns200WithEmptyData()
    {
        var response = await _client.GetAsync("/api/alerts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.TotalAlerts);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetAlerts_WithData_ReturnsPaginatedResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-1", "yoyomun",     "TSLA", "bto", true),
            BuildEntity("id-2", "yoyomun",     "AAPL", "stc", false),
            BuildEntity("id-3", "Fibonaccizer","SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(3, body.TotalAlerts);
        Assert.Equal(3, body.Data.Count);
    }

    [Fact]
    public async Task GetAlerts_FilterByUserName_ReturnsFilteredResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-4", "yoyomun",     "TSLA", "bto", true),
            BuildEntity("id-5", "Fibonaccizer","SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?userName=yoyomun");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.All(body.Data, a => Assert.Equal("yoyomun", a.UserName));
    }

    [Fact]
    public async Task GetAlerts_FilterBySymbol_ReturnsFilteredResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-6", "yoyomun", "TSLA", "bto", true),
            BuildEntity("id-7", "yoyomun", "AAPL", "bto", true),
            BuildEntity("id-8", "yoyomun", "TSLA", "stc", false)
        );
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?symbol=TSLA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.TotalAlerts);
        Assert.All(body.Data, a => Assert.Equal("TSLA", a.Symbol));
    }

    [Fact]
    public async Task GetAlerts_FilterBySide_ReturnsFilteredResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-9",  "yoyomun", "TSLA", "bto", true),
            BuildEntity("id-10", "yoyomun", "AAPL", "stc", false),
            BuildEntity("id-11", "yoyomun", "SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?side=bto");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.TotalAlerts);
        Assert.All(body.Data, a => Assert.Equal("bto", a.Side));
    }

    [Fact]
    public async Task GetAlerts_FilterByRiskApproved_ReturnsFilteredResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-12", "yoyomun", "TSLA", "bto", true),
            BuildEntity("id-13", "yoyomun", "AAPL", "bto", false),
            BuildEntity("id-14", "yoyomun", "SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?riskApproved=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.TotalAlerts);
        Assert.All(body.Data, a => Assert.True(a.RiskApproved));
    }

    [Fact]
    public async Task GetAlerts_Pagination_SecondPageReturnsCorrectSubset()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        // Seed 15 alerts to test second page
        for (int i = 0; i < 15; i++)
            db.Alerts.Add(BuildEntity($"page-{i:D2}", "yoyomun", "TSLA", "bto", true));

        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts?page=2&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(15, body.TotalAlerts);
        Assert.Equal(5, body.Data.Count); // 15 total, page 2 of 10 = 5 remaining
        Assert.Equal(2, body.Page);
    }

    // -- Validation --

    [Theory]
    [InlineData("/api/alerts?page=0",       "page")]
    [InlineData("/api/alerts?pageSize=0",   "pageSize")]
    [InlineData("/api/alerts?pageSize=101", "pageSize")]
    public async Task GetAlerts_InvalidPagination_Returns400(
        string url, string expectedErrorField)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<ValidationProblemResponse>();
        Assert.NotNull(body);
        Assert.True(body.Errors.ContainsKey(expectedErrorField));
    }

    // -- GET /api/alerts/{id} --

    [Fact]
    public async Task GetAlertById_ExistingId_Returns200()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

        db.Alerts.Add(BuildEntity("existing-id", "yoyomun", "TSLA", "bto", true));
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/alerts/existing-id");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAlertById_NonExistentId_Returns404()
    {
        var response = await _client.GetAsync("/api/alerts/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private record ValidationProblemResponse(
        string Title,
        int Status,
        Dictionary<string, string[]> Errors);
}
