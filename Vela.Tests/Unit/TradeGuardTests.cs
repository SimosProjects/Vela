using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Engine;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

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

        _guard = BuildGuard();
    }

    private TradeGuard BuildGuard(int maxPositionsPerSymbol = 1, int maxOptions = 0, int maxStocks = 0)
    {
        var options = Options.Create(new RiskEngineOptions
        {
            MaxPositionsPerSymbol        = maxPositionsPerSymbol,
            MaxOptionsPositionsPerSymbol = maxOptions,
            MaxStockPositionsPerSymbol   = maxStocks,
        });
        return new TradeGuard(_brokerMock.Object, options, NullLogger<TradeGuard>.Instance);
    }

    private static TradeOrder BuildOrder(
        string symbol = "TSLA",
        string? contractSymbol = "TSLA260620C00450000",
        decimal budgetUsed = 1_000m,
        bool isAverage = false,
        string userName = "TestTrader") =>
        new(
            AlertId: Guid.NewGuid().ToString(),
            UserName: userName,
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
        result!.Reason.Should().Contain("already open");
        result.IsRoutine.Should().BeFalse("a confirmed open contract is a real cap, not a concurrent-path duplicate");
    }

    // -- Exact contract deduplication --

    [Fact]
    public async Task CheckAsync_BlocksDuplicateContract_DifferentTrader()
    {
        var firstOrder = BuildOrder(userName: "Trader1");
        _guard.RegisterOpen(firstOrder, BuildResult("ORDER-001"));

        var secondOrder = BuildOrder(userName: "Trader2");
        var result = await _guard.CheckAsync(secondOrder);

        result.Should().NotBeNull();
        result!.Reason.Should().Contain("already open");
        result.IsRoutine.Should().BeFalse("a confirmed open contract blocks another trader non-routinely");
    }

    [Fact]
    public async Task CheckAsync_AllowsDifferentContractWhenUnderSymbolCap()
    {
        var guard = BuildGuard(maxPositionsPerSymbol: 3);
        guard.SetCacheForTesting(100_000m, 0m);

        var firstOrder = BuildOrder(contractSymbol: "TSLA260620C00450000");
        guard.RegisterOpen(firstOrder, BuildResult("ORDER-001"));

        var secondOrder = BuildOrder(contractSymbol: "TSLA260620C00500000");
        var result = await guard.CheckAsync(secondOrder);

        result.Should().BeNull("different contracts on the same underlying are allowed under the cap");
    }

    [Fact]
    public async Task CheckAsync_BlocksDuplicateContract_ViaPendingReservation()
    {
        var order = BuildOrder();

        var firstBlock = await _guard.CheckAsync(order);
        firstBlock.Should().BeNull("first path should pass");

        var secondBlock = await _guard.CheckAsync(order);

        secondBlock.Should().NotBeNull();
        secondBlock!.IsRoutine.Should().BeTrue("pending contract reservation is an expected concurrent-path duplicate");
        secondBlock.Reason.Should().Contain("concurrent path");
    }

    // -- Per-type symbol cap --

    [Fact]
    public async Task CheckAsync_AllowsOptionWhenOnlyStockOpenOnSameSymbol()
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

        var optionOrder = BuildOrder(symbol: "TSLA", contractSymbol: "TSLA260620C00450000");
        var result = await _guard.CheckAsync(optionOrder);

        result.Should().BeNull("stock and options caps are independent — a stock position does not block an options entry");
    }

    [Fact]
    public async Task CheckAsync_BlocksSecondOptionsWhenCapReached_AllowsStockOnSameSymbol()
    {
        var guard = BuildGuard(maxPositionsPerSymbol: 1);
        guard.SetCacheForTesting(100_000m, 0m);

        var optionOrder = BuildOrder(symbol: "TSLA", contractSymbol: "TSLA260620C00450000");
        guard.RegisterOpen(optionOrder, BuildResult("OPT-001"));

        var secondOption = BuildOrder(
            symbol: "TSLA",
            contractSymbol: "TSLA260620C00500000",
            userName: "OtherTrader");
        var optionBlock = await guard.CheckAsync(secondOption);

        optionBlock.Should().NotBeNull();
        optionBlock!.Reason.Should().Contain("options").And.Contain("per symbol reached");

        var stockOrder = new TradeOrder(
            AlertId: Guid.NewGuid().ToString(),
            UserName: "TestTrader",
            Symbol: "TSLA",
            TradeType: TradeType.Stock,
            OptionsContractSymbol: null,
            Direction: null,
            Strike: null,
            Expiration: null,
            Quantity: 10,
            EstimatedEntryPrice: 165.00m,
            BudgetUsed: 1_650m,
            StopPrice: 140.25m,
            TargetPrice: 214.50m,
            TrailPercent: 15.0);

        var stockResult = await guard.CheckAsync(stockOrder);
        stockResult.Should().BeNull("stock cap is independent of the options cap");
    }

    // -- Exposure and balance checks --

    [Fact]
    public async Task CheckAsync_BlocksWhenBudgetExceedsAvailableCapital()
    {
        _guard.SetCacheForTesting(balance: 100m, openValue: 95m);

        var order = BuildOrder(budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().NotBeNull();
        result!.Reason.Should().MatchRegex("Daily exposure cap|Insufficient");
    }

    [Fact]
    public async Task CheckAsync_BlocksWhenDailyExposureCapReached()
    {
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
        result!.Reason.Should().Contain("Daily exposure cap");
    }

    [Fact]
    public async Task CheckAsync_AllowsWhenUnderExposureCap()
    {
        _guard.SetCacheForTesting(balance: 100_000m, openValue: 10_000m);

        var order = BuildOrder(budgetUsed: 1_000m);
        var result = await _guard.CheckAsync(order);

        result.Should().BeNull();
    }

    // -- Averaging --

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
        result!.Reason.Should().Contain("Already averaged");
    }

    // -- Concurrent path (routine duplicate) --

    [Fact]
    public async Task CheckAsync_RoutineBlock_WhenPendingReservationOnlyNoPosopen()
    {
        var order = BuildOrder();

        var firstBlock = await _guard.CheckAsync(order);
        firstBlock.Should().BeNull("first path should pass");

        var secondBlock = await _guard.CheckAsync(order);

        secondBlock.Should().NotBeNull();
        secondBlock!.IsRoutine.Should().BeTrue("pending-reservation duplicate is expected concurrent-path behaviour");
        secondBlock.Reason.Should().Contain("concurrent path");
    }

    // -- Close and find --

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

    // -- Single-closer election tests --

    [Fact]
    public void TryMarkClosing_ReturnsTrueForOpenPosition()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        var result = _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        result.Should().BeTrue();
    }

    [Fact]
    public void TryMarkClosing_ReturnsFalseWhenPositionNotFound()
    {
        var result = _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        result.Should().BeFalse("no position exists to close");
    }

    [Fact]
    public void TryMarkClosing_ReturnsFalseWhenAlreadyClosing()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");
        var secondAttempt = _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        secondAttempt.Should().BeFalse("concurrent path already claimed the close");
    }

    [Fact]
    public void RevertClosing_AllowsSubsequentTryMarkClosing()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");
        _guard.RevertClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        var retryResult = _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        retryResult.Should().BeTrue("closing mark was reverted — position available again");
    }

    [Fact]
    public void RegisterClose_ClearsClosingMark()
    {
        var order = BuildOrder();
        _guard.RegisterOpen(order, BuildResult());

        _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");
        _guard.RegisterClose("TestTrader", "TSLA260620C00450000", "TSLA", 8.20m, TradeOutcome.XtradesExit);

        var afterClose = _guard.TryMarkClosing("TestTrader", "TSLA260620C00450000", "TSLA");

        afterClose.Should().BeFalse("position no longer exists after close");
    }

    // -- Alert ID deduplication --

    [Fact]
    public async Task CheckAsync_DuplicateAlertId_ConcurrentPaths_BlocksSecondAsRoutine()
    {
        // First path passes CheckAsync and reserves the alert ID. Second path carrying the
        // same alert_id (polling + SignalR race on the same alert) is blocked as routine
        // before any contract or symbol checks run.
        var alertId = Guid.NewGuid().ToString();
        var order = BuildOrder() with { AlertId = alertId };

        var firstBlock = await _guard.CheckAsync(order);
        firstBlock.Should().BeNull("first path should pass");

        var secondOrder = BuildOrder(contractSymbol: "TSLA260620C00500000") with { AlertId = alertId };
        var secondBlock = await _guard.CheckAsync(secondOrder);

        secondBlock.Should().NotBeNull();
        secondBlock!.IsRoutine.Should().BeTrue("same alert_id on a concurrent path is an expected duplicate");
        secondBlock.Reason.Should().Contain(alertId);
    }

    [Fact]
    public async Task CheckAsync_AlertIdReleasedAfterReleaseReservation_AllowsRetry()
    {
        // After ReleaseReservation the alert_id slot is cleared. A subsequent call
        // with the same alert_id should pass (e.g. retry after a broker failure).
        var alertId = Guid.NewGuid().ToString();
        var order = BuildOrder() with { AlertId = alertId };

        await _guard.CheckAsync(order);
        _guard.ReleaseReservation(order);

        var retryOrder = BuildOrder(contractSymbol: "TSLA260620C00500000") with { AlertId = alertId };
        var retryBlock = await _guard.CheckAsync(retryOrder);

        retryBlock.Should().BeNull("alert_id was released — retry should be allowed");
    }

    [Fact]
    public async Task CheckAsync_NullAlertId_FallsThroughToSymbolChecks()
    {
        // When AlertId is null or empty, the alert_id dedup is skipped entirely
        // and normal symbol-based dedup still applies.
        var order = BuildOrder() with { AlertId = string.Empty };
        var firstBlock = await _guard.CheckAsync(order);
        firstBlock.Should().BeNull();

        var secondOrder = BuildOrder() with { AlertId = string.Empty };
        var secondBlock = await _guard.CheckAsync(secondOrder);

        secondBlock.Should().NotBeNull();
        secondBlock!.Reason.Should().Contain("concurrent path");
    }
}