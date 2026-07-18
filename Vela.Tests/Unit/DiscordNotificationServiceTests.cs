using Vela.Worker.Formatting;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class DiscordNotificationServiceTests
{
    [Fact]
    public void BuildSnapshotMessage_GoodAndBadPosition_FormatsCorrectly()
    {
        var account = new AccountSnapshot(
            NetLiquidation: 127843m,
            TotalCash:      42615m,
            BuyingPower:    84230m,
            TodayPnL:       623m,
            TimedOut:       false);

        var positions = new List<IbkrPosition>
        {
            new("NVDA", "STK", null, 100, 174.23m),   // good — has both orders below
            new("AAPL", "STK", null, 50,  208.15m),   // bad — no stop order at all
        };

        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
            new(2, "NVDA", "STK", null, "SELL", "LMT",   100, "Submitted", null,   188.00),
            // AAPL intentionally has no matching order at all
        };

        var message = IbSnapshotFormatter.BuildSnapshotMessage(account, positions, orders);

        message.Should().Contain("Net Liquidation: $127,843");
        message.Should().Contain("Today's P&L: +$623");
        message.Should().Contain("NVDA");
        message.Should().Contain("✓ Stop Loss");
        message.Should().Contain("SELL 100 @ 166.50");
        message.Should().Contain("✓ Take Profit");
        message.Should().Contain("SELL 100 @ 188.00");
        message.Should().Contain("AAPL");
        message.Should().Contain("⚠️ NO STOP LOSS FOUND");
        message.Should().NotContain("NO TAKE PROFIT");
    }

    [Fact]
    public void SplitSnapshotIntoChunks_ManyPositions_SplitsOnPositionBoundariesWithoutCuttingAny()
    {
        var account = new AccountSnapshot(
            NetLiquidation: 500000m,
            TotalCash:      100000m,
            BuyingPower:    900000m,
            TodayPnL:       1500m,
            TimedOut:       false);

        var positions = Enumerable.Range(0, 40)
            .Select(i => new IbkrPosition($"SYM{i:D3}", "STK", null, 10 + i, 100m + i))
            .ToList();

        var message = IbSnapshotFormatter.BuildSnapshotMessage(account, positions, new List<IbkrOpenOrder>());

        var chunks = DiscordNotificationService.SplitSnapshotIntoChunks(message);

        chunks.Count.Should().BeGreaterThan(1);

        foreach (var chunk in chunks)
            (chunk.Length + 8).Should().BeLessOrEqualTo(2000);

        foreach (var i in Enumerable.Range(0, 40))
        {
            var symbol = $"SYM{i:D3}";
            chunks.Count(c => c.Contains(symbol)).Should().Be(1);
        }

        var reconstructed = string.Join($"\n{IbSnapshotFormatter.PositionSeparator}\n", chunks);
        reconstructed.Should().Be(message);
    }
}
