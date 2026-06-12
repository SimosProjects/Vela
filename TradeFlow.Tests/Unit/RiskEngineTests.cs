namespace TradeFlow.Tests.Unit;

public class RiskEngineTests
{
    // Helpers
    private static Alert BuildAlert(
        string side = "bto",
        string risk = "standard",
        double xScore = 75.0,
        string userName = "yoyomun") =>
        new(Id: "test-id", UserId: null, UserName: userName,
            Symbol: "TSLA", Type: "options", Direction: "call",
            Strike: 395m, Expiration: null,
            OptionsContractSymbol: null, ContractDescription: null,
            Side: side, Status: null, Result: null,
            ActualPriceAtTimeOfAlert: null, ActualPriceAtTimeOfExit: null,
            PricePaid: null, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: risk,
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: xScore, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    private static Alert BuildStockAlert(decimal? price) =>
        new(Id: "test-id", UserId: null, UserName: "yoyomun",
            Symbol: "SBFM", Type: "commons", Direction: "none",
            Strike: null, Expiration: null,
            OptionsContractSymbol: null, ContractDescription: null,
            Side: "bto", Status: null, Result: null,
            ActualPriceAtTimeOfAlert: price, ActualPriceAtTimeOfExit: null,
            PricePaid: price, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: "standard",
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: 75.0, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    private static Alert BuildOptionAlert(decimal? price) =>
        new(Id: "test-id", UserId: null, UserName: "Fibonaccizer",
            Symbol: "SPX", Type: "options", Direction: "put",
            Strike: 5000m, Expiration: "2026-06-20T00:00:00",
            OptionsContractSymbol: "SPXW260620P05000000", ContractDescription: null,
            Side: "bto", Status: null, Result: null,
            ActualPriceAtTimeOfAlert: price, ActualPriceAtTimeOfExit: null,
            PricePaid: price, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: "standard",
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: 75.0, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    // -- EntryOnlyRule --
    [Fact]
    public void EntryOnlyRule_BtoSide_Passes()
    {
        var rule   = new EntryOnlyRule();
        var alert  = BuildAlert(side: "bto");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("stc")]
    [InlineData("btc")]
    [InlineData("sto")]
    public void EntryOnlyRule_NonBtoSide_Fails(string side)
    {
        var rule   = new EntryOnlyRule();
        var alert  = BuildAlert(side: side);
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- NoLottoRule --
    [Fact]
    public void NoLottoRule_StandardRisk_Passes()
    {
        var rule = new NoLottoRule(() => false);
        var alert  = BuildAlert(risk: "standard");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void NoLottoRule_LottoRisk_Fails()
    {
        var rule = new NoLottoRule(() => true);
        var alert  = BuildAlert(risk: "lotto");
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
        Assert.Contains("lotto", result.Reason);
    }

    // -- MinXScoreRule --
    [Theory]
    [InlineData(60.0, 60.0, true)]   // exactly at threshold: passes
    [InlineData(60.0, 59.9, false)]  // just below: fails
    [InlineData(60.0, 100.0, true)]  // well above: passes
    [InlineData(60.0, 0.0, false)]   // zero: fails
    public void MinXScoreRule_Threshold_CorrectResult(
        double threshold, double xScore, bool expectedPassed)
    {
        var rule   = new MinXScoreRule(threshold);
        var alert  = BuildAlert(xScore: xScore);
        var result = rule.Evaluate(alert);
        Assert.Equal(expectedPassed, result.Passed);
    }

    [Fact]
    public void MinXScoreRule_NullXScore_Fails()
    {
        // Null XScore should be treated as 0, below any threshold
        var rule  = new MinXScoreRule(60.0);
        var alert = BuildAlert() with { XScore = null };
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- ApprovedTraderRule --
    [Fact]
    public void ApprovedTraderRule_ApprovedTrader_Passes()
    {
        var rule   = new ApprovedTraderRule(["yoyomun", "Fibonaccizer"]);
        var alert  = BuildAlert(userName: "yoyomun");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedTraderRule_CaseInsensitive_Passes()
    {
        // Trader name matching should be case-insensitive
        var rule   = new ApprovedTraderRule(["yoyomun"]);
        var alert  = BuildAlert(userName: "YOYOMUN");
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedTraderRule_UnknownTrader_Fails()
    {
        var rule   = new ApprovedTraderRule(["yoyomun"]);
        var alert  = BuildAlert(userName: "unknown_trader");
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    // -- Short-circuit behavior in Risk Engine Service --
    [Fact]
    public void RiskEngine_FirstRuleFails_ShortCircuits()
    {
        // Arrange, mock a rule that always fails
        var failingRule = new Mock<IRiskRule>();
        failingRule
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Fail("First rule failed"));

        var neverCalledRule = new Mock<IRiskRule>();

        var engine = new RiskEngineService([
            failingRule.Object,
            neverCalledRule.Object
        ]);

        var alert = BuildAlert();

        // Act
        var result = engine.Evaluate(alert);

        // Assert
        Assert.False(result.Approved);
        Assert.Equal("First rule failed", result.Reason);

        // The second rule should never have been called
        neverCalledRule.Verify(
            r => r.Evaluate(It.IsAny<Alert>()),
            Times.Never);
    }

    [Fact]
    public void RiskEngine_AllRulesPass_ReturnsApproved()
    {
        // Arrange, mock rules that always pass
        var passingRule1 = new Mock<IRiskRule>();
        passingRule1
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Pass("Rule 1 passed"));

        var passingRule2 = new Mock<IRiskRule>();
        passingRule2
            .Setup(r => r.Evaluate(It.IsAny<Alert>()))
            .Returns(RuleResult.Pass("Rule 2 passed"));

        var engine = new RiskEngineService([
            passingRule1.Object,
            passingRule2.Object
        ]);

        var alert = BuildAlert();

        // Act
        var result = engine.Evaluate(alert);

        // Assert
        Assert.True(result.Approved);

        // Both rules should have been called
        passingRule1.Verify(r => r.Evaluate(It.IsAny<Alert>()), Times.Once);
        passingRule2.Verify(r => r.Evaluate(It.IsAny<Alert>()), Times.Once);
    }

    // -- MinStockPriceRule --

    [Fact]
    public void MinStockPriceRule_StockAtMinimum_Passes()
    {
        var rule  = new MinStockPriceRule(minimumPrice: 3.00m);
        var alert = BuildStockAlert(price: 3.00m);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MinStockPriceRule_StockAboveMinimum_Passes()
    {
        var rule  = new MinStockPriceRule(minimumPrice: 3.00m);
        var alert = BuildStockAlert(price: 25.50m);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MinStockPriceRule_StockBelowMinimum_Fails()
    {
        var rule  = new MinStockPriceRule(minimumPrice: 3.00m);
        var alert = BuildStockAlert(price: 1.19m); // SBFM scenario
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
        Assert.Contains("penny stock filter", result.Reason);
    }

    [Fact]
    public void MinStockPriceRule_OptionAlert_AlwaysPasses()
    {
        // Options below $3 are fine — a $1.50 option is not a penny stock
        var rule  = new MinStockPriceRule(minimumPrice: 3.00m);
        var alert = BuildOptionAlert(price: 0.50m);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MinStockPriceRule_NullPrice_Fails()
    {
        var rule  = new MinStockPriceRule(minimumPrice: 3.00m);
        var alert = BuildStockAlert(price: null);
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData(3.00,  2.99, false)]  // just below threshold
    [InlineData(3.00,  3.00, true)]   // exactly at threshold
    [InlineData(3.00,  3.01, true)]   // just above threshold
    [InlineData(5.00,  4.99, false)]  // custom threshold
    [InlineData(0.00,  0.01, true)]   // zero threshold disables filter
    public void MinStockPriceRule_Threshold_CorrectResult(
        decimal threshold, decimal price, bool expectedPassed)
    {
        var rule   = new MinStockPriceRule(minimumPrice: threshold);
        var alert  = BuildStockAlert(price: price);
        var result = rule.Evaluate(alert);
        Assert.Equal(expectedPassed, result.Passed);
    }

    // -- ApprovedOrHighScoreRule --

    [Fact]
    public void ApprovedOrHighScoreRule_ApprovedTraderWithZeroScore_Passes()
    {
        // Approved traders bypass the XScore check entirely
        var rule   = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert  = BuildAlert(userName: "yoyomun", xScore: 0.0);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_ApprovedTraderCaseInsensitive_Passes()
    {
        var rule   = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert  = BuildAlert(userName: "YOYOMUN", xScore: 0.0);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_UnknownTraderAboveThreshold_Passes()
    {
        // Unknown traders must meet the minimum XScore
        var rule   = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert  = BuildAlert(userName: "unknown_trader", xScore: 75.0);
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_UnknownTraderBelowThreshold_Fails()
    {
        var rule   = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert  = BuildAlert(userName: "unknown_trader", xScore: 55.0);
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_UnknownTraderZeroScore_Fails()
    {
        var rule   = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert  = BuildAlert(userName: "unknown_trader", xScore: 0.0);
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_NullTraderName_Fails()
    {
        var rule  = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert = BuildAlert() with { UserName = null };
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ApprovedOrHighScoreRule_ApprovedTraderWithNullScore_Passes()
    {
        // Approved traders bypass XScore even if it's null
        var rule  = new ApprovedOrHighScoreRule(["yoyomun"], minimumScore: 60.0);
        var alert = BuildAlert(userName: "yoyomun") with { XScore = null };
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void BearishCallBlockRule_BlockInactive_Passes()
    {
        var rule   = new BearishCallBlockRule(blockCalls: () => false);
        var alert  = BuildAlert() with { IsBullish = true, Type = "options" };
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void BearishCallBlockRule_ActiveAndCallEntry_Fails()
    {
        var rule   = new BearishCallBlockRule(blockCalls: () => true);
        var alert  = BuildAlert() with { IsBullish = true, Type = "options", Direction = "call" };
        var result = rule.Evaluate(alert);
        Assert.False(result.Passed);
    }

    [Fact]
    public void BearishCallBlockRule_ActiveAndPutEntry_Passes()
    {
        var rule   = new BearishCallBlockRule(blockCalls: () => true);
        var alert  = BuildAlert() with { IsBullish = false, Type = "options", Direction = "put" };
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }

    [Fact]
    public void BearishCallBlockRule_ActiveAndStockEntry_Passes()
    {
        var rule   = new BearishCallBlockRule(blockCalls: () => true);
        var alert  = BuildAlert() with { IsBullish = true, Type = "commons" };
        var result = rule.Evaluate(alert);
        Assert.True(result.Passed);
    }
}