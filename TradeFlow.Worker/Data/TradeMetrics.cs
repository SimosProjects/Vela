namespace TradeFlow.Worker.Data;

/// <summary>
/// Represents a single trade's full lifecycle metrics for analytics and performance reporting.
/// Written on open with timing and entry data, updated on close with exit and P&amp;L data.
/// Separate from TradeRecord (in-memory) and CsvTradeLogger (bookkeeping) — this is the
/// analytics source of truth persisted to PostgreSQL.
/// </summary>
public class TradeMetric
{
    // Primary key — matches the OrderId from IBKR so we can correlate
    public string Id { get; set; } = string.Empty;

    // Alert reference — allows JOIN to alerts table for filter efficiency analysis
    public string? AlertId { get; set; }

    // Trader and instrument
    public string? TraderName { get; set; }
    public string? Symbol { get; set; }
    public string? TradeType { get; set; }
    public string? Direction { get; set; }
    public string? OptionsContract { get; set; }
    public bool IsAverage { get; set; }

    // -- Timing --

    // When the alert first arrived at TradeFlow (SignalR callback or REST poll)
    // This is the start of our latency measurement
    public DateTimeOffset AlertReceivedAt { get; set; }

    // When PlaceOrderAsync was called at the broker — end of our processing pipeline
    public DateTimeOffset OrderSubmittedAt { get; set; }

    // When IBKR confirmed the fill — end of full round trip
    public DateTimeOffset OrderFilledAt { get; set; }

    // Total ms from alert received to order filled — the key latency metric
    public int LatencyMs { get; set; }

    // -- Pricing and slippage --

    // Price from the Xtrades alert that triggered this trade
    public decimal AlertedPrice { get; set; }

    // Actual fill price from IBKR
    public decimal FillPrice { get; set; }

    // Percentage difference between alerted and filled price
    // Positive = filled higher than alerted (worse for buyer)
    // Negative = filled lower than alerted (better for buyer)
    public decimal SlippagePct { get; set; }

    // -- Position sizing --
    public int Quantity { get; set; }
    public decimal EntryAmount { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TargetPrice { get; set; }

    // -- Account snapshot at time of entry --
    // Captured to track portfolio risk exposure over time
    public decimal AccountBalanceAtEntry { get; set; }
    public decimal OpenPositionsValueAtEntry { get; set; }

    // Percentage of account balance committed to open positions at time of this trade
    public decimal ExposurePct { get; set; }

    // -- Exit data (null until the trade closes) --
    public decimal? ExitPrice { get; set; }
    public decimal? ExitAmount { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    // -- P&L (null until closed) --
    public decimal? PnL { get; set; }

    // Percentage gain or loss relative to entry amount
    public decimal? PnLPct { get; set; }

    // -- Exit execution analytics (null until closed via Xtrades alert) --

    // Time from exit alert received to close order filled in milliseconds
    public int? ExitLatencyMs { get; set; }

    // Percentage difference between alerted exit price and actual fill price
    // Positive = filled higher than alerted (better for seller)
    // Negative = filled lower than alerted (worse for seller)
    public decimal? ExitSlippagePct { get; set; }

    // xScore of the trader at time of alert, enables performance analysis by score band
    public decimal? XScore { get; set; }
}