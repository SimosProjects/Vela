using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
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

    // No live Gateway means GetAllOpenOrdersAsync (called internally by
    // ReRegisterStopCallbacksAsync) returns an empty snapshot — every stored order ID is
    // therefore "not found live" by construction, exercising the stale-ID path without
    // needing to fake an actual open orders response.
    [Fact]
    public async Task ReRegisterStopCallbacksAsync_StopOrderIdNotInLiveSnapshot_NotRegisteredAndWarns()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var position = new OpenPosition
        {
            OrderId       = "10768",
            StopOrderId   = "11241",
            TargetOrderId = null,
            Symbol        = "BROS"
        };

        await broker.ReRegisterStopCallbacksAsync([position]);

        broker.IsKnownOrder(11241).Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("BROS") && w.Contains("11241"));

        connection.Dispose();
    }

    // ClassifyStaleOrderId is private, so its (Live, Trackable) tuple is observed via
    // reflection, same pattern as SyncReqId_SeedsNextReqIdWellClearOfDefault. This is the
    // exact tri-state distinction requested: a live OrderId-0 match must not be conflated
    // with either "stale" (genuinely no live order) or "fully tracked" (real ID to register).
    [Fact]
    public void ClassifyStaleOrderId_LiveOrderIdZeroMatch_IsLiveButNotTrackable_AndDoesNotLogStale()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var liveOrders = new List<IbkrOpenOrder>
        {
            new(0, "GE", "STK", null, "SELL", "TRAIL", 11, "PreSubmitted", 305.91, null)
        };

        var method = typeof(IbkrBrokerService).GetMethod(
            "ClassifyStaleOrderId", BindingFlags.NonPublic | BindingFlags.Instance);
        var (live, trackable) = ((bool, bool))method!.Invoke(
            broker, [ "GE", "Stop", 11252, "GE", liveOrders, true ])!;

        live.Should().BeTrue();
        trackable.Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("OrderId 0") && w.Contains("GE"));
        logger.Warnings.Should().NotContain(w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));

        connection.Dispose();
    }

    [Fact]
    public void ClassifyStaleOrderId_NoLiveMatch_IsStale_AndLogsStaleMessage()
    {
        var (broker, connection, logger) = BuildDisconnectedBroker();

        var method = typeof(IbkrBrokerService).GetMethod(
            "ClassifyStaleOrderId", BindingFlags.NonPublic | BindingFlags.Instance);
        var (live, trackable) = ((bool, bool))method!.Invoke(
            broker, [ "GE", "Stop", 11252, "GE", new List<IbkrOpenOrder>(), true ])!;

        live.Should().BeFalse();
        trackable.Should().BeFalse();
        logger.Warnings.Should().ContainSingle(w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));

        connection.Dispose();
    }

    private static (IbkrBrokerService Broker, IbkrConnectionService Connection, CapturingLogger<IbkrBrokerService> Logger)
        BuildDisconnectedBroker()
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

        var logger = new CapturingLogger<IbkrBrokerService>();
        var broker = new IbkrBrokerService(connection, options, logger);

        return (broker, connection, logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
