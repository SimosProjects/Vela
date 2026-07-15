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
}
