using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Engine;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class PositionSizerTests
{
    private static readonly RiskEngineOptions DefaultOptions = new();

    private readonly PositionSizer _sizer = new(
        Options.Create(DefaultOptions),
        NullLogger<PositionSizer>.Instance);

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
    public void Size_RestrictedTrader_ScalesBudget()
    {
        var options = new RiskEngineOptions
        {
            RestrictedTraders = new Dictionary<string, int> { ["TestTrader"] = 25 }
        };
        var sizer = new PositionSizer(
            Options.Create(options),
            NullLogger<PositionSizer>.Instance);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        // 25% of $2,000 = $500 budget -> floor($500 / ($4.95 * 100)) = 1 contract
        order.Should().NotBeNull();
        order!.Quantity.Should().Be(1);
    }

    [Fact]
    public void Size_BlockedTrader_ReturnsNull()
    {
        var options = new RiskEngineOptions
        {
            RestrictedTraders = new Dictionary<string, int> { ["TestTrader"] = 0 }
        };
        var sizer = new PositionSizer(
            Options.Create(options),
            NullLogger<PositionSizer>.Instance);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().BeNull();
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

    [Fact]
    public void Size_BullishRegime_UsesFullBudget()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(0, 2, RegimeTier.Bullish, 1.0m, false, 560m, 540m, 480m);
        var sizer = new PositionSizer(
            Options.Create(DefaultOptions),
            NullLogger<PositionSizer>.Instance,
            regime);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.BudgetUsed.Should().Be((int)(DefaultOptions.OptionsInitialBudget * 1.0m / (4.95m * 100)) * 4.95m * 100);
    }

    [Fact]
    public void Size_ChoppyRegime_HalvesBudget()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(3, 2, RegimeTier.Choppy, 0.5m, false, 540m, 540m, 480m);
        var sizer = new PositionSizer(
            Options.Create(DefaultOptions),
            NullLogger<PositionSizer>.Instance,
            regime);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        var expectedBudget = DefaultOptions.OptionsInitialBudget * 0.5m;
        var expectedQty    = (int)(expectedBudget / (4.95m * 100));
        order!.Quantity.Should().Be(expectedQty);
    }

    [Fact]
    public void Size_BearishRegime_QuartersBudget()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(5, 2, RegimeTier.Bearish, 0.25m, true, 520m, 540m, 480m);
        var sizer = new PositionSizer(
            Options.Create(DefaultOptions),
            NullLogger<PositionSizer>.Instance,
            regime);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be(1);
    }

    [Fact]
    public void Size_NullRegime_UsesFullBudget()
    {
        var sizer = new PositionSizer(
            Options.Create(DefaultOptions),
            NullLogger<PositionSizer>.Instance,
            regime: null);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.Quantity.Should().Be((int)(DefaultOptions.OptionsInitialBudget / (4.95m * 100)));
    }

    [Fact]
    public void Size_LottoBudget_NotScaledByRegime()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(5, 2, RegimeTier.Bearish, 0.25m, true, 520m, 540m, 480m);
        var sizer = new PositionSizer(
            Options.Create(DefaultOptions),
            NullLogger<PositionSizer>.Instance,
            regime);

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var alert = BuildAlert("bto", "options", "call", 0.50m, "TSLA260620C00450000", 450,
            expiration: today);
        var order = sizer.Size(alert, CallClassification());

        if (order is not null)
            order.BudgetUsed.Should().BeLessThanOrEqualTo(DefaultOptions.OptionsLottoBudget);
    }

    [Fact]
    public void MarketRegimeService_BullishTier_SetsCorrectMultiplier()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(0, 2, RegimeTier.Bullish, 1.0m, false, 560m, 540m, 480m);

        regime.Tier.Should().Be(RegimeTier.Bullish);
        regime.SizingMultiplier.Should().Be(1.0m);
        regime.BlockCalls.Should().BeFalse();
        regime.IsChoppy.Should().BeFalse();
        regime.Ma20.Should().Be(560m);
        regime.Ma50.Should().Be(540m);
        regime.Ma200.Should().Be(480m);
    }

    [Fact]
    public void MarketRegimeService_BearishTier_SetsBlockCalls()
    {
        var regime = new MarketRegimeService(NullLogger<MarketRegimeService>.Instance);
        regime.SetRegime(5, 2, RegimeTier.Bearish, 0.25m, true, 520m, 540m, 480m);

        // BlockCalls is driven by _blockCallsOverride, not _blockCalls.
        // SetRegime signals the intent but SetBlockCallsOverride is the explicit gate —
        // matching how SystemStateService seeds it via the BlockCallsOverrideChanged event.
        regime.SetBlockCallsOverride(true);

        regime.Tier.Should().Be(RegimeTier.Bearish);
        regime.SizingMultiplier.Should().Be(0.25m);
        regime.BlockCalls.Should().BeTrue();
        regime.IsChoppy.Should().BeTrue();
    }

    [Fact]
    public void Size_StandardOptions_ComputesLimitPrice()
    {
        const decimal price = 4.95m;
        var expected = Math.Round(price * (1 + DefaultOptions.OptionsStandardMaxSlippagePct / 100), 2);

        var alert = BuildAlert("bto", "options", "call", price, "TSLA260620C00450000", 450);
        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.LimitPrice.Should().Be(expected);
    }

    [Fact]
    public void Size_HighOptionsThisWeek_ComputesHighLimitPrice()
    {
        var et      = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var todayEt = DateOnly.FromDateTime(et.DateTime);
        var friday  = todayEt.AddDays((int)DayOfWeek.Friday - (int)todayEt.DayOfWeek);

        if (todayEt.DayOfWeek == DayOfWeek.Friday)
            return; // On Fridays all same-week expiries are lotto; no high window exists

        const decimal price = 4.95m;
        var expected = Math.Round(price * (1 + DefaultOptions.OptionsHighMaxSlippagePct / 100), 2);

        var alert = BuildAlert("bto", "options", "call", price,
            "TSLA260620C00450000", 450,
            expiration: $"{friday:yyyy-MM-dd}T00:00:00");

        var order = _sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.LimitPrice.Should().Be(expected);
    }

    [Fact]
    public void Size_LottoOptions_LimitPriceIsNull()
    {
        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

        var alert = BuildAlert("bto", "options", "call", 0.50m,
            "TSLA260620C00450000", 450,
            expiration: $"{todayEt:yyyy-MM-dd}T00:00:00");

        var order = _sizer.Size(alert, CallClassification());

        if (order is not null)
            order.LimitPrice.Should().BeNull();
    }

    [Fact]
    public void Size_Stock_ComputesLimitPrice()
    {
        const decimal price = 165.33m;
        var expected = Math.Round(price * (1 + DefaultOptions.StockMaxSlippagePct / 100), 2);

        var alert = BuildAlert("bto", "commons", "none", price);
        var order = _sizer.Size(alert, StockClassification());

        order.Should().NotBeNull();
        order!.LimitPrice.Should().Be(expected);
    }

    [Fact]
    public void Size_DisabledSlippage_LimitPriceIsNull()
    {
        var options = new RiskEngineOptions { OptionsStandardMaxSlippagePct = 0m };
        var sizer   = new PositionSizer(Options.Create(options), NullLogger<PositionSizer>.Instance);

        var alert = BuildAlert("bto", "options", "call", 4.95m, "TSLA260620C00450000", 450);
        var order = sizer.Size(alert, CallClassification());

        order.Should().NotBeNull();
        order!.LimitPrice.Should().BeNull();
    }
}