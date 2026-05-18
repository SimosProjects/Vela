using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Integration;

// These tests require IB Gateway running on localhost:4002.
public class IbkrConnectionTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_IBKR_TESTS") == "true";

    private static IbkrConnectionService BuildConnectionService(int clientId = 99)
    {
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 4002,
            ClientId = clientId,
            AccountId = Environment.GetEnvironmentVariable("IBKR__ACCOUNTID") ?? "",
            TimeoutMs = 5000
        });

        var discord = new DiscordNotificationService(
            NullLogger<DiscordNotificationService>.Instance);

        return new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            discord);
    }

    private static IbkrBrokerService BuildBrokerService(IbkrConnectionService connection, int clientId = 99)
    {
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 4002,
            ClientId = clientId,
            AccountId = Environment.GetEnvironmentVariable("IBKR__ACCOUNTID") ?? "",
            TimeoutMs = 5000
        });

        return new IbkrBrokerService(
            connection,
            options,
            NullLogger<IbkrBrokerService>.Instance);
    }

    // Builds a connection service pointing at a port with nothing running
    private static IbkrConnectionService BuildDownConnectionService() =>
        new IbkrConnectionService(
            Options.Create(new IbkrOptions
            {
                Host      = "127.0.0.1",
                Port      = 9999,
                ClientId  = 99,
                AccountId = "",
                TimeoutMs = 2000
            }),
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance));

    // -- Gateway running tests --

    [Fact]
    public void Connect_WithGatewayRunning_ReturnsTrue()
    {
        if (ShouldSkip) return;

        var service = BuildConnectionService();
        var connected = service.Connect();

        connected.Should().BeTrue();
        service.IsConnected.Should().BeTrue();

        service.Dispose();
    }

    [Fact]
    public async Task GetAccountBalanceAsync_ReturnsPositiveBalance()
    {
        if (ShouldSkip) return;

        var connection = BuildConnectionService();
        var broker = BuildBrokerService(connection);

        var balance = await broker.GetAccountBalanceAsync();

        balance.Should().BeGreaterThan(0);

        connection.Dispose();
    }

    [Fact]
    public async Task GetOpenPositionsValueAsync_ReturnsNonNegativeValue()
    {
        if (ShouldSkip) return;

        var connection = BuildConnectionService();
        var broker = BuildBrokerService(connection);

        var value = await broker.GetOpenPositionsValueAsync();

        value.Should().BeGreaterThanOrEqualTo(0);

        connection.Dispose();
    }

    // -- Gateway down tests --

    [Fact]
    public async Task GetAccountBalanceAsync_WithGatewayDown_ReturnsZero()
    {
        if (ShouldSkip) return;

        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1", Port = 9999, ClientId = 99, AccountId = "", TimeoutMs = 2000
        });

        var connection = BuildDownConnectionService();
        var broker = new IbkrBrokerService(
            connection, options, NullLogger<IbkrBrokerService>.Instance);

        var balance = await broker.GetAccountBalanceAsync();

        balance.Should().Be(0);

        connection.Dispose();
    }

    [Fact]
    public async Task GetOpenPositionsValueAsync_WithGatewayDown_ReturnsZero()
    {
        if (ShouldSkip) return;

        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1", Port = 9999, ClientId = 99, AccountId = "", TimeoutMs = 2000
        });

        var connection = BuildDownConnectionService();
        var broker = new IbkrBrokerService(
            connection, options, NullLogger<IbkrBrokerService>.Instance);

        var value = await broker.GetOpenPositionsValueAsync();

        value.Should().Be(0);

        connection.Dispose();
    }

    [Fact]
    public void Connect_WithGatewayDown_ReturnsFalse()
    {
        if (ShouldSkip) return;

        var connection = BuildDownConnectionService();
        var connected = connection.Connect();

        connected.Should().BeFalse();
        connection.IsConnected.Should().BeFalse();

        connection.Dispose();
    }
}