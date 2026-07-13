using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for StartupReconciliationService.
/// Covers all four reconciliation steps and top-level RunAsync behaviour.
/// All external dependencies are mocked via Moq; TradeGuard and
/// DiscordNotificationService are real instances with no external calls.
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

        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(100_000m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(0m);

        // Default: IBKR confirms empty account (not a timeout)
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([], false));

        // Default: no open orders
        broker.Setup(b => b.GetAllOpenOrdersAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrdersSnapshot([], false));

        // Default: no orders are known (none placed by Vela this session)
        broker.Setup(b => b.IsKnownOrder(It.IsAny<int>()))
              .Returns(false);

        // Default successful cover result
        broker.Setup(b => b.ClosePositionAsync(
                It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new BrokerOrderResult(
                  "COVER", null, null, 1m, 1, 100m, OrderStatus.Filled, DateTimeOffset.UtcNow));

        // Default: no DB positions
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        repo.Setup(r => r.SaveAsync(It.IsAny<OpenPosition>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var guard = new TradeGuard(
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

    private static OpenPosition StockDbPosition(string symbol, string orderId, int qty = 3) =>
        new()
        {
            OrderId     = orderId,
            Symbol      = symbol,
            TradeType   = "Stock",
            Quantity    = qty,
            EntryPrice  = 10m,
            EntryAmount = qty * 10m,
            StopPrice   = 8m,
            TargetPrice = 20m,
            OpenedAt    = DateTimeOffset.UtcNow,
            IsManual    = false,
        };

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
            IsManual        = false,
        };

    private static IbkrPosition StockPos(string symbol, int qty) =>
        new(symbol, "STK", null, qty, 10m);

    private static IbkrPosition OptionsPos(string symbol, string localSymbol, int qty) =>
        new(symbol, "OPT", localSymbol, qty, 2m);

    // -- RunAsync top-level behaviour --

    [Fact]
    public async Task RunAsync_WhenGetAllPositionsTimesOut_AbortsWithoutModifyingDb()
    {
        var (svc, broker, repo) = BuildService();

        // TimedOut=true: Gateway did not respond — cannot trust any state
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([], true));

        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906")]);

        await svc.RunAsync();

        // Must not touch DB or place orders — we don't know the real account state
        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.GetAllAsync(
            It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenIbkrReportsEmptyButDbHasPositions_SkipsWithoutDeletingDb()
    {
        // IBKR returns empty with TimedOut=false, but DB has an open position — this is not
        // proof of a flat account, Gateway may have returned a stale empty response. Must not
        // treat the DB position as a confirmed ghost.
        var (svc, broker, repo) = BuildService();

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
    public async Task RunAsync_WhenIbkrAndDbBothReportFlat_ProceedsWithoutError()
    {
        // IBKR returns empty and DB agrees the account is flat — genuinely nothing to reconcile.
        var (svc, broker, repo) = BuildService();

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenExceptionThrown_DoesNotPropagate()
    {
        var (svc, broker, _) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("Gateway unavailable"));

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();
    }

    // -- Step 2: VerifyDbPositionsAsync --

    [Fact]
    public async Task VerifyDbPositions_WhenPositionNotInIbkr_DeletesFromRepo()
    {
        var (svc, broker, repo) = BuildService();

        // IBKR holds AAPL but not TSLA — TSLA in DB is a ghost
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("AAPL", 5)], false));
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("TSLA", "2906")]);

        await svc.RunAsync();

        repo.Verify(r => r.DeleteAsync("2906", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyDbPositions_WhenIbkrQtyIsZero_DeletesFromRepo()
    {
        var (svc, broker, repo) = BuildService();

        // IBKR shows zero qty — position already closed at IBKR
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("TSLA", 0)], false));
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
              .ReturnsAsync(new PositionsSnapshot([StockPos("TSLA", 2)], false)); // IBKR: 2
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
              .ReturnsAsync(new PositionsSnapshot([StockPos("TSLA", 3)], false));
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
              .ReturnsAsync(new PositionsSnapshot([StockPos("TSLA", 3)], false));

        // repo.GetAllAsync returns [] by default

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
              .ReturnsAsync(new PositionsSnapshot(
                  [OptionsPos("TSLA", "TSLA  260620C00250000", 2)], false));
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([OptionsDbPosition("TSLA", "2906", occ, qty: 2)]);

        await svc.RunAsync();

        // Spaces stripped on both sides, should match — position stays intact
        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Step 1: CoverShortsAsync --

    [Fact]
    public async Task CoverShorts_WhenStockShortDetected_PlacesMarketBuyWithNullOptionsContract()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("TSLA", -3)], false));

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
              .ReturnsAsync(new PositionsSnapshot(
                  [OptionsPos("TSLA", localSymbol, -2)], false));

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
              .ReturnsAsync(new PositionsSnapshot(
                  [StockPos("TSLA", 3), StockPos("AAPL", 5)], false));

        await svc.RunAsync();

        broker.Verify(b => b.ClosePositionAsync(
            It.IsAny<TradeRecord>(), It.IsAny<TradeOutcome>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -- Step 3: DetectManualPositionsAsync --

    [Fact]
    public async Task DetectManualPositions_WhenUntrackedLongExists_CreatesManualRecord()
    {
        var (svc, broker, repo) = BuildService();

        // IBKR has AMD but Vela has no record of it — manual trade
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("AMD", 5)], false));

        // DB is empty — AMD not tracked at all
        await svc.RunAsync();

        repo.Verify(r => r.SaveAsync(
            It.Is<OpenPosition>(p =>
                p.Symbol   == "AMD" &&
                p.IsManual == true  &&
                p.UserName == "MANUAL"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectManualPositions_WhenLongAlreadyTrackedInDb_DoesNotCreateDuplicate()
    {
        var (svc, broker, repo) = BuildService();

        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("AMD", 5)], false));

        // DB already has AMD (managed position) — no manual record needed
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([StockDbPosition("AMD", "9001", qty: 5)]);

        await svc.RunAsync();

        repo.Verify(r => r.SaveAsync(
            It.IsAny<OpenPosition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectManualPositions_WhenIbkrHasOnlyShorts_DoesNotCreateManualRecord()
    {
        var (svc, broker, repo) = BuildService();

        // Shorts are excluded from manual detection (handled by CoverShortsAsync)
        broker.Setup(b => b.GetAllPositionsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PositionsSnapshot([StockPos("AMD", -3)], false));

        await svc.RunAsync();

        repo.Verify(r => r.SaveAsync(
            It.IsAny<OpenPosition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Step 4: ClassifyOpenOrdersAsync --

    [Fact]
    public async Task ClassifyOrders_WhenAllOrdersAreKnown_DoesNotThrow()
    {
        var (svc, broker, _) = BuildService();

        broker.Setup(b => b.GetAllOpenOrdersAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrdersSnapshot(
                  [new IbkrOpenOrder(1001, "TSLA", "OPT", null, "SELL", "TRAIL", 2, "PreSubmitted", null, null)],
                  false));
        broker.Setup(b => b.IsKnownOrder(1001)).Returns(true);

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task ClassifyOrders_WhenOrdersRequestTimesOut_DoesNotThrow()
    {
        var (svc, broker, _) = BuildService();

        broker.Setup(b => b.GetAllOpenOrdersAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrdersSnapshot([], true));

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task ClassifyOrders_WhenUnknownOrderExists_DoesNotThrow()
    {
        var (svc, broker, _) = BuildService();

        broker.Setup(b => b.GetAllOpenOrdersAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrdersSnapshot(
                  [new IbkrOpenOrder(9999, "AMD", "STK", null, "BUY", "MKT", 10, "Submitted", null, null)],
                  false));
        broker.Setup(b => b.IsKnownOrder(9999)).Returns(false);

        await svc.Invoking(s => s.RunAsync()).Should().NotThrowAsync();
    }
}