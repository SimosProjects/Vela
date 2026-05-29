using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Engine;

namespace TradeFlow.Tests.Unit;

public class PositionSizerTests
{
    private static readonly RiskEngineOptions DefaultOptions = new();

    private readonly PositionSizer _sizer = new(
        Options.Create(DefaultOptions));

    private static Alert BuildAlert(string side, string type, string direction,
        decimal? pricePaid, string? contractSymbol = null, decimal? strike = null,
        string expiration = "2026-06-20T00:00:00") =>
        new(
            Id: Guid.NewGuid().ToString(),
            UserId: null,
            UserName: "TestTrader",
            Symbol: "TSLA",
            Type: type,
            Direction: direction,
            Strike: strike,
            Expiration: expiration,
            OptionsContractSymbol: contractSymbol,
            ContractDescription: null,
            Side: side,
            Status: "open",
            Result: null,
            ActualPriceAtTimeOfAlert: pricePaid,
            ActualPriceAtTimeOfExit: null,
            PricePaid: pricePaid,
            PriceAtExit: null,
            HighestPrice: null,
            LowestPrice: null,
            LastCheckedPrice: null,
            Risk: "standard",
            LastKnownPercentProfit: null,
            IsProfitableTrade: null,
            XScore: 80,
            CanAverage: true,
            TimeOfEntryAlert: null,
            TimeOfFullExitAlert: null,
            FormattedLength: null,
            IsSwing: false,
            IsBullish: true,
            IsShort: false,
            Strategy: null,
            OriginalMessage: null,
            OriginalExitMessage: null);

    private static AlertClassification CallClassification() =>
        new(AlertCategory.CallOptionEntry, "Call option entry");

    private static AlertClassification StockClassification() =>
        new(AlertCategory.StockEntry, "Stock entry");

    [Fact]
    public void Size_OptionsEntry_CalculatesCorrectQuantity()
    {
        const decimal price = 4.95m;
        var expectedQty = (int)(DefaultOptions.OptionsInitialBudget / (price * 100));

        var alert = BuildAlert("bto", "options", "call", price, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be(expectedQty);
        order.BudgetUsed.Should().Be(expectedQty * price * 100);
    }

    [Fact]
    public void Size_OptionsEntry_CalculatesCorrectStopAndTarget()
    {
        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.StopPrice.Should().Be(4.95m * 0.50m);
        order.TargetPrice.Should().Be(4.95m * (decimal)DefaultOptions.OptionsTargetMultiple);
    }

    [Fact]
    public void Size_OptionsEntry_StandardRisk_UsesStandardTrailPct()
    {
        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.TrailPercent.Should().Be(DefaultOptions.OptionsStandardTrailPct);
    }

    [Fact]
    public void Size_StockEntry_CalculatesCorrectQuantity()
    {
        const decimal price = 165.33m;
        var expectedQty = (int)(DefaultOptions.StockInitialBudget / price);

        var alert = BuildAlert("bto", "commons", "none", price);
        var order = _sizer.Size(alert, StockClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be(expectedQty);
    }

    [Fact]
    public void Size_StockEntry_CalculatesCorrectStopAndTarget()
    {
        var alert = BuildAlert("bto", "commons", "none", 165.33m);
        var order = _sizer.Size(alert, StockClassification());

        order.Should().NotBeNull();
        order!.StopPrice.Should().Be(165.33m * 0.85m);
        order.TargetPrice.Should().Be(165.33m * (decimal)DefaultOptions.StockTargetMultiple);
    }

    [Fact]
    public void Size_NullPrice_ReturnsNull()
    {
        var alert = BuildAlert("bto", "options", "call", null, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().BeNull();
    }

    [Fact]
    public void Size_PriceTooHighForBudget_ReturnsNull()
    {
        // Price so high that quantity rounds down to 0
        var alert = BuildAlert("bto", "options", "call", 150.00m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().BeNull();
    }

    [Fact]
    public void Size_AveragingOrder_UsesHalfBudget()
    {
        const decimal price = 4.95m;
        var expectedQty = (int)(DefaultOptions.OptionsAverageBudget / (price * 100));

        var alert = BuildAlert("avg", "options", "call", price, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification(), isAverage: true);

        order.Should().NotBeNull();
        order!.IsAverage.Should().BeTrue();
        order.Quantity.Should().Be(expectedQty);
    }

    [Fact]
    public void Size_OptionsExpiresToday_ClassifiedAsLotto()
    {
        var todayEt = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;

        var alert = BuildAlert("bto", "options", "call", 4.95m,
            "TSLA260620C00450000", 450,
            expiration: todayEt.ToString("yyyy-MM-ddTHH:mm:ss"));

        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.TrailPercent.Should().Be(DefaultOptions.OptionsLottoTrailPct);
    }
}