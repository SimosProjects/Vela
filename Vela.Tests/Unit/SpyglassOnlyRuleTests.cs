using Vela.Worker.Configuration;
using Vela.Worker.Engine;

namespace Vela.Tests.Unit;

public class SpyglassOnlyRuleTests
{
    // -- Helpers --

    private static Alert BuildAlert(string? userName, string? id, double xScore = 100.0) =>
        new(Id: id, UserId: null, UserName: userName,
            Symbol: "AAPL", Type: "commons", Direction: null,
            Strike: null, Expiration: null,
            OptionsContractSymbol: null, ContractDescription: null,
            Side: "bto", Status: null, Result: null,
            ActualPriceAtTimeOfAlert: 150m, ActualPriceAtTimeOfExit: null,
            PricePaid: null, PriceAtExit: null,
            HighestPrice: null, LowestPrice: null,
            LastCheckedPrice: null, Risk: "standard",
            LastKnownPercentProfit: null, IsProfitableTrade: null,
            XScore: xScore, CanAverage: null,
            TimeOfEntryAlert: null, TimeOfFullExitAlert: null,
            FormattedLength: null, IsSwing: null,
            IsBullish: null, IsShort: null,
            Strategy: null, OriginalMessage: null,
            OriginalExitMessage: null);

    private static RiskEngineOptions Options(bool spyglassOnly) => new()
    {
        ApprovedTraders  = ["SLAM"],
        MinXScore        = 80,
        SpyglassOnlyMode = spyglassOnly,
    };

    private static RiskEngineService BuildEngine(bool spyglassOnly) =>
        new(RiskRuleComposer.Compose(Options(spyglassOnly), () => false, () => false, () => false));

    // -- SpyglassOnlyRule --

    [Fact]
    public void SpyglassAlert_Passes()
    {
        var result = new SpyglassOnlyRule().Evaluate(BuildAlert("SPYGLASS", "spyglass-AAPL-20261002-1"));
        Assert.True(result.Passed);
    }

    [Fact]
    public void XtradesTraderAtXScore100_Rejects()
    {
        var result = new SpyglassOnlyRule().Evaluate(BuildAlert("SLAM", "abc123", xScore: 100.0));
        Assert.False(result.Passed);
        Assert.Contains("Spyglass-only mode", result.Reason);
    }

    [Fact]
    public void NullUserName_Rejects()
    {
        var result = new SpyglassOnlyRule().Evaluate(BuildAlert(null, "spyglass-AAPL-1"));
        Assert.False(result.Passed);
    }

    [Fact]
    public void SpoofedUserNameWithoutSpyglassId_Rejects()
    {
        var result = new SpyglassOnlyRule().Evaluate(BuildAlert("SPYGLASS", "xtrades-9999"));
        Assert.False(result.Passed);
    }

    [Fact]
    public void SpyglassIdWithWrongUserName_Rejects()
    {
        var result = new SpyglassOnlyRule().Evaluate(BuildAlert("SLAM", "spyglass-AAPL-1"));
        Assert.False(result.Passed);
    }

    // -- Composition --

    [Fact]
    public void Compose_ModeEnabled_PlacesRuleDirectlyAfterEntryOnly()
    {
        var rules = RiskRuleComposer.Compose(Options(true), () => false, () => false, () => false);
        var entryIdx = rules.FindIndex(r => r is EntryOnlyRule);
        Assert.IsType<SpyglassOnlyRule>(rules[entryIdx + 1]);
    }

    [Fact]
    public void Compose_ModeDisabled_OmitsRule()
    {
        var rules = RiskRuleComposer.Compose(Options(false), () => false, () => false, () => false);
        Assert.DoesNotContain(rules, r => r is SpyglassOnlyRule);
    }

    [Fact]
    public void Engine_ModeEnabled_RejectsApprovedXtradesTrader()
    {
        var result = BuildEngine(true).Evaluate(BuildAlert("SLAM", "abc123"));
        Assert.False(result.Approved);
        Assert.Contains("Spyglass-only mode", result.Reason);
    }

    [Fact]
    public void Engine_ModeEnabled_AcceptsSpyglassAlert()
    {
        var result = BuildEngine(true).Evaluate(BuildAlert("SPYGLASS", "spyglass-AAPL-20261002-1"));
        Assert.True(result.Approved);
    }

    [Fact]
    public void Engine_ModeEnabled_ExitKeepsSideRejectionReason()
    {
        var exit = BuildAlert("SLAM", "abc123") with { Side = "stc" };
        var result = BuildEngine(true).Evaluate(exit);
        Assert.Contains("BTO entry", result.Reason);
    }

    [Fact]
    public void Engine_ModeDisabled_ApprovedXtradesTraderStillPasses()
    {
        var result = BuildEngine(false).Evaluate(BuildAlert("SLAM", "abc123"));
        Assert.True(result.Approved);
    }

    // -- Options validation --

    [Fact]
    public void Options_EmptyApprovedTraders_PassesValidation()
    {
        var options = new RiskEngineOptions { ApprovedTraders = [], SpyglassOnlyMode = true };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.True(valid);
    }

    [Fact]
    public void WorkerAppSettings_BindAndValidate_WithSpyglassOnlyMode()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Vela.Worker", "appsettings.json")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir!, "Vela.Worker", "appsettings.json")),
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var options = System.Text.Json.JsonSerializer.Deserialize<RiskEngineOptions>(
            doc.RootElement.GetProperty(RiskEngineOptions.SectionName).GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.True(valid, string.Join("; ", results.Select(r => r.ErrorMessage)));
        Assert.True(options.SpyglassOnlyMode);
    }
}
