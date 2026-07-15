using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class IbkrBrokerServiceTests
{
    // SyncOrderId/SyncReqId and NextReqId() don't require a live Gateway connection —
    // construction alone (no Connect()) is enough, same pattern as
    // IbkrConnectionTests.BuildBrokerService. NextReqId() is private, so its return value
    // is observed via reflection rather than through a public call path that would
    // otherwise require EnsureConnected() to be true.
    [Fact]
    public void SyncReqId_SeedsNextReqIdWellClearOfDefault()
    {
        var options = Options.Create(new IbkrOptions
        {
            Host      = "127.0.0.1",
            Port      = 9999,
            ClientId  = 99,
            AccountId = "",
            TimeoutMs = 2000
        });

        var connection = new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance));

        var broker = new IbkrBrokerService(connection, options, NullLogger<IbkrBrokerService>.Instance);

        broker.SyncReqId(50);

        var nextReqIdMethod = typeof(IbkrBrokerService).GetMethod(
            "NextReqId", BindingFlags.NonPublic | BindingFlags.Instance);
        var nextReqId = (int)nextReqIdMethod!.Invoke(broker, null)!;

        nextReqId.Should().Be(50 + 100_000 + 1);

        connection.Dispose();
    }
}
