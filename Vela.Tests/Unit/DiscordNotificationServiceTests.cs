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
}
