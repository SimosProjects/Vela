using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Data;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

/// <summary>
/// Unit tests for PositionMonitorService broker fill detection.
/// All tests run in memory with no external dependencies.
/// </summary>
public class PositionMonitorServiceTests : IDisposable
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly Mock<IOpenPositionRepository> _repoMock = new();
    private readonly TradeGuard _guard;
    private readonly PositionMonitorService _monitor;
    private readonly string _tempDir;

    public PositionMonitorServiceTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default)).ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default)).ReturnsAsync(0m);
        _brokerMock.Setup(b => b.RegisterBrokerFillHandler(
            It.IsAny<Action<string, decimal, TradeOutcome>>()));

        var riskOptions = Options.Create(new RiskEngineOptions());
        _guard = new TradeGuard(_brokerMock.Object, riskOptions, NullLogger<TradeGuard>.Instance);  

        _tempDir = Path.Combine(Path.GetTempPath(), $"tradeflow_monitor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trades:Directory"] = _tempDir
            })
            .Build();

        var csv = new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance);
        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);

        var services = new ServiceCollection();
        services.AddScoped<IOpenPositionRepository>(_ => _repoMock.Object);
        var scopeFactory = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        _monitor = new PositionMonitorService(
            _guard,
            _brokerMock.Object,
            csv,
            discord,
            scopeFactory,
            NullLogger<PositionMonitorService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task HandleBrokerFill_ClosesMatchingOpenPosition()
    {
        var (order, result) = RegisterOpenTrade("ORDER-100");

        Action<string, decimal, TradeOutcome>? capturedHandler = null;
        _brokerMock
            .Setup(b => b.RegisterBrokerFillHandler(It.IsAny<Action<string, decimal, TradeOutcome>>()))
            .Callback<Action<string, decimal, TradeOutcome>>(h => capturedHandler = h);

        using var cts = new CancellationTokenSource();
        var monitorTask = _monitor.StartAsync(cts.Token);

        await Task.Delay(50);

        capturedHandler.Should().NotBeNull();
        capturedHandler!("ORDER-100", 7.50m, TradeOutcome.StoppedOut);

        await Task.Delay(100);

        _guard.GetOpenTrades().Should().BeEmpty();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task HandleBrokerFill_SkipsWhenNoMatchingPosition()
    {
        Action<string, decimal, TradeOutcome>? capturedHandler = null;
        _brokerMock
            .Setup(b => b.RegisterBrokerFillHandler(It.IsAny<Action<string, decimal, TradeOutcome>>()))
            .Callback<Action<string, decimal, TradeOutcome>>(h => capturedHandler = h);

        using var cts = new CancellationTokenSource();
        await _monitor.StartAsync(cts.Token);
        await Task.Delay(50);

        var act = () =>
        {
            capturedHandler!("UNKNOWN-ORDER", 5.00m, TradeOutcome.StoppedOut);
            return Task.Delay(100);
        };

        await act.Should().NotThrowAsync();
        _guard.GetOpenTrades().Should().BeEmpty();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task HandleBrokerFill_StoppedOut_RecordsCorrectOutcome()
    {
        RegisterOpenTrade("ORDER-200");

        Action<string, decimal, TradeOutcome>? capturedHandler = null;
        _brokerMock
            .Setup(b => b.RegisterBrokerFillHandler(It.IsAny<Action<string, decimal, TradeOutcome>>()))
            .Callback<Action<string, decimal, TradeOutcome>>(h => capturedHandler = h);

        using var cts = new CancellationTokenSource();
        await _monitor.StartAsync(cts.Token);
        await Task.Delay(50);

        capturedHandler!("ORDER-200", 2.48m, TradeOutcome.StoppedOut);
        await Task.Delay(100);

        var optionsPath = Path.Combine(_tempDir, "options_trades.csv");
        var content     = await File.ReadAllTextAsync(optionsPath);
        content.Should().Contain("StoppedOut");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task HandleBrokerFill_TargetHit_RecordsCorrectOutcome()
    {
        RegisterOpenTrade("ORDER-300");

        Action<string, decimal, TradeOutcome>? capturedHandler = null;
        _brokerMock
            .Setup(b => b.RegisterBrokerFillHandler(It.IsAny<Action<string, decimal, TradeOutcome>>()))
            .Callback<Action<string, decimal, TradeOutcome>>(h => capturedHandler = h);

        using var cts = new CancellationTokenSource();
        await _monitor.StartAsync(cts.Token);
        await Task.Delay(50);

        capturedHandler!("ORDER-300", 14.85m, TradeOutcome.TargetHit);
        await Task.Delay(100);

        var optionsPath = Path.Combine(_tempDir, "options_trades.csv");
        var content     = await File.ReadAllTextAsync(optionsPath);
        content.Should().Contain("TargetHit");

        await cts.CancelAsync();
    }

    // -- Helpers --

    private (TradeOrder Order, BrokerOrderResult Result) RegisterOpenTrade(string orderId)
    {
        var order = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(),
            UserName: "TestTrader",
            Symbol: "TSLA",
            TradeType: TradeType.Options,
            OptionsContractSymbol: "TSLA260620C00450000",
            Direction: "call",
            Strike: 450,
            Expiration: "2026-06-20",
            Quantity: 2,
            EstimatedEntryPrice: 4.95m,
            BudgetUsed: 990m,
            StopPrice: 2.48m,
            TargetPrice: 14.85m,
            TrailPercent: 50.0);

        var result = new BrokerOrderResult(
            OrderId: orderId,
            StopOrderId: "STOP-001",
            TargetOrderId: "TGT-001",
            FillPrice: 4.95m,
            FillQuantity: 2,
            FillAmount: 990m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow);

        _guard.RegisterOpen(order, result);
        return (order, result);
    }
}