using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Data;

namespace Vela.Tests.Integration;

/// <summary>
/// Integration tests for AlertRepository against a real PostgreSQL container.
/// Uses TestContainers so ExecuteUpdateAsync runs against the actual Npgsql provider,
/// not the EF in-memory provider which does not support bulk update operations.
/// </summary>
[Collection("Integration")]
public class AlertRepositoryTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _postgres;
    private VelaDbContext _db = null!;

    public AlertRepositoryTests(PostgreSqlFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        _db = new VelaDbContext(
            new DbContextOptionsBuilder<VelaDbContext>()
                .UseNpgsql(_postgres.ConnectionString)
                .Options);

        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // Clean up only the rows this test class inserted
        var spyglassRows = _db.Alerts.AsTracking()
            .Where(a => a.UserName == "SPYGLASS_REPO_TEST");
        _db.Alerts.RemoveRange(spyglassRows);
        await _db.SaveChangesAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task UpdateRiskResultAsync_SetsBothRiskApprovedAndRiskReason()
    {
        // Arrange — insert a pending Spyglass alert
        var entity = BuildEntity("repo-test-001");
        _db.Alerts.Add(entity);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var repo = new AlertRepository(_db, NullLogger<AlertRepository>.Instance);

        // Act
        await repo.UpdateRiskResultAsync("repo-test-001", riskApproved: true, riskReason: "All rules passed");

        // Assert — clear the tracker so we read from DB, not cache
        _db.ChangeTracker.Clear();
        var updated = await _db.Alerts.FindAsync("repo-test-001");

        Assert.NotNull(updated);
        Assert.True(updated.RiskApproved);
        Assert.Equal("All rules passed", updated.RiskReason);
    }

    [Fact]
    public async Task UpdateRiskResultAsync_RejectedAlert_SetsBothColumnsCorrectly()
    {
        var entity = BuildEntity("repo-test-002");
        _db.Alerts.Add(entity);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var repo = new AlertRepository(_db, NullLogger<AlertRepository>.Instance);

        await repo.UpdateRiskResultAsync(
            "repo-test-002",
            riskApproved: false,
            riskReason:   "High risk trades are blocked this session");

        _db.ChangeTracker.Clear();
        var updated = await _db.Alerts.FindAsync("repo-test-002");

        Assert.NotNull(updated);
        Assert.False(updated.RiskApproved);
        Assert.Equal("High risk trades are blocked this session", updated.RiskReason);
    }

    // -- Helpers --

    private static AlertEntity BuildEntity(string id) => new()
    {
        Id           = id,
        UserName     = "SPYGLASS_REPO_TEST",
        Symbol       = "AMD",
        Side         = "bto",
        RiskApproved = false,
        RiskReason   = "spyglass_pending",
        IngestedAt   = DateTimeOffset.UtcNow,
        TimeOfEntryAlert = DateTimeOffset.UtcNow,
    };
}