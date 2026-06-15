namespace Vela.Tests.Unit;

public class AlertClassifierTests
{
    // -- Helpers --
    private static Alert BuildAlert(
        string type, string direction, string side) =>
        new(Id: "test-id", UserId: null, UserName: null,
            Symbol: "TSLA", Type: type, Direction: direction,
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
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    // -- Theory: all classification combinations --
    [Theory]
    [InlineData("options", "call", "bto",  AlertCategory.CallOptionEntry)]
    [InlineData("options", "call", "stc",  AlertCategory.CallOptionExit)]
    [InlineData("options", "put",  "bto",  AlertCategory.PutOptionEntry)]
    [InlineData("options", "put",  "stc",  AlertCategory.PutOptionExit)]
    [InlineData("commons", "none", "bto",  AlertCategory.StockEntry)]
    [InlineData("commons", "none", "stc",  AlertCategory.StockExit)]
    [InlineData("futures", "long", "bto",  AlertCategory.Unclassified)]
    public void Classify_AllCombinations_ReturnsCorrectCategory(
        string type, string direction, string side,
        AlertCategory expectedCategory)
    {
        var alert = BuildAlert(type, direction, side);
        var result = AlertClassifier.Classify(alert);
        Assert.Equal(expectedCategory, result.Category);
    }

    // -- Facts: IsEntry helper --
    [Fact]
    public void IsEntry_CallOptionEntry_ReturnsTrue()
    {
        var classification = new AlertClassification(
            AlertCategory.CallOptionEntry, "Call option entry");
        Assert.True(AlertClassifier.IsEntry(classification));
    }

    [Fact]
    public void IsEntry_CallOptionExit_ReturnsFalse()
    {
        var classification = new AlertClassification(
            AlertCategory.CallOptionExit, "Call option exit");
        Assert.False(AlertClassifier.IsEntry(classification));
    }
}