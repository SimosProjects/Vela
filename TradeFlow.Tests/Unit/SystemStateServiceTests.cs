using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Data;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

/// <summary>
/// Unit tests for SystemStateService and the TradeGuard cache properties it reads.
/// All tests run in memory with no external dependencies.
/// </summary>
public class SystemStateServiceTests
{
    // -- Builders --

    private static TradeGuard BuildTradeGuard()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);
        return new TradeGuard(
            broker.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);
    }

    private static IbkrConnectionService BuildIbkrService()
    {
        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);
        return new IbkrConnectionService(
            Options.Create(new IbkrOptions { Host = "127.0.0.1", Port = 4002, ClientId = 9 }),
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            discord);
    }

    private static (SystemStateService svc, string dbName) BuildService(TradeGuard? guard = null)
    {
        guard ??= BuildTradeGuard();
        var ibkr = BuildIbkrService();

        var dbName = $"sysstate_{Guid.NewGuid():N}";
        var dbOptions = new DbContextOptionsBuilder<TradeFlowDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new TradeFlowDbContext(dbOptions));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new SystemStateService(
            scopeFactory, ibkr, guard, NullLogger<SystemStateService>.Instance);

        return (svc, dbName);
    }

    private static TradeFlowDbContext OpenDb(string dbName) =>
        new(new DbContextOptionsBuilder<TradeFlowDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    // -- TradeGuard cache property tests --

    [Fact]
    public void TradeGuard_CachedBalance_ReturnsValueFromSetCacheForTesting()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(52_140m, 0m);
        guard.CachedBalance.Should().Be(52_140m);
    }

    [Fact]
    public void TradeGuard_CachedOpenValue_ReturnsValueFromSetCacheForTesting()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(0m, 2_850m);
        guard.CachedOpenValue.Should().Be(2_850m);
    }

    [Fact]
    public void TradeGuard_CachedProperties_DefaultToZeroBeforeFirstRefresh()
    {
        var guard = BuildTradeGuard();
        guard.CachedBalance.Should().Be(0m);
        guard.CachedOpenValue.Should().Be(0m);
    }

    // -- WriteHeartbeatAsync DB write tests --

    [Fact]
    public async Task WriteHeartbeat_NoExistingRow_CreatesRowWithAllValues()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(52_140m, 2_850m);
        var (svc, dbName) = BuildService(guard);

        svc.UpdateRegime("Bullish", 1.0m, false, 578.42m, 572.10m, 561.40m, 521.80m, 13.21m, -1.4m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var db = OpenDb(dbName);
        var row = await db.SystemState.FindAsync(1);

        row.Should().NotBeNull();
        row!.RegimeTier.Should().Be("Bullish");
        row.SizingMultiplier.Should().Be(1.0m);
        row.BlockCalls.Should().BeFalse();
        row.SpyPrice.Should().Be(578.42m);
        row.Ma20.Should().Be(572.10m);
        row.Ma50.Should().Be(561.40m);
        row.Ma200.Should().Be(521.80m);
        row.Vix.Should().Be(13.21m);
        row.VixDelta.Should().Be(-1.4m);
        row.ChopScore.Should().Be(1);
        row.AccountBalance.Should().Be(52_140m);
        row.OpenValue.Should().Be(2_850m);
        row.WorkerHeartbeat.Should().NotBeNull();
        row.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WriteHeartbeat_ExistingRow_OverwritesRegimeAndAccount()
    {
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState { Id = 1, RegimeTier = "Bearish", SizingMultiplier = 0.25m });
            await seed.SaveChangesAsync();
        }

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.RegimeTier.Should().Be("Bullish");
        row.SizingMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public async Task WriteHeartbeat_DoesNotOverwriteIsPaused()
    {
        // The Api owns is_paused. The Worker heartbeat must never reset it.
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState { Id = 1, IsPaused = true });
            await seed.SaveChangesAsync();
        }

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.IsPaused.Should().BeTrue("heartbeat must not overwrite is_paused set by the Api");
    }

    [Fact]
    public async Task WriteHeartbeat_DbUnavailable_DoesNotPropagate()
    {
        // Verify the trading path is never affected by a failed DB write.
        var guard = BuildTradeGuard();
        var ibkr = BuildIbkrService();

        var dbOptions = new DbContextOptionsBuilder<TradeFlowDbContext>()
            .UseInMemoryDatabase($"sysstate_throw_{Guid.NewGuid():N}")
            .Options;

        var disposedDb = new TradeFlowDbContext(dbOptions);
        disposedDb.Dispose();

        var services = new ServiceCollection();
        services.AddScoped<TradeFlowDbContext>(_ => disposedDb);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new SystemStateService(
            scopeFactory, ibkr, guard, NullLogger<SystemStateService>.Instance);

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);

        var act = () => svc.WriteHeartbeatAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteHeartbeat_Cancelled_DoesNotPropagate()
    {
        // OperationCanceledException on shutdown must be swallowed cleanly.
        var (svc, _) = BuildService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => svc.WriteHeartbeatAsync(cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteHeartbeat_DefaultRegime_WritesUnknownTier()
    {
        // Before 9:20am ET the regime has not been set, row should still be created.
        var (svc, dbName) = BuildService();

        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var db = OpenDb(dbName);
        var row = await db.SystemState.FindAsync(1);
        row.Should().NotBeNull();
        row!.RegimeTier.Should().Be("Unknown");
        row.SizingMultiplier.Should().Be(1.0m);
    }
}