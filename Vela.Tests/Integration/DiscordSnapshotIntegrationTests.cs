using Microsoft.Extensions.Logging.Abstractions;
using Vela.Worker.Formatting;
using Vela.Worker.Services;

namespace Vela.Tests.Integration;

// Posts a real message to the configured Discord summary channel via
// DISCORD_SUMMARY_WEBHOOK_URL. Unlike the IBKR integration tests (which run by
// default and are opted OUT of via SKIP_IBKR_TESTS), this defaults to skipped —
// running it fires a real, visible message in Discord, so it must be opted IN to.
// Set RUN_DISCORD_INTEGRATION_TESTS=true and export DISCORD_SUMMARY_WEBHOOK_URL
// (dotnet test does not read .env) to actually send.
public class DiscordSnapshotIntegrationTests
{
    private static bool ShouldRun =>
        Environment.GetEnvironmentVariable("RUN_DISCORD_INTEGRATION_TESTS") == "true";

    [Fact, Trait("Category", "Integration")]
    public async Task NotifyIbSnapshotAsync_PostsRenderedSnapshotToDiscord()
    {
        if (!ShouldRun) return;

        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);

        var account = new AccountSnapshot(
            NetLiquidation: 127843m,
            TotalCash:      42615m,
            BuyingPower:    84230m,
            TodayPnL:       623m,
            TimedOut:       false);

        var positions = new List<IbkrPosition>
        {
            new("NVDA", "STK", null, 100, 174.23m),
            new("AAPL", "STK", null, 50,  208.15m),
        };

        var orders = new List<IbkrOpenOrder>
        {
            new(1, "NVDA", "STK", null, "SELL", "TRAIL", 100, "Submitted", 166.50, null),
            new(2, "NVDA", "STK", null, "SELL", "LMT",   100, "Submitted", null,   188.00),
        };

        var message = IbSnapshotFormatter.BuildSnapshotMessage(account, positions, orders);

        await discord.NotifyIbSnapshotAsync(message);
    }
}
