using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Engine;

namespace TradeFlow.Tests.Unit;

public class PositionSizerTests
{
    private readonly PositionSizer _sizer = new(
        Options.Create(new RiskEngineOptions()));

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
        // $1,000 budget, price $4.95, quantity = floor(1000 / (4.95 * 100)) = 2
        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be(2);
        order.BudgetUsed.Should().Be(990m); // 2 x 4.95 x 100
    }

    [Fact]
    public void Size_OptionsEntry_CalculatesCorrectStopAndTarget()
    {
        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.StopPrice.Should().Be(4.95m * 0.50m);   // -50%
        order.TargetPrice.Should().Be(4.95m * 3.00m);  // +200%
    }

    [Fact]
    public void Size_OptionsEntry_StandardRisk_UsesStandardTrailPct()
    {
        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.TrailPercent.Should().Be(40.0); // standard options trail
    }

    [Fact]
    public void Size_StockEntry_CalculatesCorrectQuantity()
    {
        // $3,000 budget, price $165.33, quantity = floor(3000 / 165.33) = 18
        var alert = BuildAlert("bto", "commons", "none", 165.33m);
        var order = _sizer.Size(alert, StockClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be(18);
    }

    [Fact]
    public void Size_StockEntry_CalculatesCorrectStopAndTarget()
    {
        var alert = BuildAlert("bto", "commons", "none", 165.33m);
        var order = _sizer.Size(alert, StockClassification());

        order.Should().NotBeNull();
        order!.StopPrice.Should().Be(165.33m * 0.85m);  // -15%
        order.TargetPrice.Should().Be(165.33m * 1.30m); // +30%
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

        // $1,000 / ($150 x 100) = 0.066 → rounds to 0 → null
        order.Should().BeNull();
    }

    [Fact]
    public void Size_AveragingOrder_UsesHalfBudget()
    {
        var alert = BuildAlert("avg", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification(), isAverage: true);

        order.Should().NotBeNull();
        order!.IsAverage.Should().BeTrue();
        // $500 budget, quantity = floor(500 / (4.95 * 100)) = 1
        order.Quantity.Should().Be(1);
    }

    [Fact]
    public void Size_OptionsExpiresToday_ClassifiedAsLotto()
    {
        // Use today's ET date as expiration to trigger lotto classification
        var todayEt = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;

        var alert = BuildAlert("bto", "options", "call", 4.95m,
            "TSLA260620C00450000", 450,
            expiration: todayEt.ToString("yyyy-MM-ddTHH:mm:ss"));

        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.TrailPercent.Should().Be(50.0); // lotto trail
    }
}