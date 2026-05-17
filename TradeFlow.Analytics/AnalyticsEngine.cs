namespace TradeFlow.Analytics;

/// <summary>
/// Strongly typed container for all calculated analytics values.
/// Passed to the report generator after all queries are complete.
/// </summary>
public class ReportData
{
    public ReportType ReportType { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }

    // -- Overview --
    public int TotalAlerts { get; init; }
    public int TotalTrades { get; init; }
    public int OpenTrades { get; init; }
    public int ClosedTrades { get; init; }
    public decimal FilterRatePct { get; init; }        
    public decimal OptionsTradesPct { get; init; }
    public decimal StockTradesPct { get; init; }

    // -- Win/Loss --
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int BreakEvens { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgWinPct { get; init; }         
    public decimal AvgLossPct { get; init; }         
    public decimal AvgPnLPerTrade { get; init; }       
    public decimal TotalPnL { get; init; }
    public decimal LargestWin { get; init; }
    public decimal LargestLoss { get; init; }
    public int MaxConsecutiveLosses { get; init; }

    // -- Latency --
    public double AvgLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }

    // -- Slippage --
    public decimal AvgSlippagePct { get; init; }
    public decimal MaxSlippagePct { get; init; }

    // -- Exposure --
    public decimal AvgExposurePct { get; init; }
    public decimal MaxExposurePct { get; init; }

    // -- Outcome breakdown --
    public int TargetHits { get; init; }
    public int StoppedOuts { get; init; }
    public int XtradesExits { get; init; }

    // -- Per trader --
    public List<TraderStats> TraderBreakdown { get; init; } = [];

    // -- Per symbol --
    public List<SymbolStats> SymbolBreakdown { get; init; } = [];

    // -- Daily P&L series for chart --
    public List<DailyPnL> DailyPnLSeries { get; init; } = [];

    // -- All trades for detail table --
    public List<TradeRow> AllTrades { get; init; } = [];
}

public class TraderStats
{
    public string TraderName { get; init; } = string.Empty;
    public int TotalTrades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgWinPct { get; init; }
    public decimal AvgLossPct { get; init; }
    public decimal AvgPnLPerTrade { get; init; }
    public decimal TotalPnL { get; init; }
}

public class SymbolStats
{
    public string Symbol { get; init; } = string.Empty;
    public int TotalTrades { get; init; }
    public int Wins { get; init; }
    public decimal WinRatePct { get; init; }
    public decimal AvgPnLPerTrade { get; init; }
    public decimal TotalPnL { get; init; }
}

public class DailyPnL
{
    public DateOnly Date { get; init; }
    public decimal DayPnL { get; init; }
    public decimal CumulativePnL { get; init; }
}

public class TradeRow
{
    public string OrderId { get; init; } = string.Empty;
    public string? TraderName { get; init; }
    public string? Symbol { get; init; }
    public string? TradeType { get; init; }
    public string? Direction { get; init; }
    public bool IsAverage { get; init; }
    public DateTimeOffset AlertReceivedAt { get; init; }
    public int LatencyMs { get; init; }
    public decimal AlertedPrice { get; init; }
    public decimal FillPrice { get; init; }
    public decimal SlippagePct { get; init; }
    public int Quantity { get; init; }
    public decimal EntryAmount { get; init; }
    public decimal StopPrice { get; init; }
    public decimal TargetPrice { get; init; }
    public decimal ExposurePct { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal? PnL { get; init; }
    public decimal? PnLPct { get; init; }
    public string? Outcome { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>
/// Queries the trade_metrics table and calculates all analytics values
/// for the given date range.
/// </summary>
public class AnalyticsEngine
{
    private readonly TradeFlowDbContext _db;
    private readonly ILogger<AnalyticsEngine> _logger;

    private static readonly TimeZoneInfo Et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public AnalyticsEngine(TradeFlowDbContext db, ILogger<AnalyticsEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Runs all queries and calculations for the given options, returning
    /// a fully populated ReportData ready for the report generator.
    /// </summary>
    public async Task<ReportData> RunAsync(AnalyticsOptions options)
    {
        _logger.LogInformation(
            "Running analytics: {Report} | {From:yyyy-MM-dd} to {To:yyyy-MM-dd}",
            options.Report, options.From, options.To);

        // Load all trades in the period into memory, volumes are low enough
        // that in-memory calculation is cleaner than complex SQL expressions
        var trades = await _db.TradeMetrics
            .AsNoTracking()
            .Where(m => m.AlertReceivedAt >= options.From
                     && m.AlertReceivedAt <  options.To)
            .OrderBy(m => m.AlertReceivedAt)
            .ToListAsync();

        _logger.LogInformation("Loaded {Count} trade metrics for period.", trades.Count);

        // Total alerts in the same period from the alerts table
        var totalAlerts = await _db.Alerts
            .AsNoTracking()
            .Where(a => a.IngestedAt >= options.From
                     && a.IngestedAt <  options.To)
            .CountAsync();

        var closed = trades.Where(t => t.ClosedAt.HasValue).ToList();
        var open   = trades.Where(t => !t.ClosedAt.HasValue).ToList();

        var wins      = closed.Where(t => t.PnL > 0).ToList();
        var losses    = closed.Where(t => t.PnL < 0).ToList();
        var breakEvens = closed.Where(t => t.PnL == 0).ToList();

        var options_trades = trades.Where(t => t.TradeType == "Options").ToList();
        var stock_trades   = trades.Where(t => t.TradeType == "Stock").ToList();

        return new ReportData
        {
            ReportType  = options.Report,
            From        = options.From,
            To          = options.To,
            GeneratedAt = DateTimeOffset.UtcNow,

            // Overview
            TotalAlerts      = totalAlerts,
            TotalTrades      = trades.Count,
            OpenTrades       = open.Count,
            ClosedTrades     = closed.Count,
            FilterRatePct    = totalAlerts > 0
                ? Math.Round((decimal)trades.Count / totalAlerts * 100, 1)
                : 0,
            OptionsTradesPct = trades.Count > 0
                ? Math.Round((decimal)options_trades.Count / trades.Count * 100, 1)
                : 0,
            StockTradesPct   = trades.Count > 0
                ? Math.Round((decimal)stock_trades.Count / trades.Count * 100, 1)
                : 0,

            // Win/Loss
            Wins             = wins.Count,
            Losses           = losses.Count,
            BreakEvens       = breakEvens.Count,
            WinRatePct       = closed.Count > 0
                ? Math.Round((decimal)wins.Count / closed.Count * 100, 1)
                : 0,
            AvgWinPct        = wins.Count > 0
                ? Math.Round(wins.Average(t => t.PnLPct ?? 0), 2)
                : 0,
            AvgLossPct       = losses.Count > 0
                ? Math.Round(losses.Average(t => t.PnLPct ?? 0), 2)
                : 0,
            AvgPnLPerTrade   = closed.Count > 0
                ? Math.Round(closed.Average(t => t.PnL ?? 0), 2)
                : 0,
            TotalPnL         = closed.Sum(t => t.PnL ?? 0),
            LargestWin       = wins.Count > 0
                ? wins.Max(t => t.PnL ?? 0)
                : 0,
            LargestLoss      = losses.Count > 0
                ? losses.Min(t => t.PnL ?? 0)
                : 0,
            MaxConsecutiveLosses = CalculateMaxConsecutiveLosses(closed),

            // Latency
            AvgLatencyMs = trades.Count > 0
                ? Math.Round(trades.Average(t => (double)t.LatencyMs), 0)
                : 0,
            P50LatencyMs = Percentile(trades.Select(t => (double)t.LatencyMs).ToList(), 50),
            P95LatencyMs = Percentile(trades.Select(t => (double)t.LatencyMs).ToList(), 95),
            MaxLatencyMs = trades.Count > 0
                ? trades.Max(t => (double)t.LatencyMs)
                : 0,

            // Slippage
            AvgSlippagePct = trades.Count > 0
                ? Math.Round(trades.Average(t => t.SlippagePct), 3)
                : 0,
            MaxSlippagePct = trades.Count > 0
                ? trades.Max(t => t.SlippagePct)
                : 0,

            // Exposure
            AvgExposurePct = trades.Count > 0
                ? Math.Round(trades.Average(t => t.ExposurePct), 1)
                : 0,
            MaxExposurePct = trades.Count > 0
                ? trades.Max(t => t.ExposurePct)
                : 0,

            // Outcome breakdown
            TargetHits   = closed.Count(t => t.Outcome == "TargetHit"),
            StoppedOuts  = closed.Count(t => t.Outcome == "StoppedOut"),
            XtradesExits = closed.Count(t => t.Outcome == "XtradesExit"),

            // Per trader
            TraderBreakdown = trades
                .GroupBy(t => t.TraderName ?? "Unknown")
                .Select(g =>
                {
                    var traderClosed = g.Where(t => t.ClosedAt.HasValue).ToList();
                    var traderWins   = traderClosed.Where(t => t.PnL > 0).ToList();
                    var traderLosses = traderClosed.Where(t => t.PnL < 0).ToList();
                    return new TraderStats
                    {
                        TraderName     = g.Key,
                        TotalTrades    = g.Count(),
                        Wins           = traderWins.Count,
                        Losses         = traderLosses.Count,
                        WinRatePct     = traderClosed.Count > 0
                            ? Math.Round((decimal)traderWins.Count / traderClosed.Count * 100, 1)
                            : 0,
                        AvgWinPct      = traderWins.Count > 0
                            ? Math.Round(traderWins.Average(t => t.PnLPct ?? 0), 2)
                            : 0,
                        AvgLossPct     = traderLosses.Count > 0
                            ? Math.Round(traderLosses.Average(t => t.PnLPct ?? 0), 2)
                            : 0,
                        AvgPnLPerTrade = traderClosed.Count > 0
                            ? Math.Round(traderClosed.Average(t => t.PnL ?? 0), 2)
                            : 0,
                        TotalPnL       = traderClosed.Sum(t => t.PnL ?? 0),
                    };
                })
                .OrderByDescending(t => t.AvgPnLPerTrade)
                .ToList(),

            // Per symbol
            SymbolBreakdown = trades
                .GroupBy(t => t.Symbol ?? "Unknown")
                .Select(g =>
                {
                    var symClosed = g.Where(t => t.ClosedAt.HasValue).ToList();
                    var symWins   = symClosed.Where(t => t.PnL > 0).ToList();
                    return new SymbolStats
                    {
                        Symbol         = g.Key,
                        TotalTrades    = g.Count(),
                        Wins           = symWins.Count,
                        WinRatePct     = symClosed.Count > 0
                            ? Math.Round((decimal)symWins.Count / symClosed.Count * 100, 1)
                            : 0,
                        AvgPnLPerTrade = symClosed.Count > 0
                            ? Math.Round(symClosed.Average(t => t.PnL ?? 0), 2)
                            : 0,
                        TotalPnL       = symClosed.Sum(t => t.PnL ?? 0),
                    };
                })
                .OrderByDescending(s => s.TotalPnL)
                .ToList(),

            // Daily P&L series, grouped by ET date for accurate market-day alignment
            DailyPnLSeries = BuildDailyPnLSeries(closed),

            // All trades detail table
            AllTrades = trades.Select(t => new TradeRow
            {
                OrderId         = t.Id,
                TraderName      = t.TraderName,
                Symbol          = t.Symbol,
                TradeType       = t.TradeType,
                Direction       = t.Direction,
                IsAverage       = t.IsAverage,
                AlertReceivedAt = t.AlertReceivedAt,
                LatencyMs       = t.LatencyMs,
                AlertedPrice    = t.AlertedPrice,
                FillPrice       = t.FillPrice,
                SlippagePct     = t.SlippagePct,
                Quantity        = t.Quantity,
                EntryAmount     = t.EntryAmount,
                StopPrice       = t.StopPrice,
                TargetPrice     = t.TargetPrice,
                ExposurePct     = t.ExposurePct,
                ExitPrice       = t.ExitPrice,
                PnL             = t.PnL,
                PnLPct          = t.PnLPct,
                Outcome         = t.Outcome,
                ClosedAt        = t.ClosedAt,
            }).ToList(),
        };
    }

    // Groups closed trades by ET date and builds a cumulative P&L series for the chart
    private static List<DailyPnL> BuildDailyPnLSeries(List<TradeMetric> closed)
    {
        var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var byDay = closed
            .GroupBy(t => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(t.ClosedAt!.Value, et).DateTime))
            .OrderBy(g => g.Key)
            .ToList();

        var cumulative = 0m;
        return byDay.Select(g =>
        {
            var dayPnL = g.Sum(t => t.PnL ?? 0);
            cumulative += dayPnL;
            return new DailyPnL
            {
                Date          = g.Key,
                DayPnL        = Math.Round(dayPnL, 2),
                CumulativePnL = Math.Round(cumulative, 2),
            };
        }).ToList();
    }

    // Walks trades in order and tracks the longest consecutive losing streak
    private static int CalculateMaxConsecutiveLosses(List<TradeMetric> closed)
    {
        int max = 0, current = 0;
        foreach (var trade in closed.OrderBy(t => t.ClosedAt))
        {
            if (trade.PnL < 0)
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }
        return max;
    }

    // Calculates a percentile value from a sorted list
    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var index = (percentile / 100.0) * (values.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return values[lower];
        return values[lower] + (index - lower) * (values[upper] - values[lower]);
    }
}