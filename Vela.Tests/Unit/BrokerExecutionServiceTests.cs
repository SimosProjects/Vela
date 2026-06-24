using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Engine;
using Vela.Worker.Models;
using Vela.Worker.Services;
using Vela.Worker.Data;

namespace Vela.Tests.Unit;

public class BrokerExecutionServiceTests
{
    // Writes to a temp directory so tests never pollute the real trades CSV files.
    private static readonly IConfiguration TestConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trades:Directory"] = Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "..", "..", "trades", "test")
        })
        .Build();

    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly Mock<ITradeMetricsRepository> _metricsMock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradeGuard _guard;
    private readonly PositionSizer _sizer;
    private readonly BrokerExecutionService _execution;
    private readonly BrokerExecutionService _executionMarketOpen;

    // Builds a BrokerExecutionService with the shared mock dependencies and the given options.
    // Avoids repeating the 9-argument constructor in every test that needs custom configuration.
    private BrokerExecutionService BuildService(
        RiskEngineOptions? options = null,
        bool marketOpen = true) =>
        new(
            _brokerMock.Object,
            _sizer,
            _guard,
            new CsvTradeLogger(TestConfig, NullLogger<CsvTradeLogger>.Instance),
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance),
            _scopeFactory,
            NullLogger<BrokerExecutionService>.Instance,
            Options.Create(options ?? new RiskEngineOptions()),
            isMarketOpen: () => marketOpen);

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
        _brokerMock.Setup(b => b.ReplaceTrailStopAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TradeOrder>(),
            It.IsAny<double>(), default))
            .ReturnsAsync((string?)null);
        _brokerMock.Setup(b => b.GetAllPositionsAsync(default))
            .ReturnsAsync(new PositionsSnapshot([], false));
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

        var config = TestConfig;

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
        string userName = "TestTrader",
        decimal? actualPriceAtTimeOfAlert = null,
        string expiration = "2027-09-17T00:00:00") =>
        new(
            Id: Guid.NewGuid().ToString(),
            UserId: null,
            UserName: userName,
            Symbol: "TSLA",
            Type: type,
            Direction: direction,
            Strike: strike,
            Expiration: expiration,
            OptionsContractSymbol: contractSymbol,
            ContractDescription: null,
            Side: side,
            Status: "open",
            Result: null,
            ActualPriceAtTimeOfAlert: actualPriceAtTimeOfAlert ?? pricePaid,
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
    public async Task HandleEntryAsync_Pending_ZeroQty_RecordsWithEstimatedFill()
    {
        // Both position checks return (0, 0), gateway timeout. Trade is recorded with
        // estimated fill rather than silently dropped. StopOrderId is null because no
        // trail stop was placed; the no-stop Discord critical fires in production.
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
 
        var trades = _guard.GetOpenTrades();
        trades.Should().HaveCount(1);
        trades.First().StopOrderId.Should().BeNull();
        trades.First().EntryPrice.Should().Be(4.95m);
    }
 
    [Fact]
    public async Task HandleEntryAsync_Pending_NegativeQty_RecordsWithEstimatedFill()
    {
        // Negative quantity (positionQty <= 0) is treated as a timeout, same estimated
        // fill path as zero qty. Protects against malformed Gateway responses creating
        // an unrecorded open position at IBKR.
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
 
        var trades = _guard.GetOpenTrades();
        trades.Should().HaveCount(1);
        trades.First().StopOrderId.Should().BeNull();
        trades.First().EntryPrice.Should().Be(4.95m);
    }

    [Fact]
    public async Task HandleEntryAsync_Pending_PartialFill_RecordsActualQty()
    {
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
        // PlaceOrderAsync returns Filled with a quantity lower than what was ordered.
        // Verifies the recorded position reflects the broker's actual fill quantity.
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
        // PlaceOrderAsync times out (Pending), then GetCurrentPositionPriceAsync confirms
        // a partial fill. Verifies the pending path records only what IBKR actually holds.
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "3006",
                StopOrderId:   "3008",
                TargetOrderId: null,
                FillPrice:     0m,
                FillQuantity:  0,
                FillAmount:    0m,
                Status:        OrderStatus.Pending,
                FilledAt:      DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default))
            .ReturnsAsync((1.18m, 3));

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
    }

    [Fact]
    public async Task HandleEntryAsync_NormalFill_FullQty_RecordsCorrectly()
    {
        // Full fill, FilledQuantity matches ordered quantity.
        // Verifies the normal path records the fill correctly.
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

    [Fact]
    public async Task HandleEntryAsync_HighPostFillSlippage_RecordsTrade_DoesNotClose()
    {
        // Fill price is well above the alerted price (22% slippage).
        // PricePaid equals ActualPriceAtTimeOfAlert so staleness gate passes.
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-003",
                StopOrderId:   "STOP-003",
                TargetOrderId: null,
                FillPrice:     6.10m,
                FillQuantity:  2,
                FillAmount:    1_220.00m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));

        var alert = BuildAlert(
            side:           "bto",
            type:           "options",
            direction:      "call",
            pricePaid:      5.00m,
            contractSymbol: "TSLA260620C00450000",
            strike:         450m,
            userName:       "TestTrader");

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().NotBeEmpty();

        _brokerMock.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(),
            TradeOutcome.ForcedClose,
            default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_AlertStaleness_ExceedsThreshold_Skips()
    {
        // PricePaid is 83% above ActualPriceAtTimeOfAlert, exceeding the 25% default threshold.
        var alert = BuildAlert(
            pricePaid: 5.50m,
            actualPriceAtTimeOfAlert: 3.00m);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_AlertStaleness_WithinThreshold_Proceeds()
    {
        // PricePaid is 11% above ActualPriceAtTimeOfAlert, within the 25% default threshold.
        var alert = BuildAlert(
            pricePaid: 5.00m,
            actualPriceAtTimeOfAlert: 4.50m);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_AlertStaleness_MissingActualPrice_Proceeds()
    {
        // ActualPriceAtTimeOfAlert is null, gate is skipped and the trade proceeds.
        var alert = BuildAlert(
            pricePaid: 5.50m,
            actualPriceAtTimeOfAlert: null);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_AlertStaleness_Disabled_Proceeds()
    {
        // AlertStalenessMaxSlippagePct = 0 disables the gate entirely.
        var service = BuildService(new RiskEngineOptions { AlertStalenessMaxSlippagePct = 0m });
 
        var alert = BuildAlert(
            pricePaid: 10.00m,
            actualPriceAtTimeOfAlert: 1.00m);
 
        await service.HandleEntryAsync(alert, CallClassification());
 
        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_StandardOptions_PlaceOrderReceivesLimitPrice()
    {
        // PositionSizer computes LimitPrice from OptionsStandardMaxSlippagePct.
        // Verifies the computed limit price flows through to PlaceOrderAsync.
        var alert = BuildAlert(
            pricePaid:      4.95m,
            contractSymbol: "TSLA260620C00450000",
            strike:         450m);

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        var expectedLimit = Math.Round(4.95m * (1 + new RiskEngineOptions().OptionsStandardMaxSlippagePct / 100), 2);

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.Is<TradeOrder>(o => o.LimitPrice == expectedLimit),
            default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_LottoAlert_PlaceOrderHasNoLimitPrice()
    {
        // Lotto options always use a market order regardless of slippage config.
        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

        var alert = BuildAlert(
            pricePaid:      0.50m,
            contractSymbol: "TSLA260620C00450000",
            strike:         450m,
            expiration:     $"{todayEt:yyyy-MM-dd}T00:00:00");

        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.Is<TradeOrder>(o => o.LimitPrice == null),
            default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_ElevatedPostFillSlippage_TightensTrail()
    {
        // Fill price is 20% above alerted price, exceeding the 10% warning threshold.
        // Verifies ReplaceTrailStopAsync is called with the configured tighter trail percent.
        var options = new RiskEngineOptions
        {
            PostFillSlippageWarningPct = 10.0,
            HighSlippageTrailPct       = 25.0
        };
 
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-010",
                StopOrderId:   "STOP-010",
                TargetOrderId: null,
                FillPrice:     5.94m,
                FillQuantity:  2,
                FillAmount:    1_188m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));
 
        var service = BuildService(options);
        var alert   = BuildAlert(pricePaid: 4.95m);
 
        await service.HandleEntryAsync(alert, CallClassification());
 
        _brokerMock.Verify(b => b.ReplaceTrailStopAsync(
            "STOP-010",
            It.IsAny<int>(),
            It.IsAny<TradeOrder>(),
            options.HighSlippageTrailPct,
            default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_NormalPostFillSlippage_DoesNotTightenTrail()
    {
        // Fill price is 5% above alerted price, within the 10% threshold.
        // Verifies ReplaceTrailStopAsync is not called.
        var options = new RiskEngineOptions
        {
            PostFillSlippageWarningPct = 10.0,
            HighSlippageTrailPct       = 25.0
        };
 
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-011",
                StopOrderId:   "STOP-011",
                TargetOrderId: null,
                FillPrice:     5.20m,
                FillQuantity:  2,
                FillAmount:    1_040m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));
 
        var service = BuildService(options);
        var alert   = BuildAlert(pricePaid: 4.95m);
 
        await service.HandleEntryAsync(alert, CallClassification());
 
        _brokerMock.Verify(b => b.ReplaceTrailStopAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TradeOrder>(),
            It.IsAny<double>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_TrailTightening_Disabled_DoesNotReplace()
    {
        // HighSlippageTrailPct = 0 disables tightening even with 100% slippage.
        var options = new RiskEngineOptions
        {
            PostFillSlippageWarningPct = 10.0,
            HighSlippageTrailPct       = 0.0
        };
 
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId:       "ORDER-012",
                StopOrderId:   "STOP-012",
                TargetOrderId: null,
                FillPrice:     9.90m,
                FillQuantity:  2,
                FillAmount:    1_980m,
                Status:        OrderStatus.Filled,
                FilledAt:      DateTimeOffset.UtcNow));
 
        var service = BuildService(options);
        var alert   = BuildAlert(pricePaid: 4.95m);
 
        await service.HandleEntryAsync(alert, CallClassification());
 
        _brokerMock.Verify(b => b.ReplaceTrailStopAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TradeOrder>(),
            It.IsAny<double>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_PriceProtectionRejectionDuringFillWindow_DoesNotRecordGhost()
    {
        // IbkrBrokerService now returns Rejected (not Pending) when a price-protection
        // cancellation races with the limit order fill window. Verifies that no position
        // is recorded in TradeGuard and GetCurrentPositionPriceAsync is never called.
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Rejected, FilledAt: DateTimeOffset.UtcNow,
                RejectionReason: "PRICE_PROTECTION:5.90"));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().BeEmpty();
        _brokerMock.Verify(
            b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default),
            Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_HighSlippage_StopAndTargetRebasedFromFillPrice()
    {
        // Fill is 197% above alert. After trail tightening to 25%, stop and target
        // persisted to TradeGuard should be calculated from the $11.00 fill, not the $3.70 alert.
        // Stop: 11.00 * (1 - 0.25) = $8.25. Target: 11.00 * (11.10 / 3.70) = 11.00 * 3.0 = $33.00
        var options = new RiskEngineOptions
        {
            PostFillSlippageWarningPct = 50.0,
            HighSlippageTrailPct       = 25.0,
            OptionsStandardTrailPct    = 40.0,
        };

        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "ORDER-200", StopOrderId: "STOP-200", TargetOrderId: null,
                FillPrice: 11.00m, FillQuantity: 2, FillAmount: 2200m,
                Status: OrderStatus.Filled, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.ReplaceTrailStopAsync(
                "STOP-200", It.IsAny<int>(), It.IsAny<TradeOrder>(), 25.0, default))
            .ReturnsAsync("STOP-200-TIGHT");

        var service = BuildService(options);
        var alert   = BuildAlert(pricePaid: 3.70m);

        await service.HandleEntryAsync(alert, CallClassification());

        var trade = _guard.GetOpenTrades().FirstOrDefault();
        trade.Should().NotBeNull();
        trade!.StopPrice.Should().BeApproximately(8.25m, 0.01m);
        trade.TargetPrice.Should().BeApproximately(33.00m, 0.05m);
    }

    [Fact]
    public async Task HandleEntryAsync_NbboRejection_DoesNotRecordGhost()
    {
        // IbkrEWrapper now stores NBBO_REJECTION and resolves the TCS immediately.
        // IbkrBrokerService returns Cancelled with that reason from the early-cancel path.
        // ExecuteBrokerEntryAsync treats Cancelled as rejected when reason is not PRICE_PROTECTION.
        // Verifies no position is recorded and GetCurrentPositionPriceAsync is never called.
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Cancelled, FilledAt: DateTimeOffset.UtcNow,
                RejectionReason: "NBBO_REJECTION"));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().BeEmpty();
        _brokerMock.Verify(
            b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default),
            Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_LimitTimeout_NoPositionConfirmed_DoesNotRecordGhost()
    {
        // After the fill window OCE and the rejection-reason retry loop both yield nothing,
        // IbkrBrokerService queries reqPositions. When the position is absent it returns
        // Rejected rather than Pending, preventing VerifyPendingFillAsync from running and
        // recording an estimated fill ghost.
        _brokerMock
            .Setup(b => b.PlaceOrderAsync(It.IsAny<TradeOrder>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 0m, FillQuantity: 0, FillAmount: 0m,
                Status: OrderStatus.Rejected, FilledAt: DateTimeOffset.UtcNow,
                RejectionReason: "Cancelled — no position confirmed after fill window"));

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        await _executionMarketOpen.HandleEntryAsync(alert, CallClassification());

        _guard.GetOpenTrades().Should().BeEmpty();
        _brokerMock.Verify(
            b => b.GetCurrentPositionPriceAsync(It.IsAny<TradeRecord>(), default),
            Times.Never);
    }

    [Fact]
    public async Task HandleExitAsync_CloseTimeout_PositionAbsent_RecordsClose()
    {
        // ClosePositionAsync returns Pending (both callbacks timed out).
        // GetAllPositionsAsync returns an empty non-timed-out snapshot, position is gone.
        // VerifyCloseExecutedAsync confirms the close executed and returns Filled.
        // HandleExitAsync should record the close in TradeGuard.
        var order = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(), UserName: "TestTrader", Symbol: "TSLA",
            TradeType: TradeType.Options, OptionsContractSymbol: "TSLA260620C00450000",
            Direction: "call", Strike: 450, Expiration: "2026-06-20",
            Quantity: 2, EstimatedEntryPrice: 4.95m, BudgetUsed: 990m,
            StopPrice: 2.48m, TargetPrice: 14.85m, TrailPercent: 50.0);

        _guard.RegisterOpen(order, new BrokerOrderResult(
            OrderId: "ORDER-FC1", StopOrderId: "STOP-FC1", TargetOrderId: null,
            FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
            Status: OrderStatus.Filled, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.ClosePositionAsync(It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "CLOSE-FC1", StopOrderId: null, TargetOrderId: null,
                FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        // Empty list, TimedOut=false: position confirmed absent from IBKR
        _brokerMock
            .Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PositionsSnapshot([], false));

        var exitAlert = BuildAlert(side: "stc", userName: "TestTrader");
        await _executionMarketOpen.HandleExitAsync(exitAlert);

        _guard.GetOpenTrades().Should().BeEmpty("close was confirmed via reqPositions");
    }

    [Fact]
    public async Task HandleExitAsync_CloseTimeout_PositionPresent_LeavesOpen()
    {
        // ClosePositionAsync returns Pending.
        // GetAllPositionsAsync returns a snapshot containing the position, close did not execute.
        // HandleExitAsync should revert and leave the position open.
        var order = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(), UserName: "TestTrader", Symbol: "TSLA",
            TradeType: TradeType.Options, OptionsContractSymbol: "TSLA260620C00450000",
            Direction: "call", Strike: 450, Expiration: "2026-06-20",
            Quantity: 2, EstimatedEntryPrice: 4.95m, BudgetUsed: 990m,
            StopPrice: 2.48m, TargetPrice: 14.85m, TrailPercent: 50.0);

        _guard.RegisterOpen(order, new BrokerOrderResult(
            OrderId: "ORDER-FC2", StopOrderId: "STOP-FC2", TargetOrderId: null,
            FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
            Status: OrderStatus.Filled, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.ClosePositionAsync(It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "CLOSE-FC2", StopOrderId: null, TargetOrderId: null,
                FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        // Position still present in IBKR
        _brokerMock
            .Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PositionsSnapshot(
                [new IbkrPosition("TSLA", "OPT", "TSLA260620C00450000", 2, 4.95m)], false));

        var exitAlert = BuildAlert(side: "stc", userName: "TestTrader");
        await _executionMarketOpen.HandleExitAsync(exitAlert);

        _guard.GetOpenTrades().Should().HaveCount(1, "position still open at IBKR, not safe to record close");
    }

    [Fact]
    public async Task HandleExitAsync_CloseTimeout_GatewayDegraded_LeavesOpen()
    {
        // ClosePositionAsync returns Pending.
        // GetAllPositionsAsync returns TimedOut=true, Gateway did not respond.
        // Cannot confirm whether close executed, so HandleExitAsync must leave position open.
        var order = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(), UserName: "TestTrader", Symbol: "TSLA",
            TradeType: TradeType.Options, OptionsContractSymbol: "TSLA260620C00450000",
            Direction: "call", Strike: 450, Expiration: "2026-06-20",
            Quantity: 2, EstimatedEntryPrice: 4.95m, BudgetUsed: 990m,
            StopPrice: 2.48m, TargetPrice: 14.85m, TrailPercent: 50.0);

        _guard.RegisterOpen(order, new BrokerOrderResult(
            OrderId: "ORDER-FC3", StopOrderId: "STOP-FC3", TargetOrderId: null,
            FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
            Status: OrderStatus.Filled, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.ClosePositionAsync(It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "CLOSE-FC3", StopOrderId: null, TargetOrderId: null,
                FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        // TimedOut=true: Gateway degraded, cannot determine close status
        _brokerMock
            .Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PositionsSnapshot([], true));

        var exitAlert = BuildAlert(side: "stc", userName: "TestTrader");
        await _executionMarketOpen.HandleExitAsync(exitAlert);

        _guard.GetOpenTrades().Should().HaveCount(1, "Gateway timeout means close status is unknown");
    }

    [Fact]
    public async Task ForceCloseAsync_CloseTimeout_PositionAbsent_ReturnsClosed()
    {
        // ClosePositionAsync returns Pending.
        // GetAllPositionsAsync confirms position absent, close executed.
        // ForceCloseAsync should record the close and return ForceCloseOutcome.Closed.
        var order = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(), UserName: "TestTrader", Symbol: "TSLA",
            TradeType: TradeType.Options, OptionsContractSymbol: "TSLA260620C00450000",
            Direction: "call", Strike: 450, Expiration: "2026-06-20",
            Quantity: 2, EstimatedEntryPrice: 4.95m, BudgetUsed: 990m,
            StopPrice: 2.48m, TargetPrice: 14.85m, TrailPercent: 50.0);

        _guard.RegisterOpen(order, new BrokerOrderResult(
            OrderId: "ORDER-FC4", StopOrderId: "STOP-FC4", TargetOrderId: null,
            FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
            Status: OrderStatus.Filled, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.ClosePositionAsync(It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), default))
            .ReturnsAsync(new BrokerOrderResult(
                OrderId: "CLOSE-FC4", StopOrderId: null, TargetOrderId: null,
                FillPrice: 4.95m, FillQuantity: 2, FillAmount: 990m,
                Status: OrderStatus.Pending, FilledAt: DateTimeOffset.UtcNow));

        _brokerMock
            .Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PositionsSnapshot([], false));

        var trade = _guard.FindOpenTrade("TestTrader", "TSLA260620C00450000", "TSLA")!;
        var outcome = await _executionMarketOpen.ForceCloseAsync(trade, TradeOutcome.ForcedClose);

        outcome.Should().Be(ForceCloseOutcome.Closed);
        _guard.GetOpenTrades().Should().BeEmpty();
    }

    // -- Options alert staleness check --

    [Fact]
    public async Task HandleEntryAsync_OptionsAlertStaleness_ExceedsThreshold_Skips()
    {
        // OptionsAlertStalenessMaxSlippagePct = 8%. PricePaid is 12% above ActualPriceAtTimeOfAlert.
        // The options-specific threshold fires and blocks the entry before PlaceOrderAsync is called.
        var service = BuildService(new RiskEngineOptions
        {
            OptionsAlertStalenessMaxSlippagePct = 8.0m,
        });

        var alert = BuildAlert(
            pricePaid: 5.60m,
            actualPriceAtTimeOfAlert: 5.00m);

        await service.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task HandleEntryAsync_OptionsAlertStaleness_WithinThreshold_Proceeds()
    {
        // OptionsAlertStalenessMaxSlippagePct = 8%. PricePaid is 5% above, within threshold.
        var service = BuildService(new RiskEngineOptions
        {
            OptionsAlertStalenessMaxSlippagePct = 8.0m,
        });

        var alert = BuildAlert(
            pricePaid: 5.25m,
            actualPriceAtTimeOfAlert: 5.00m);

        await service.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_OptionsAlertStaleness_NullActualPrice_Proceeds()
    {
        // When ActualPriceAtTimeOfAlert is null the staleness check is skipped regardless
        // of OptionsAlertStalenessMaxSlippagePct. The limit order provides price protection.
        var service = BuildService(new RiskEngineOptions
        {
            OptionsAlertStalenessMaxSlippagePct = 8.0m,
        });

        var alert = BuildAlert(
            pricePaid: 10.00m,
            actualPriceAtTimeOfAlert: null);

        await service.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleEntryAsync_OptionsAlertStaleness_Disabled_FallsBackToGeneral()
    {
        // OptionsAlertStalenessMaxSlippagePct = 0 disables the options gate. Falls back to
        // AlertStalenessMaxSlippagePct (25%). 12% staleness is within the 25% fallback.
        var service = BuildService(new RiskEngineOptions
        {
            OptionsAlertStalenessMaxSlippagePct = 0m,
            AlertStalenessMaxSlippagePct        = 25.0m,
        });

        var alert = BuildAlert(
            pricePaid: 5.60m,
            actualPriceAtTimeOfAlert: 5.00m);

        await service.HandleEntryAsync(alert, CallClassification());

        _brokerMock.Verify(b => b.PlaceOrderAsync(
            It.IsAny<TradeOrder>(), default), Times.Once);
    }
}