using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Data;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

/// <summary>
/// Unit tests for StartupReconciliationService.
/// Covers the two reconciliation steps (VerifyDbPositions, CoverShorts) and
/// the top-level RunAsync behaviour. All external dependencies are mocked
/// IBrokerService and IOpenPositionRepository via Moq, TradeGuard and
/// DiscordNotificationService as real instances with no external calls.
/// </summary>
public class StartupReconciliationServiceTests
{
    // -- Helpers --

    private static (
        StartupReconciliationService Svc,
        Mock<IBrokerService> Broker,
        Mock<IOpenPositionRepository> Repo)
        BuildService()
    {
        var broker = new Mock<IBrokerService>();
        var repo   = new Mock<IOpenPositionRepository>();

        // TradeGuard requires balance / exposure calls from broker
        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(100_000m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(0m);

        // Default empty positions, individual tests override as needed
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);

        // Default successful cover result
        broker.Setup(b => b.ClosePositionAsync(
                It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new BrokerOrderResult(
                  "COVER", null, null, 1m, 1, 100m, OrderStatus.Filled, DateTimeOffset.UtcNow));

        // Default empty DB, individual tests override as needed
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var guard   = new TradeGuard(
            broker.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);

        var discord = new DiscordNotificationService(
            NullLogger<DiscordNotificationService>.Instance);

        var svc = new StartupReconciliationService(
            broker.Object, repo.Object, guard, discord,
            NullLogger<StartupReconciliationService>.Instance);

        return (svc, broker, repo);
    }

    // Minimal stock DB position
    private static OpenPosition StockDbPosition(string symbol, string orderId, int qty = 3) =>
        new()
        {
            OrderId      = orderId,
            Symbol       = symbol,
            TradeType    = "Stock",
            Quantity     = qty,
            EntryPrice   = 10m,
            EntryAmount  = qty * 10m,
            StopPrice    = 8m,
            TargetPrice  = 20m,
            OpenedAt     = DateTimeOffset.UtcNow,
        };

    // Minimal options DB position
    private static OpenPosition OptionsDbPosition(
        string symbol, string orderId, string occ, int qty = 2) =>
        new()
        {
            OrderId         = orderId,
            Symbol          = symbol,
            TradeType       = "Options",
            OptionsContract = occ,
            Quantity        = qty,
            EntryPrice      = 2m,
            EntryAmount     = qty * 200m,
            StopPrice       = 1m,
            TargetPrice     = 6m,
            OpenedAt        = DateTimeOffset.UtcNow,
        };

    private static IbkrPosition StockPos(string symbol, int qty) =>
        new(symbol, "STK", null, qty, 10m);

    private static IbkrPosition OptionsPos(string symbol, string localSymbol, int qty) =>
        new(symbol, "OPT", localSymbol, qty, 2m);

    // -- Step 1: VerifyDbPositionsAsync --

    [Fact]
    public async Task VerifyDbPositions_WhenPositionNotInIbkr_DeletesFromRepo()
    {
        var (svc, broker, repo) = BuildService();

        // IBKR holds AAPL but not TSLA: TSLA in DB is a ghost
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("AAPL", 5)]);
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906")]);

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync("2906", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyDbPositions_WhenIbkrQtyIsZero_DeletesFromRepo()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", 0)]); // IBKR shows zero, position already closed
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906")]);

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync("2906", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyDbPositions_WhenQtyMismatch_UpdatesToIbkrQty()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", 2)]); // IBKR: 2
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906", qty: 3)]); // DB: 3

        await svc.RunAsync();

        repo.Verify(r => r.UpdateQuantityAsync("2906", 2, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyDbPositions_WhenQtyMatches_MakesNoChanges()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", 3)]);
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906", qty: 3)]);

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.UpdateQuantityAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyDbPositions_WhenDbIsEmpty_SkipsVerification()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", 3)]);

        // repo.GetAllAsync left on default empty list

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.UpdateQuantityAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyDbPositions_OptionsContract_StripsIbkrPaddingBeforeMatching()
    {
        var (svc, broker, repo) = BuildService();
        var occ = "TSLA260620C00250000";

        // IBKR pads the root symbol to 6 chars: "TSLA  260620C00250000"
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([OptionsPos("TSLA", "TSLA  260620C00250000", 2)]);
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([OptionsDbPosition("TSLA", "2906", occ, qty: 2)]);

        await svc.RunAsync();

        // Spaces stripped on both sides, should match and leave position intact
        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Step 2: CoverShortsAsync --

    [Fact]
    public async Task CoverShorts_WhenStockShortDetected_PlacesMarketBuyWithNullOptionsContract()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", -3)]); // Short!

        await svc.RunAsync();

        broker.Verify(b => b.ClosePositionAsync(
            It.Is<TradeRecord>(t =>
                t.Symbol          == "TSLA" &&
                t.Quantity        == 3 &&
                t.OptionsContract == null &&
                t.TradeType       == TradeType.Stock),
            TradeOutcome.ForcedClose,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CoverShorts_WhenOptionsShortDetected_PlacesMarketBuyWithLocalSymbol()
    {
        var (svc, broker, repo) = BuildService();
        var localSymbol = "TSLA  260620C00250000";

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([OptionsPos("TSLA", localSymbol, -2)]);

        await svc.RunAsync();

        broker.Verify(b => b.ClosePositionAsync(
            It.Is<TradeRecord>(t =>
                t.Symbol          == "TSLA" &&
                t.Quantity        == 2 &&
                t.OptionsContract == localSymbol &&
                t.TradeType       == TradeType.Options),
            TradeOutcome.ForcedClose,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CoverShorts_WhenNoShortsDetected_PlacesNoOrders()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([StockPos("TSLA", 3), StockPos("AAPL", 5)]);

        await svc.RunAsync();

        broker.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -- RunAsync top-level behaviour --

    [Fact]
    public async Task RunAsync_WhenIbkrReturnsNoPositions_SkipsBothSteps()
    {
        var (svc, broker, repo) = BuildService();

        // IBKR returns empty, default setup, no override needed
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906")]);

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenExceptionThrown_DoesNotPropagate()
    {
        var (svc, broker, _) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("Gateway unavailable"));

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();
    }
}