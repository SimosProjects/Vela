using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

public class TradeGuardTests
{
    private readonly Mock<IBrokerService> _brokerMock = new();
    private readonly TradeGuard _guard;

    public TradeGuardTests()
    {
        _brokerMock.Setup(b => b.GetAccountBalanceAsync(default))
            .ReturnsAsync(100_000m);
        _brokerMock.Setup(b => b.GetOpenPositionsValueAsync(default))
            .ReturnsAsync(0m);

        var riskOptions = Options.Create(new RiskEngineOptions());
        _guard = new TradeGuard(_brokerMock.Object, riskOptions, NullLogger<TradeGuard>.Instance);
    }

    private static TradeOrder BuildOrder(
        string symbol = "TSLA",
        string? contractSymbol = "TSLA260620C00450000",
        decimal budgetUsed = 1_000m,
        bool isAverage = false) =>
        new(
            AlertId: Guid.NewGuid().ToString(),
            UserName: "TestTrader",
            Symbol: symbol,
            TradeType: TradeType.Options,
            OptionsContractSymbol: contractSymbol,
            Direction: "call",
            Strike: 450,
            Expiration: "2026-06-20",
            Quantity: 2,
            EstimatedEntryPrice: 4.95m,
            BudgetUsed: budgetUsed,
            StopPrice: 2.48m,
            TargetPrice: 14.85m,
            TrailPercent: 50.0,
            IsAverage: isAverage);

    private static BrokerOrderResult BuildResult(string orderId = "ORDER-001") =>
        new(
            OrderId: orderId,
            StopOrderId: "STOP-001",
            TargetOrderId: "TGT-001",
            FillPrice: 4.95m,
            FillQuantity: 2,
            FillAmount: 990m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task CheckAsync_AllowsValidOrder()
    {
        var order = BuildOrder();
        var result = await _guard.CheckAsync(order);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_BlocksDuplicateOpenPosition()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        result.Should().Contain("Max positions per symbol reached");
    }

    [Fact]
    public async Task CheckAsync_BlocksWhenBudgetExceedsAvailableCapital()
    {
        _guard.SetCacheForTesting(balance: 100m, openValue: 95m);

        var order = BuildOrder(budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        // Either the cap or the balance check fires depending on which limit is hit first
        result.Should().MatchRegex("Daily exposure cap|Insufficient");
    }

    [Fact]
    public async Task CheckAsync_BlocksWhenDailyExposureCapReached()
    {
        // Balance $100,000, cap 30% = $30,000 max deployment
        // Register $29,500 worth of trades opened today — only $500 deployable, order needs $1,000
        _guard.SetCacheForTesting(balance: 100_000m, openValue: 29_500m);

        for (var i = 0; i < 29; i++)
        {
            var o = BuildOrder(
                symbol: $"TICK{i}",
                contractSymbol: $"TICK{i}260620C00100000",
                budgetUsed: 1_000m);
            _guard.RegisterOpen(o, new BrokerOrderResult(
                OrderId: $"ORDER-{i:D3}",
                StopOrderId: null,
                TargetOrderId: null,
                FillPrice: 4.95m,
                FillQuantity: 2,
                FillAmount: 1_000m,
                Status: OrderStatus.Filled,
                FilledAt: DateTimeOffset.UtcNow));
        }

        var order = BuildOrder(symbol: "NEW", contractSymbol: "NEW260620C00100000", budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        result.Should().Contain("Daily exposure cap");
    }

    [Fact]
    public async Task CheckAsync_AllowsWhenUnderExposureCap()
    {
        // Balance $100,000, cap 30% = $30,000 max deployment
        // Open $10,000 — $20,000 still deployable, order needs $1,000
        _guard.SetCacheForTesting(balance: 100_000m, openValue: 10_000m);

        var order = BuildOrder(budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_AllowsAveragingWhenNotYetAveraged()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var avgOrder = BuildOrder(isAverage: true);
        var result = await _guard.CheckAsync(avgOrder);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_BlocksSecondAverage()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var avgOrder = BuildOrder(isAverage: true);
        _guard.RegisterOpen(avgOrder, BuildResult("ORDER-002"));

        var result = await _guard.CheckAsync(avgOrder);

        result.Should().NotBeNull();
        result.Should().Contain("Already averaged");
    }

    [Fact]
    public void RegisterClose_PopulatesExitData()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var closed = _guard.RegisterClose(
            "TestTrader", "TSLA260620C00450000", "TSLA", 8.20m, TradeOutcome.XtradesExit);

        closed.Should().NotBeNull();
        closed!.ExitPrice.Should().Be(8.20m);
        closed.PnL.Should().BePositive();
        closed.Result.Should().Be(TradeOutcome.XtradesExit);
        closed.Status.Should().Be(TradeStatus.Closed);
    }

    [Fact]
    public void GetOpenTrades_ReturnsAllOpenPositions()
    {
        var order1 = BuildOrder(symbol: "TSLA", contractSymbol: "TSLA260620C00450000");
        var order2 = BuildOrder(symbol: "AAPL", contractSymbol: "AAPL260620C00200000");

        _guard.RegisterOpen(order1, BuildResult("ORDER-001"));
        _guard.RegisterOpen(order2, BuildResult("ORDER-002"));

        var openTrades = _guard.GetOpenTrades();

        openTrades.Should().HaveCount(2);
        openTrades.Should().Contain(t => t.Symbol == "TSLA");
        openTrades.Should().Contain(t => t.Symbol == "AAPL");
    }

    [Fact]
    public void FindOpenTrade_ReturnsNullWhenNoMatch()
    {
        var result = _guard.FindOpenTrade("TestTrader", "TSLA260620C00450000", "TSLA");

        result.Should().BeNull();
    }

    [Fact]
    public void FindOpenTrade_ReturnsTradeAfterRegisterOpen()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var result = _guard.FindOpenTrade("TestTrader", "TSLA260620C00450000", "TSLA");

        result.Should().NotBeNull();
        result!.Symbol.Should().Be("TSLA");
    }

    [Fact]
    public async Task CheckAsync_BlocksSameSymbolDifferentInstrument()
    {
        var stockOrder = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(),
            UserName: "TestTrader",
            Symbol: "TSLA",
            TradeType: TradeType.Stock,
            OptionsContractSymbol: null,
            Direction: null,
            Strike: null,
            Expiration: null,
            Quantity: 18,
            EstimatedEntryPrice: 165.00m,
            BudgetUsed: 2_970m,
            StopPrice: 140.25m,
            TargetPrice: 214.50m,
            TrailPercent: 15.0);

        _guard.RegisterOpen(stockOrder, new BrokerOrderResult(
            OrderId: "ORDER-STK-001",
            StopOrderId: null,
            TargetOrderId: null,
            FillPrice: 165.00m,
            FillQuantity: 18,
            FillAmount: 2_970m,
            Status: OrderStatus.Filled,
            FilledAt: DateTimeOffset.UtcNow));

        var optionOrder = new TradeOrder(
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

        var result = await _guard.CheckAsync(optionOrder);

        result.Should().NotBeNull();
        result.Should().Contain("TSLA");
        result.Should().Contain("Max positions per symbol reached");
    }
}