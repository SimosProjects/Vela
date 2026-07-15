using Vela.Worker.Formatting;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class IbSnapshotFormatterTests
{
    [Fact]
    public void BuildSnapshotMessage_ExcludesZeroQuantityPositions()
    {
        var account = new AccountSnapshot(
            NetLiquidation: 127843m,
            TotalCash:      42615m,
            BuyingPower:    84230m,
            TodayPnL:       623m,
            TimedOut:       false);

        var positions = new List<IbkrPosition>
        {
            new("NVDA", "STK", null, 100, 174.23m),   // real, protected
            new("CLOSED", "STK", null, 0, 50.00m),    // just-closed, IBKR's zero-qty echo
        };

        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
        };

        var message = IbSnapshotFormatter.BuildSnapshotMessage(account, positions, orders);

        message.Should().Contain("NVDA");
        message.Should().NotContain("CLOSED");
    }

    [Fact]
    public void GetUnprotectedPositions_ExcludesZeroQuantityPositions()
    {
        var positions = new List<IbkrPosition>
        {
            new("NVDA", "STK", null, 100, 174.23m),   // real, protected
            new("CLOSED", "STK", null, 0, 50.00m),    // just-closed, no stop, would look "unprotected"
        };

        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
        };

        var unprotected = IbSnapshotFormatter.GetUnprotectedPositions(positions, orders);

        unprotected.Should().NotContain(p => p.Symbol == "CLOSED");
        unprotected.Should().BeEmpty();
    }

    [Fact]
    public void BuildSnapshotMessage_OptionsPosition_IncludesContractDetailLine()
    {
        var account = new AccountSnapshot(
            NetLiquidation: 127843m,
            TotalCash:      42615m,
            BuyingPower:    84230m,
            TodayPnL:       623m,
            TimedOut:       false);

        var positions = new List<IbkrPosition>
        {
            new("SPX", "OPT", "SPXW260714C07595000", 4, 35128m),
        };

        var orders = new List<IbkrOpenOrder>
        {
            new(1, "SPX", "OPT", "SPXW260714C07595000", "SELL", "TRAIL", 4, "Submitted", 50.00, null),
        };

        var message = IbSnapshotFormatter.BuildSnapshotMessage(account, positions, orders);

        message.Should().Contain("Jul 14 '26 $7595 Call");
    }

    [Fact]
    public void ParseOccContract_MultiCharacterRoot_ParsesCorrectly()
    {
        var result = IbSnapshotFormatter.ParseOccContract("SPXW260714C07595000");

        result.Should().NotBeNull();
        result!.Value.Expiration.Should().Be(new DateOnly(2026, 7, 14));
        result.Value.Right.Should().Be("C");
        result.Value.Strike.Should().Be(7595m);
    }

    [Fact]
    public void ParseOccContract_SingleCharacterRoot_ParsesCorrectly()
    {
        var result = IbSnapshotFormatter.ParseOccContract("V260620C00450000");

        result.Should().NotBeNull();
        result!.Value.Expiration.Should().Be(new DateOnly(2026, 6, 20));
        result.Value.Right.Should().Be("C");
        result.Value.Strike.Should().Be(450m);
    }

    [Fact]
    public void ParseOccContract_MalformedString_ReturnsNull()
    {
        var result = IbSnapshotFormatter.ParseOccContract("not-a-valid-contract");

        result.Should().BeNull();
    }

    // Guardian's remediation loop branches to PlaceProtectiveStopWithTargetAsync (OCA pair)
    // vs. PlaceProtectiveStopAsync (bare stop) based directly on whether this returns
    // a non-null, unambiguous Order — these tests are the branch condition.
    [Fact]
    public void GetMatchingTargetOrder_ReturnsLmtOrder_WhenPresent()
    {
        var position = new IbkrPosition("NVDA", "STK", null, 100, 174.23m);
        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
            new(2, "NVDA", "STK", null, "SELL", "LMT",   100, "Submitted", null,   188.00),
        };

        var (target, ambiguous) = IbSnapshotFormatter.GetMatchingTargetOrder(position, orders);

        target.Should().NotBeNull();
        target!.OrderId.Should().Be(2);
        ambiguous.Should().BeFalse();
    }

    [Fact]
    public void GetMatchingTargetOrder_ReturnsNull_WhenNoTargetExists()
    {
        var position = new IbkrPosition("NVDA", "STK", null, 100, 174.23m);
        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
        };

        var (target, ambiguous) = IbSnapshotFormatter.GetMatchingTargetOrder(position, orders);

        target.Should().BeNull();
        ambiguous.Should().BeFalse();
    }

    // A cancelled remnant of an earlier (e.g. buggy OCA-pairing) attempt must not count as
    // a live match — only the genuinely working order should be matched.
    [Fact]
    public void GetMatchingTargetOrder_IgnoresCancelledOrder_MatchesOnlyLiveOne()
    {
        var position = new IbkrPosition("GE", "STK", null, 11, 359.92m);
        var orders = new List<IbkrOpenOrder>
        {
            new(1, "GE", "STK", null, "SELL", "LMT", 11, "Cancelled", null, 375.00),
            new(2, "GE", "STK", null, "SELL", "LMT", 11, "Submitted", null, 382.69),
        };

        var (target, ambiguous) = IbSnapshotFormatter.GetMatchingTargetOrder(position, orders);

        target.Should().NotBeNull();
        target!.OrderId.Should().Be(2);
        target.Status.Should().Be("Submitted");
        ambiguous.Should().BeFalse();
    }

    // Two genuinely live target orders on the same symbol is not safe to resolve via
    // FirstOrDefault — the caller must be told to stop, not handed a guess.
    [Fact]
    public void GetMatchingTargetOrder_TwoLiveOrders_ReturnsAmbiguousAndNoOrder()
    {
        var position = new IbkrPosition("GE", "STK", null, 11, 359.92m);
        var orders = new List<IbkrOpenOrder>
        {
            new(1, "GE", "STK", null, "SELL", "LMT", 11, "Submitted", null, 375.00),
            new(2, "GE", "STK", null, "SELL", "LMT", 11, "PreSubmitted", null, 382.69),
        };

        var (target, ambiguous) = IbSnapshotFormatter.GetMatchingTargetOrder(position, orders);

        target.Should().BeNull();
        ambiguous.Should().BeTrue();
    }
}
