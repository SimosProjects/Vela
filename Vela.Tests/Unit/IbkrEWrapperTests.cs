using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Models;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class IbkrEWrapperTests
{
    [Fact]
    public async Task OpenOrder_TrailOrder_UsesTrailStopPriceNotAuxPrice()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterAllOpenOrdersCallback();

        var contract = new Contract { Symbol = "NVDA", SecType = "STK" };
        var order = new Order
        {
            Action = "SELL",
            OrderType = "TRAIL",
            TotalQuantity = 100,
            AuxPrice = double.MaxValue,
            TrailStopPrice = 166.50,
            LmtPrice = double.MaxValue
        };
        var orderState = new OrderState { Status = "Submitted" };

        wrapper.openOrder(1, contract, order, orderState);
        wrapper.openOrderEnd();

        var orders = await tcs.Task;
        var mapped = Assert.Single(orders);
        Assert.Equal(166.50, mapped.AuxPrice);
    }

    [Fact]
    public async Task OpenOrder_PlainStopOrder_StillUsesAuxPrice()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterAllOpenOrdersCallback();

        var contract = new Contract { Symbol = "AAPL", SecType = "STK" };
        var order = new Order
        {
            Action = "SELL",
            OrderType = "STP",
            TotalQuantity = 50,
            AuxPrice = 200.00,
            TrailStopPrice = double.MaxValue,
            LmtPrice = double.MaxValue
        };
        var orderState = new OrderState { Status = "Submitted" };

        wrapper.openOrder(2, contract, order, orderState);
        wrapper.openOrderEnd();

        var orders = await tcs.Task;
        var mapped = Assert.Single(orders);
        Assert.Equal(200.00, mapped.AuxPrice);
    }

    // PlaceProtectiveStopAsync (and PlaceTrailWithFallbackAsync/PlaceTrailWithTargetAsync)
    // can't be exercised end-to-end here — EnsureConnected() requires a real live socket to
    // Gateway, same reason IbkrBrokerServiceTests don't exist and IbkrConnectionTests are
    // gated behind SKIP_IBKR_TESTS. This instead verifies the actual mechanism the 103 fix
    // touches: error() must resolve a registered _stopRejectionCallbacks entry for code 103,
    // exactly as it already does for 201 and 404, so callers waiting on that TCS see the
    // rejection within their detection window instead of timing out into a false "accepted".
    [Fact]
    public async Task Error_DuplicateOrderId103_ResolvesStopRejectionCallback()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.RegisterStopRejectionCallback(42, tcs);

        wrapper.error(42, 103, "Duplicate order id");

        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Duplicate order id", await tcs.Task);
    }

    // Reproduces the SPX/SPXW incident (2026-07-17/18): the alert-supplied contract said
    // "SPX260717C07500000" but IBKR resolved and filled the order against its actual
    // weekly listing, "SPXW260717C07500000". execDetails receives the full Contract IBKR
    // actually executed against, LocalSymbol included — this confirms that value now flows
    // into the resolved OrderFill instead of being discarded.
    [Fact]
    public async Task ExecDetails_ContractLocalSymbolDiffersFromSubmittedSymbol_ResolvesOrderFillWithIbkrValue()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12004, requestedQuantity: 1);

        var contract = new Contract
        {
            Symbol = "SPX",
            SecType = "OPT",
            LocalSymbol = "SPXW260717C07500000"
        };
        var execution = new Execution
        {
            OrderId = 12004,
            AvgPrice = 3.10,
            CumQty = 1
        };

        wrapper.execDetails(0, contract, execution);

        Assert.True(tcs.Task.IsCompleted);
        var fill = await tcs.Task;
        Assert.Equal(3.10m, fill.AvgFillPrice);
        Assert.Equal("SPXW260717C07500000", fill.LocalSymbol);
    }

    // -- Partial-fill quantity gating (2026-07-17 UBER incident) --

    // Reproduces UBER's actual timeline: order for 5 filled 1 first, then the remaining 4
    // roughly nine minutes later. The TCS must not resolve on the first (partial) callback —
    // only the second, once cumulative filled reaches the full requested quantity.
    [Fact]
    public async Task ExecDetails_PartialThenFullSequence_ResolvesOnlyOnSecondCallback()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        Assert.False(tcs.Task.IsCompleted);

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 5 });

        Assert.True(tcs.Task.IsCompleted);
        var fill = await tcs.Task;
        Assert.Equal(4.25m, fill.AvgFillPrice);
        Assert.Equal(5, fill.FilledQuantity);
    }

    // The inverse of the UBER incident: if the remainder genuinely never fills, the TCS must
    // never resolve as "Filled" — it must simply stay pending forever, so that whatever bounded
    // wait the caller applies (ClosePositionAsync/PartialCloseAsync/stop-order watch) is the
    // only thing that can end the wait, and it does so via the degraded-state path, not via a
    // false success here.
    [Fact]
    public async Task ExecDetails_NeverReachesFullQuantity_TcsNeverResolves()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        var tcs = wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };

        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
    }

    // A partial callback must invoke the caller's onPartialFill side-channel with the latest
    // confirmed fill data — this is what lets a caller (e.g. the stop-order watchdog) start a
    // bounded completion timer without treating the partial as done.
    [Fact]
    public void ExecDetails_PartialCallback_InvokesOnPartialFillWithLatestData()
    {
        var wrapper = new IbkrEWrapper(NullLogger<IbkrEWrapper>.Instance);
        OrderFill? observed = null;
        wrapper.RegisterExecDetailsTcsCallback(12184, requestedQuantity: 5, onPartialFill: fill => observed = fill);

        var contract = new Contract { Symbol = "UBER", SecType = "OPT", LocalSymbol = "UBER270617C00100000" };
        wrapper.execDetails(0, contract, new Execution { OrderId = 12184, AvgPrice = 4.25, CumQty = 1 });

        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FilledQuantity);
        Assert.Equal(4.25m, observed.AvgFillPrice);
    }
}
