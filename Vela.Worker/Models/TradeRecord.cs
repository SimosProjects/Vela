namespace Vela.Worker.Models;

/// <summary>
/// Represents a trade lifecycle entry for CSV tracking and in-memory position management.
/// Written on open, updated on close.
/// </summary>
public record TradeRecord
{
    // Identity
    public required string AlertId { get; init; }
    public required string OrderId { get; init; }
    public required string? StopOrderId { get; set; }
    public required string? TargetOrderId { get; init; }
    public required string UserName { get; init; }
    public required decimal XScore { get; set; }
    public required string? DiscordRank { get; init; }

    // Instrument
    public required string Symbol { get; init; }
    public required TradeType TradeType { get; init; }
    public required string? OptionsContract { get; init; }
    public required string? Direction { get; init; }
    public required decimal? Strike { get; init; }
    public required string? Expiration { get; init; }

    // Position
    public required int Quantity { get; set; }
    public required decimal EntryPrice { get; init; }
    public required decimal EntryAmount { get; init; }
    public required decimal StopPrice { get; init; }
    public required decimal TargetPrice { get; init; }

    // Timing
    public required DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }

    // Exit
    public decimal? ExitPrice { get; set; }
    public decimal? ExitAmount { get; set; }
    public decimal? PnL { get; set; }
    public decimal? PnLPercent { get; set; }

    // Entry execution quality
    public int? LatencyMs { get; set; }
    public decimal? SlippagePct { get; set; }

    // Exit execution quality
    public int? ExitLatencyMs { get; set; }
    public decimal? ExitSlippagePct { get; set; }

    // Status
    public TradeStatus Status { get; set; } = TradeStatus.Open;
    public TradeOutcome Result { get; set; } = TradeOutcome.Open;
    public bool IsAverage { get; init; } = false;
    public bool HasAveraged { get; set; } = false;
}