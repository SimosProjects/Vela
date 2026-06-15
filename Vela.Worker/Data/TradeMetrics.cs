using System.ComponentModel.DataAnnotations.Schema;

namespace Vela.Worker.Data;

/// <summary>
/// Represents a single trade's full lifecycle metrics for analytics and performance reporting.
/// Written on open with timing and entry data, updated on close with exit and P&L data.
/// Separate from TradeRecord (in-memory) and CsvTradeLogger (bookkeeping), this is the
/// analytics source of truth persisted to PostgreSQL.
/// </summary>
public class TradeMetric
{
    // Primary key: matches the OrderId from IBKR so we can correlate
    public string Id { get; set; } = string.Empty;

    // Alert reference, allows JOIN to alerts table for filter efficiency analysis
    public string? AlertId { get; set; }

    // Trader and instrument
    public string? TraderName { get; set; }
    public string? Symbol { get; set; }
    public string? TradeType { get; set; }
    public string? Direction { get; set; }
    public string? OptionsContract { get; set; }
    public bool IsAverage { get; set; }

    // Timing
    // When the alert first arrived at Vela (SignalR callback or REST poll)
    public DateTimeOffset AlertReceivedAt { get; set; }

    // When PlaceOrderAsync was called at the broker
    public DateTimeOffset OrderSubmittedAt { get; set; }

    // When IBKR confirmed the fill
    public DateTimeOffset OrderFilledAt { get; set; }

    // Total ms from alert received to order filled
    public int LatencyMs { get; set; }

    // Pricing and slippage
    public decimal AlertedPrice { get; set; }
    public decimal FillPrice { get; set; }
    public decimal SlippagePct { get; set; }

    // Position sizing 
    public int Quantity { get; set; }
    public decimal EntryAmount { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TargetPrice { get; set; }

    // Account snapshot at time of entry
    public decimal AccountBalanceAtEntry { get; set; }
    public decimal OpenPositionsValueAtEntry { get; set; }
    public decimal ExposurePct { get; set; }

    // Exit data (null until the trade closes)
    public decimal? ExitPrice { get; set; }
    public decimal? ExitAmount { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    // -- P&L (null until closed) --
    public decimal? PnL { get; set; }

    // Percentage gain or loss relative to entry amount
    public decimal? PnLPct { get; set; }

    // Exit execution analytics (null until closed via Xtrades alert)
    public int? ExitLatencyMs { get; set; }
    public decimal? ExitSlippagePct { get; set; }

    // xScore of the trader at time of alert, enables performance analysis by score band
    [Column("x_score")]
    public decimal? XScore { get; set; }

    // Discord rank of the trader at time of alert, enables rank-filtered performance queries
    [Column("discord_rank")]
    public string? DiscordRank { get; set; }
}