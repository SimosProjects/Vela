using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class MarketRegimeServiceTests
{
    private readonly MarketRegimeService _regime =
        new(NullLogger<MarketRegimeService>.Instance);

    // Default MA values for tests
    private const decimal Ma20  = 560m;
    private const decimal Ma50  = 550m;
    private const decimal Ma200 = 500m;

    [Fact]
    public void SetRegime_FirstCall_AlwaysAppliesTier()
    {
        _regime.SetRegime(
            chopScore: 0, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200,
            isIntradayCheck: false);

        _regime.Tier.Should().Be(RegimeTier.Choppy);
        _regime.SizingMultiplier.Should().Be(0.75m);
        _regime.RegimeBlockCalls.Should().BeFalse();
    }

    [Fact]
    public void SetRegime_IntradayDowngrade_AppliesTier()
    {
        // Start at Bullish
        _regime.SetRegime(
            chopScore: 0, minSignalsForChop: 2,
            tier: RegimeTier.Bullish, sizingMultiplier: 1.0m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        // Intraday check worsens to Choppy, should apply (downgrade)
        _regime.SetRegime(
            chopScore: 3, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: true);

        _regime.Tier.Should().Be(RegimeTier.Choppy);
        _regime.SizingMultiplier.Should().Be(0.75m);
    }

    [Fact]
    public void SetRegime_IntradayUpgrade_HoldsTierAndSizing()
    {
        // Start at Bearish
        _regime.SetRegime(
            chopScore: 4, minSignalsForChop: 2,
            tier: RegimeTier.Bearish, sizingMultiplier: 0.5m, blockCalls: true,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        // Intraday check improves to Choppy, tier and sizing should be held
        _regime.SetRegime(
            chopScore: 1, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: true);

        _regime.Tier.Should().Be(RegimeTier.Bearish, "tier held under downgrade-only rule");
        _regime.SizingMultiplier.Should().Be(0.5m, "sizing held under downgrade-only rule");
    }

    [Fact]
    public void SetRegime_IntradayUpgrade_StillUpdatesRegimeBlockCalls()
    {
        // Regime-computed BlockCalls should follow the actual computed tier,
        // even when the effective tier is held conservatively.
        // This allows SystemStateService to auto-clear the dashboard override.
        _regime.SetRegime(
            chopScore: 4, minSignalsForChop: 2,
            tier: RegimeTier.Bearish, sizingMultiplier: 0.5m, blockCalls: true,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        _regime.RegimeBlockCalls.Should().BeTrue("regime-computed value seeded from Bearish");

        _regime.SetRegime(
            chopScore: 1, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: true);

        _regime.Tier.Should().Be(RegimeTier.Bearish, "tier held");
        _regime.RegimeBlockCalls.Should().BeFalse(
            "RegimeBlockCalls follows computed tier even when effective tier is held — " +
            "allows SystemStateService to auto-clear BlockCalls dashboard override");
    }

    [Fact]
    public void SetRegime_IntradayFurtherDowngrade_AppliesTier()
    {
        // Choppy at 9:20, then Bearish at 11:00, should apply (further downgrade)
        _regime.SetRegime(
            chopScore: 2, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        _regime.SetRegime(
            chopScore: 5, minSignalsForChop: 2,
            tier: RegimeTier.Bearish, sizingMultiplier: 0.5m, blockCalls: true,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: true);

        _regime.Tier.Should().Be(RegimeTier.Bearish);
        _regime.SizingMultiplier.Should().Be(0.5m);
        _regime.RegimeBlockCalls.Should().BeTrue();
    }

    [Fact]
    public void SetRegime_SameTierIntraday_AppliesNormally()
    {
        // Same tier at intraday checkpoint, not an upgrade, should apply fully
        _regime.SetRegime(
            chopScore: 2, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        _regime.SetRegime(
            chopScore: 3, minSignalsForChop: 2,
            tier: RegimeTier.Choppy, sizingMultiplier: 0.75m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: true);

        _regime.Tier.Should().Be(RegimeTier.Choppy);
        _regime.IsChoppy.Should().BeTrue();
        _regime.ChopScore.Should().Be(3, "chop score updates even on same-tier intraday check");
    }

    [Fact]
    public void SetRegimeTier_ManualOverride_AlwaysApplies()
    {
        // SetRegimeTier (dashboard force override) should always apply regardless of session state
        _regime.SetRegime(
            chopScore: 4, minSignalsForChop: 2,
            tier: RegimeTier.Bearish, sizingMultiplier: 0.5m, blockCalls: true,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        _regime.SetRegimeTier(RegimeTier.Bullish, 1.0m, false);

        _regime.Tier.Should().Be(RegimeTier.Bullish);
        _regime.SizingMultiplier.Should().Be(1.0m);
        _regime.RegimeBlockCalls.Should().BeFalse();
    }

    [Fact]
    public void SetBlockCallsOverride_IndependentOfRegimeBlockCalls()
    {
        // Dashboard BlockCalls override is separate from the regime-computed value
        _regime.SetRegime(
            chopScore: 0, minSignalsForChop: 2,
            tier: RegimeTier.Bullish, sizingMultiplier: 1.0m, blockCalls: false,
            ma20: Ma20, ma50: Ma50, ma200: Ma200, isIntradayCheck: false);

        _regime.SetBlockCallsOverride(true);

        _regime.BlockCalls.Should().BeTrue("dashboard override is ON");
        _regime.RegimeBlockCalls.Should().BeFalse("regime-computed value is still Bullish/false");
    }
}