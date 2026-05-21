using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;
using TradeFlow.Worker.Data;

namespace TradeFlow.Tests.Unit;

public class BrokerExecutionServiceTests
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly Mock<ITradeMetricsRepository> _metricsMock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradeGuard _guard;
    private readonly PositionSizer _sizer = new();
    private readonly BrokerExecutionService _execution;
    private readonly BrokerExecutionService _executionMarketOpen;

    public BrokerExecutionServiceTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(0m);
        _brokerMock.Setup(b => b.RegisterBrokerFillHandler(
            It.IsAny<Action<string, decimal, TradeOutcome>>()));
        _brokerMock.Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "ORDER-001",
                StopOrderId: "STOP-001",
                TargetOrderId: "TGT-001",
                FillPrice: 4.95m,
                FillQuantity: 2,
                FillAmount: 990m,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow));
        _brokerMock.Setup(b => b.ClosePositionAsync(
                It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "CLOSE-001",
                StopOrderId: null,
                TargetOrderId: null,
                FillPrice: 9.90m,
                FillQuantity: 2,
                FillAmount: 1_980m,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow));

        _guard = new TradeGuard(_brokerMock.Object, NullLogger<TradeGuard>.Instance);

        var services = new ServiceCollection();
        services.AddScoped<ITradeMetricsRepository>(_ => _metricsMock.Object);
        services.AddScoped<IOpenPositionRepository>(_ => Mock.Of<IOpenPositionRepository>());
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder().Build();

        // Default instance — market always closed (for tests that expect no order)
        _execution = new BrokerExecutionService(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            _scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            isMarketOpen: () => false);

        // Market-open instance — for tests that exercise the trading path
        _executionMarketOpen = new BrokerExecutionService(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            _scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            isMarketOpen: () => true);
    }

    private static Alert BuildAlert(
        string side = "bto",
        string type = "options",
        string direction = "call",
        decimal? pricePaid = 4.95m,
        string? contractSymbol = "TSLA260620C00450000",
        decimal? strike = 450,
        string userName = "TestTrader") =>
        new(
            Id: Guid.NewGuid().ToString(),
            UserId: null,
            UserName: userName,
            Symbol: "TSLA",
            Type: type,
            Direction: direction,
            Strike: strike,
            Expiration: "2026-06-20T00:00:00",
            OptionsContractSymbol: contractSymbol,
            ContractDescription: null,
            Side: side,
            Status: "open",
            Result: null,
            ActualPriceAtTimeOfAlert: pricePaid,
            ActualPriceAtTimeOfExit: null,
            PricePaid: pricePaid,
            PriceAtExit: 9.90m,
            HighestPrice: null,
            LowestPrice: null,
            LastCheckedPrice: null,
            Risk: "standard",
            LastKnownPercentProfit: null,
            IsProfitableTrade: null,
            XScore: 80,
            CanAverage: true,
            TimeOfEntryAlert: null,
            TimeOfFullExitAlert: null,
            FormattedLength: null,
            IsSwing: false,
            IsBullish: true,
            IsShort: false,
            Strategy: null,
            OriginalMessage: null,
            OriginalExitMessage: null);

    private static AlertClassification CallClassification() =>
        new(AlertCategory.CallOptionEntry, "Call option entry");

    [Fact]
    public async Task HandleEntryAsync_SkipsWhenMarketClosed()
    {
        // _execution is configured with isMarketOpen: () => false
        var alert = BuildAlert();
        var classification = CallClassification();

        await _execution.HandleEntryAsync(alert, classification);

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_SkipsWhenPriceMissing()
    {
        var alert = BuildAlert(pricePaid: null);
        var classification = CallClassification();

        await _execution.HandleEntryAsync(alert, classification);

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleExitAsync_SkipsWhenNoOpenPosition()
    {
        var alert = BuildAlert(side: "stc");

        await _execution.HandleExitAsync(alert);

        _brokerMock.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleExitAsync_ClosesWhenOpenPositionExists()
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
            TargetPrice: 14.85m);

        var result = new BrokerOrderResult(
            OrderId: "ORDER-001",
            StopOrderId: "STOP-001",
            TargetOrderId: "TGT-001",
            FillPrice: 4.95m,
            FillQuantity: 2,
            FillAmount: 990m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow);

        _guard.RegisterOpen(order, result);

        var exitAlert = BuildAlert(side: "stc", userName: "TestTrader");
        await _executionMarketOpen.HandleExitAsync(exitAlert);

        _brokerMock.Verify(b => b.ClosePositionAsync(
            It.Is<TradeRecord>(t => t.Symbol == "TSLA"),
            TradeOutcome.XtradesExit,
            default), Times.Once);
    }

    [Fact]
    public async Task HandleExitAsync_SkipsWhenTraderMismatch()
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
            TargetPrice: 14.85m);

        _guard.RegisterOpen(order, new BrokerOrderResult(
            OrderId: "ORDER-001",
            StopOrderId: "STOP-001",
            TargetOrderId: "TGT-001",
            FillPrice: 4.95m,
            FillQuantity: 2,
            FillAmount: 990m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow));

        var exitAlert = BuildAlert(side: "stc", userName: "DifferentTrader");
        await _executionMarketOpen.HandleExitAsync(exitAlert);

        _brokerMock.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default), Times.Never);
    }
}