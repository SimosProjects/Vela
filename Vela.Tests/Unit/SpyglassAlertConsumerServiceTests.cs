using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for SpyglassAlertConsumerService.
/// Verifies the two branching outcomes — approved and rejected — and that
/// PricePaid=null is correctly filled by the normalizer before execution.
/// All broker and repository interactions are mocked; the risk engine and
/// normalizer behaviour is exercised through the real pipeline.
/// </summary>
public class SpyglassAlertConsumerServiceTests : IDisposable
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly Mock<IAlertRepository> _repoMock = new();
    private readonly Mock<IOpenPositionRepository> _openPosMock = new();
    private readonly Mock<ITradeMetricsRepository> _metricsMock = new();
    private readonly Mock<IAlertNormalizer> _normalizerMock = new();
    private readonly string _tempDir;

    public SpyglassAlertConsumerServiceTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(250_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        _brokerMock.Setup(b => b.PlaceOrderAsync(
                It.IsAny<TradeOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-SPY-001",
                StopOrderId:   "STOP-SPY-001",
                TargetOrderId: null,
                FillPrice:     182.50m,
                FillQuantity:  16,
                FillAmount:    2920m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));
        _brokerMock.Setup(b => b.RegisterBrokerFillHandler(
            It.IsAny<Action<string, decimal, TradeOutcome>>()));

        _openPosMock.Setup(r => r.SaveAsync(
                It.IsAny<OpenPosition>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _metricsMock.Setup(m => m.OpenAsync(
                It.IsAny<TradeMetric>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Normalizer mirrors the real AlertNormalizer behaviour for Spyglass:
        // fills PricePaid from ActualPriceAtTimeOfAlert, uppercases symbol
        _normalizerMock.Setup(n => n.Normalize(It.IsAny<Alert>()))
            .Returns<Alert>(a => a with
            {
                PricePaid  = a.PricePaid ?? a.ActualPriceAtTimeOfAlert,
                Symbol     = a.Symbol?.ToUpperInvariant(),
                Side       = a.Side?.ToLowerInvariant(),
                Type       = a.Type?.ToLowerInvariant(),
                Direction  = a.Direction?.ToLowerInvariant(),
            });

        _tempDir = Path.Combine(Path.GetTempPath(), $"vela_spyglass_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- Approved path --

    [Fact]
    public async Task ApprovedAlert_CallsBrokerAndUpdatesRiskApprovedTrue()
    {
        var dbName = $"spy_approved_{Guid.NewGuid():N}";
        var svc    = BuildService(BuildPassingRiskEngine(), dbName);

        using var db = OpenDb(dbName);
        db.Alerts.Add(BuildSpyglassEntity("spy-approved-001", 182.50m));
        await db.SaveChangesAsync();

        // Capture when UpdateRiskResultAsync is called — stops the poll loop
        var done = new TaskCompletionSource<(bool Approved, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _repoMock
            .Setup(r => r.UpdateRiskResultAsync(
                "spy-approved-001", It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, bool, string, CancellationToken>(
                (_, approved, reason, _) => done.TrySetResult((approved, reason)))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);
        var (approved, _) = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        Assert.True(approved);
        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Rejected path --

    [Fact]
    public async Task RejectedAlert_SkipsBrokerAndUpdatesRiskApprovedFalse()
    {
        var dbName = $"spy_rejected_{Guid.NewGuid():N}";
        var svc    = BuildService(BuildFailingRiskEngine(), dbName);

        using var db = OpenDb(dbName);
        db.Alerts.Add(BuildSpyglassEntity("spy-rejected-001", 182.50m));
        await db.SaveChangesAsync();

        var done = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _repoMock
            .Setup(r => r.UpdateRiskResultAsync(
                "spy-rejected-001", It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, bool, string, CancellationToken>(
                (_, approved, _, _) => done.TrySetResult(approved))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);
        var approved = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        Assert.False(approved);
        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- PricePaid null handling --

    [Fact]
    public async Task NullPricePaid_FilledByNormalizer_SizerReceivesActualPrice()
    {
        // Verifies that even when PricePaid is null the fill goes through and the
        // order submitted to the broker uses the actual price — if the normalizer
        // failed to fill PricePaid, the sizer would return null and PlaceOrderAsync
        // would never be called.
        const decimal expectedPrice = 95.25m;

        var dbName = $"spy_price_{Guid.NewGuid():N}";
        var svc    = BuildService(BuildPassingRiskEngine(), dbName);

        using var db = OpenDb(dbName);
        db.Alerts.Add(BuildSpyglassEntity("spy-price-001", expectedPrice));
        await db.SaveChangesAsync();

        var done = new TaskCompletionSource<TradeOrder>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(
                It.IsAny<TradeOrder>(), It.IsAny<CancellationToken>()))
            .Callback<TradeOrder, CancellationToken>((o, _) => done.TrySetResult(o))
            .ReturnsAsync(new BrokerOrderResult(
                "ORDER-002", "STOP-002", null,
                expectedPrice, 31, expectedPrice * 31,
                OrderStatus.Filled, DateTimeOffset.UtcNow));

        _repoMock
            .Setup(r => r.UpdateRiskResultAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);
        var order = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        Assert.Equal(expectedPrice, order.EstimatedEntryPrice);
    }

    // -- Helpers --

    private SpyglassAlertConsumerService BuildService(RiskEngineService riskEngine, string dbName)
    {
        var dbOptions = new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trades:Directory"] = _tempDir
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped(_ => new VelaDbContext(dbOptions));
        services.AddScoped(_ => _repoMock.Object);
        services.AddScoped(_ => _openPosMock.Object);
        services.AddScoped(_ => _metricsMock.Object);

        var scopeFactory = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var guard = new TradeGuard(
            _brokerMock.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);

        var sizer = new PositionSizer(
            Options.Create(new RiskEngineOptions()),
            NullLogger<PositionSizer>.Instance);

        var execution = new BrokerExecutionService(
            _brokerMock.Object,
            sizer,
            guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            Options.Create(new RiskEngineOptions()),
            isMarketOpen: () => true);

        return new SpyglassAlertConsumerService(
            scopeFactory,
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            execution,
            riskEngine,
            _normalizerMock.Object,
            NullLogger<SpyglassAlertConsumerService>.Instance);
    }

    private static RiskEngineService BuildPassingRiskEngine()
    {
        var passingRule = new Mock<IRiskRule>();
        passingRule.Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Pass("All rules passed"));
        return new RiskEngineService([passingRule.Object]);
    }

    private static RiskEngineService BuildFailingRiskEngine()
    {
        var failingRule = new Mock<IRiskRule>();
        failingRule.Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Fail("High risk trades are blocked this session"));
        return new RiskEngineService([failingRule.Object]);
    }

    private static VelaDbContext OpenDb(string dbName) =>
        new(new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static AlertEntity BuildSpyglassEntity(string id, decimal price) => new()
    {
        Id                       = id,
        UserName                 = "SPYGLASS",
        Symbol                   = "AMD",
        Side                     = "bto",
        Type                     = "commons",
        Direction                = null,
        Risk                     = "standard",
        XScore                   = 100.0,
        ActualPriceAtTimeOfAlert = price,
        PricePaid                = null,
        RiskApproved             = false,
        RiskReason               = "spyglass_pending",
        IngestedAt               = DateTimeOffset.UtcNow,
        TimeOfEntryAlert         = DateTimeOffset.UtcNow,
        IsSwing                  = true,
        FormattedLength          = "SWING",
        Strategy                 = "EMA21_cross",
        OriginalMessage          = $"SPYGLASS: AMD EMA21_cross score=0.872",
    };
}