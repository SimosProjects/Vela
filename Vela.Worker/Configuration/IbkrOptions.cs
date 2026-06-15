using System.ComponentModel.DataAnnotations;

namespace Vela.Worker.Configuration;

public class IbkrOptions
{
    // IB Gateway host, localhost for local dev, gateway container name for Docker
    public string Host { get; set; } = "127.0.0.1";

    // 4002 = paper trading, 4001 = live trading
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int Port { get; set; } = 4002;

    // Unique client ID, must be different for each connected client
    public int ClientId { get; set; } = 1;

    // Paper or live account number, set via environment/secrets, not appsettings
    public string AccountId { get; set; } = string.Empty;

    // Timeout for connection and order confirmations in milliseconds
    [Range(1000, 120000, ErrorMessage = "TimeoutMs must be between 1000 and 120000.")]
    public int TimeoutMs { get; set; } = 5000;
}