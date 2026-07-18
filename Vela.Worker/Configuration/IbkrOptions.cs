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

    // Bounded wait for an execDetails-tracked close/stop/target order to reach its full
    // requested quantity once a partial fill has been observed. Deliberately separate from
    // TimeoutMs (a quick request/acknowledgment timeout, capped at 2 minutes) — a thin
    // contract can take many minutes to fully fill across several partial executions (the
    // 2026-07-17 UBER incident took ~9m40s end to end). Configurable so tests can shrink it.
    [Range(10, 3600, ErrorMessage = "ExecDetailsFullFillWaitSeconds must be between 10 and 3600.")]
    public int ExecDetailsFullFillWaitSeconds { get; set; } = 900;
}