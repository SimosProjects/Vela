namespace Vela.Tests.Unit;

public class AlertNormalizerTests
{
    private readonly AlertNormalizer _normalizer = new();

    private static Alert BuildAlert(
        string? id = "test-id",
        string? symbol = "tsla",
        string? side = "BTO",
        string? type = "OPTIONS",
        string? direction = "CALL",
        string? message = "  BTO TSLA @ market  ") =>
        new(Id: id, UserId: null, UserName: null,
            Symbol: symbol, Type: type, Direction: direction,
            Strike: null, Expiration: null,
            OptionsContractSymbol: null, ContractDescription: null,
            Side: side, Status: null, Result: null,
            ActualPriceAtTimeOfAlert: null, ActualPriceAtTimeOfExit: null,
            PricePaid: null, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: null,
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: null, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: message,
            OriginalExitMessage: null);

    [Fact]
    public void Normalize_Symbol_UpperCase()
    {
        var alert  = BuildAlert(symbol: "tsla");
        var result = _normalizer.Normalize(alert);
        Assert.Equal("TSLA", result.Symbol);
    }

    [Fact]
    public void Normalize_Side_LowerCase()
    {
        var alert  = BuildAlert(side: "BTO");
        var result = _normalizer.Normalize(alert);
        Assert.Equal("bto", result.Side);
    }

    [Fact]
    public void Normalize_Type_LowerCase()
    {
        var alert  = BuildAlert(type: "OPTIONS");
        var result = _normalizer.Normalize(alert);
        Assert.Equal("options", result.Type);
    }

    [Fact]
    public void Normalize_Message_Trimmed()
    {
        var alert  = BuildAlert(message: "  BTO TSLA @ market  ");
        var result = _normalizer.Normalize(alert);
        Assert.Equal("BTO TSLA @ market", result.OriginalMessage);
    }

    [Fact]
    public void Normalize_DoesNotMutateOriginal()
    {
        // Records are immutable, normalize must return a new instance
        var alert  = BuildAlert(symbol: "tsla");
        var result = _normalizer.Normalize(alert);

        // Original unchanged
        Assert.Equal("tsla", alert.Symbol);
        // Result normalized
        Assert.Equal("TSLA", result.Symbol);
    }

    [Theory]
    [InlineData(null,    null,  null,  null,  false)] // missing id
    [InlineData("id",   null,  null,  null,  false)] // missing symbol
    [InlineData("id",   "sym", null,  null,  false)] // missing side
    [InlineData("id",   "sym", "bto", null,  false)] // missing type
    [InlineData("id",   "sym", "bto", "opt", true)]  // all present
    public void IsProcessable_RequiredFields_CorrectResult(
        string? id, string? symbol, string? side, string? type,
        bool expected)
    {
        var alert  = BuildAlert(id: id, symbol: symbol,
                                side: side, type: type);
        var result = _normalizer.IsProcessable(alert);
        Assert.Equal(expected, result);
    }
}