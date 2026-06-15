namespace Vela.Worker.Data;

/// <summary>
/// Defines the contract for writing and updating trade analytics records.
/// Intentionally write-only from the Worker's perspective, reads are done
/// by the Vela.Analytics project directly via DbContext.
/// </summary>
public interface ITradeMetricsRepository
{
    /// <summary>
    /// Writes a new trade metric row when a position is opened.
    /// Called immediately after a successful broker fill.
    /// </summary>
    Task OpenAsync(TradeMetric metric, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing trade metric row with exit data when a position closes.
    /// Called after RegisterClose confirms the position is closed.
    /// </summary>
    Task CloseAsync(
        string orderId,
        decimal exitPrice,
        decimal exitAmount,
        decimal pnl,
        decimal pnlPct,
        string outcome,
        DateTimeOffset closedAt,
        int? exitLatencyMs,
        decimal? exitSlippagePct,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of trades opened today in ET based on order_filled_at.
    /// Used on startup to seed TradeGuard's daily counter after a restart.
    /// </summary>
    Task<int> GetTodayTradeCountAsync(DateOnly dateEt, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest numeric order ID stored in trade_metrics.
    /// Used on startup to ensure the next order ID never collides with an
    /// existing DB row after a Gateway weekly ID reset.
    /// </summary>
    Task<int> GetMaxOrderIdAsync(CancellationToken ct = default);
}

/// <inheritdoc/>
public class TradeMetricsRepository : ITradeMetricsRepository
{
    private readonly VelaDbContext _db;
    private readonly ILogger<TradeMetricsRepository> _logger;

    public TradeMetricsRepository(VelaDbContext db, ILogger<TradeMetricsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task OpenAsync(TradeMetric metric, CancellationToken ct = default)
    {
        try
        {
            _db.TradeMetrics.Add(metric);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Trade metric opened — {Symbol} {TradeType} | Latency: {LatencyMs}ms | Slippage: {SlippagePct:F2}%",
                metric.Symbol, metric.TradeType, metric.LatencyMs, metric.SlippagePct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        {
            _logger.LogWarning(
                "Trade metric skipped — duplicate OrderId {OrderId} for {Symbol} (order ID reused across restart)",
                metric.Id, metric.Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write open trade metric for {Symbol} OrderId: {OrderId}",
                metric.Symbol, metric.Id);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(
        string orderId,
        decimal exitPrice,
        decimal exitAmount,
        decimal pnl,
        decimal pnlPct,
        string outcome,
        DateTimeOffset closedAt,
        int? exitLatencyMs,
        decimal? exitSlippagePct,
        CancellationToken ct = default)
    {
        try
        {
            var updated = await _db.TradeMetrics
                .Where(m => m.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ExitPrice,       exitPrice)
                    .SetProperty(m => m.ExitAmount,      exitAmount)
                    .SetProperty(m => m.PnL,             pnl)
                    .SetProperty(m => m.PnLPct,          pnlPct)
                    .SetProperty(m => m.Outcome,         outcome)
                    .SetProperty(m => m.ClosedAt,        closedAt)
                    .SetProperty(m => m.ExitLatencyMs,   exitLatencyMs)
                    .SetProperty(m => m.ExitSlippagePct, exitSlippagePct),
                ct);

            if (updated == 0)
                _logger.LogWarning(
                    "Trade metric close: no row found for OrderId {OrderId}", orderId);
            else
                _logger.LogInformation(
                    "Trade metric closed — OrderId: {OrderId} | P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
                    orderId, pnl, pnlPct, outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update close trade metric for OrderId: {OrderId}", orderId);
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetTodayTradeCountAsync(DateOnly dateEt, CancellationToken ct = default)
    {
        try
        {
            var easternTime = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

            var startUtc = TimeZoneInfo.ConvertTimeToUtc(
                dateEt.ToDateTime(TimeOnly.MinValue), easternTime);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(
                dateEt.ToDateTime(TimeOnly.MaxValue), easternTime);

            return await _db.TradeMetrics
                .Where(m => m.OrderFilledAt >= startUtc && m.OrderFilledAt <= endUtc)
                .CountAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query today's trade count from trade_metrics");
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetMaxOrderIdAsync(CancellationToken ct = default)
    {
        try
        {
            var ids = await _db.TradeMetrics
                .Select(m => m.Id)
                .ToListAsync(ct);

            return ids.Count == 0
                ? 0
                : ids.Select(id => int.TryParse(id, out var n) ? n : 0).Max();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query max order ID from trade_metrics");
            return 0;
        }
    }
}