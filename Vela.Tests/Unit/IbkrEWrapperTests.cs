using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
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
}
