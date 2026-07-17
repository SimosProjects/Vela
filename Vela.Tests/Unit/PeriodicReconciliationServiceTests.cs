using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for PeriodicReconciliationService.CheckManagedPositionsAsync.
/// TradeGuard is a real instance seeded via LoadFromDatabase; the repository
/// and broker are mocked via Moq.
/// </summary>
public class PeriodicReconciliationServiceTests
{
    // -- Helpers --

    private static (
        PeriodicReconciliationService Svc,
        TradeGuard Guard,
        Mock<IOpenPositionRepository> Repo)
        BuildService()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(100_000m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(0m);

        var guard = new TradeGuard(
            broker.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);

        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);

        var repo = new Mock<IOpenPositionRepository>();
        repo.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped<IOpenPositionRepository>(_ => repo.Object);
        var scopeFactory = services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var svc = new PeriodicReconciliationService(
            broker.Object,
            guard,
            discord,
            scopeFactory,
            NullLogger<PeriodicReconciliationService>.Instance);

        return (svc, guard, repo);
    }

    private static OpenPosition StockDbPosition(string symbol, string orderId, int qty = 5) =>
        new()
        {
            OrderId     = orderId,
            UserName    = "Theo",
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

    private static IbkrPosition StockPos(string symbol, int qty) =>
        new(symbol, "STK", null, qty, 10m);

    // -- CheckManagedPositionsAsync --

    [Fact]
    public async Task CheckManagedPositions_WhenIbkrMatchHasZeroQty_TreatedAsMissLikeNoMatch()
    {
        var (svc, guard, repo) = BuildService();
        guard.LoadFromDatabase([StockDbPosition("TSLA", "9905")]);

        // IBKR still returns a row for TSLA, but qty is 0 — position is actually closed.
        // Two consecutive misses matches AutoCleanupAfterMisses, same as a fully-missing row.
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 0)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 0)], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync("9905", It.IsAny<CancellationToken>()), Times.Once);
        guard.GetOpenTrades().Should().BeEmpty();
    }

    [Fact]
    public async Task CheckManagedPositions_WhenIbkrMatchHasPositiveQty_ClearsMissStreak()
    {
        var (svc, guard, repo) = BuildService();
        guard.LoadFromDatabase([StockDbPosition("TSLA", "9905")]);

        // IBKR confirms the position is still open on every cycle — must never be removed.
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);
        await svc.CheckManagedPositionsAsync([StockPos("TSLA", 5)], CancellationToken.None);

        repo.Verify(r => r.DeleteAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        guard.GetOpenTrades().Should().ContainSingle(t => t.OrderId == "9905");
    }
}
