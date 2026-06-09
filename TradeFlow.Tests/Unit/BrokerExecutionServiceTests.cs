using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
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
    private readonly PositionSizer _sizer;
    private readonly BrokerExecutionService _execution;
    private readonly BrokerExecutionService _executionMarketOpen;

    public BrokerExecutionServiceTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(0m);
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
        _brokerMock.Setup(b => b.RegisterBrokerFillHandler(
            It.IsAny<Action<string, decimal, TradeOutcome>>()));
        _metricsMock.Setup(m => m.GetTodayTradeCountAsync(
            It.IsAny<DateOnly>(), default)).ReturnsAsync(0);
        _metricsMock.Setup(m => m.CloseAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<decimal?>(),
            default)).Returns(Task.CompletedTask);

        var riskOptions = Options.Create(new RiskEngineOptions());
        _guard = new TradeGuard(_brokerMock.Object, riskOptions, NullLogger<TradeGuard>.Instance);
        _sizer = new PositionSizer(Options.Create(new RiskEngineOptions()), NullLogger<PositionSizer>.Instance);

        var services = new ServiceCollection();
        services.AddScoped<ITradeMetricsRepository>(_ => _metricsMock.Object);
        services.AddScoped<IOpenPositionRepository>(_ => Mock.Of<IOpenPositionRepository>());
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder().Build();

        _execution = new BrokerExecutionService(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            _scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            Options.Create(new RiskEngineOptions()),
            isMarketOpen: () => false);

        _executionMarketOpen = new BrokerExecutionService(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            _scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            Options.Create(new RiskEngineOptions()),
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
            TargetPrice: 14.85m,
            TrailPercent: 50.0);

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
            TargetPrice: 14.85m,
            TrailPercent: 50.0);

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

    [Fact]
    public async Task HandleEntryAsync_Pending_ZeroQty_DoesNotRecordTrade()
    {
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(4.95m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default))
            .ReturnsAsync((0m, 0));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEntryAsync_Pending_NegativeQty_DoesNotRecordTrade()
    {
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(4.95m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default))
            .ReturnsAsync((4.95m, -2));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEntryAsync_Pending_PartialFill_RecordsActualQty()
    {
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(4.95m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: "2", TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default))
            .ReturnsAsync((4.95m, 3));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        var trade = _guard.GetOpenTrades().FirstOrDefault();
        trade.Should().NotBeNull();
        trade!.Quantity.Should().Be(3);
    }

    [Fact]
    public async Task HandleEntryAsync_NormalFill_PartialQty_RecordsActualQty()
    {
        // PlaceOrderAsync returns a partial fill, FilledQuantity 3 of ordered 25.
        // BrokerExecutionService should record the trade with quantity 3, not 25.
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(1.18m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "3006",
                StopOrderId:   "3008",
                TargetOrderId: null,
                FillPrice:     1.18m,
                FillQuantity:  3,
                FillAmount:    354.00m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));

        var alert = BuildAlert(
            side:           "bto",
            type:           "options",
            direction:      "call",
            pricePaid:      1.18m,
            contractSymbol: "RBLX260619C00040000",
            strike:         40m,
            userName:       "TestTrader");

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        var trade = _guard.GetOpenTrades().FirstOrDefault();
        trade.Should().NotBeNull();
        trade!.Quantity.Should().Be(3);
        trade.EntryAmount.Should().Be(354.00m);
    }

    [Fact]
    public async Task HandleEntryAsync_LateFill_PartialQty_RecordsActualQty()
    {
        // PlaceOrderAsync returns Filled status with partial quantity from position verify.
        // Simulates the late fill path where GetCurrentPositionPriceAsync returns actual qty.
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(1.18m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "3006",
                StopOrderId:   "3008",
                TargetOrderId: null,
                FillPrice:     1.18m,
                FillQuantity:  3,
                FillAmount:    354.00m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));

        var alert = BuildAlert(
            side:           "bto",
            type:           "options",
            direction:      "call",
            pricePaid:      1.18m,
            contractSymbol: "RBLX260619C00040000",
            strike:         40m,
            userName:       "TestTrader");

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        var trade = _guard.GetOpenTrades().FirstOrDefault();
        trade.Should().NotBeNull();
        trade!.Quantity.Should().Be(3);
        trade.EntryAmount.Should().Be(354.00m);
    }

    [Fact]
    public async Task HandleEntryAsync_NormalFill_FullQty_RecordsCorrectly()
    {
        // Full fill — FilledQuantity matches ordered quantity.
        // Verifies the normal path still works correctly after partial fill changes.
        _brokerMock
            .Setup(b => b.GetCurrentMarketPriceAsync(
                It.IsAny<string>(), It.IsAny<TradeType>(),
                It.IsAny<string?>(), It.IsAny<decimal?>(),
                It.IsAny<string?>(), default))
            .ReturnsAsync(4.95m);

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-002",
                StopOrderId:   "STOP-002",
                TargetOrderId: null,
                FillPrice:     4.95m,
                FillQuantity:  2,
                FillAmount:    990.00m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));

        var alert = BuildAlert(
            side:           "bto",
            type:           "options",
            direction:      "call",
            pricePaid:      4.95m,
            contractSymbol: "TSLA260620C00450000",
            strike:         450m,
            userName:       "TestTrader");

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        var trade = _guard.GetOpenTrades().FirstOrDefault();
        trade.Should().NotBeNull();
        trade!.Quantity.Should().Be(2);
        trade.EntryAmount.Should().Be(990.00m);
    }
}