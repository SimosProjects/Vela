using System.Globalization;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

// Writes and maintains two CSV trade logs, one for options, one for stocks.
// Each file has a header row, one row per trade, and a summary block at the bottom.
// Thread-safe via SemaphoreSlim, both AlertPollingService and SignalRListenerService
// may call OpenTrade/CloseTrade concurrently.
public class CsvTradeLogger
{
    private readonly string _tradesDir;
    private readonly string _optionsPath;
    private readonly string _stocksPath;
    private readonly string _archiveDir;
    private readonly string _weeklyDir;
    private readonly ILogger<CsvTradeLogger> _logger;

    private static readonly TimeZoneInfo EasternTime = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Separate locks per file, options and stocks writes never block each other
    private readonly SemaphoreSlim _optionsLock = new(1, 1);
    private readonly SemaphoreSlim _stocksLock  = new(1, 1);

    private static readonly string OptionsHeader =
        "Date Opened,Time Opened,Date Closed,Time Closed," +
        "Symbol,Contract,Direction,Strike,Expiration," +
        "Contracts,Entry Price,Entry Amount,Entry Latency (ms),Entry Slippage %," +
        "Exit Price,Exit Amount,Exit Latency (ms),Exit Slippage %," +
        "Status,Result,UserName,XScore,DiscordRank,P&L,P&L %,OrderId";

    private static readonly string StocksHeader =
        "Date Opened,Time Opened,Date Closed,Time Closed," +
        "Symbol,Shares,Entry Price,Entry Amount,Entry Latency (ms),Entry Slippage %," +
        "Exit Price,Exit Amount,Exit Latency (ms),Exit Slippage %," +
        "Status,Result,UserName,XScore,DiscordRank,P&L,P&L %,OrderId";

    // XScore column index per trade type, used to recover the value from existing rows
    // when LoadFromDatabase resets it to 0 on Worker restart.
    private const int OptionsXScoreCol  = 21;
    private const int StocksXScoreCol   = 17;

    // OrderId column index per trade type, used for reliable row matching on close.
    // Rows written before this column was added have no value here and fall back to
    // the fuzzy symbol+direction/quantity match for backward compatibility.
    private const int OptionsOrderIdCol = 25;
    private const int StocksOrderIdCol  = 21;

    public CsvTradeLogger(
        IConfiguration config,
        ILogger<CsvTradeLogger> logger)
    {
        _logger = logger;

        var configuredDir = config["Trades:Directory"];
        _tradesDir = configuredDir is not null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredDir))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "trades"));

        Directory.CreateDirectory(_tradesDir);

        _optionsPath = Path.Combine(_tradesDir, "options_trades.csv");
        _stocksPath  = Path.Combine(_tradesDir, "stocks_trades.csv");

        var configuredArchiveDir = config["Trades:ArchiveDirectory"];
        _archiveDir = configuredArchiveDir is not null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredArchiveDir))
            : Path.Combine(_tradesDir, "archive");

        // Weekly subfolder — closed trades only for realized P&L tracking
        _weeklyDir = Path.Combine(_archiveDir, "weekly");

        Directory.CreateDirectory(_archiveDir);
        Directory.CreateDirectory(_weeklyDir);

        EnsureHeadersExist();
    }

    /// <summary>
    /// Appends a new open trade row to the appropriate CSV file and updates the summary.
    /// </summary>
    public async Task OpenTradeAsync(TradeRecord trade, CancellationToken ct = default)
    {
        var semaphore = GetSemaphore(trade.TradeType);
        await semaphore.WaitAsync(ct);
        try
        {
            var path = GetPath(trade.TradeType);

            await StripSummaryAsync(path, ct);

            var row = BuildOpenRow(trade);
            await AppendRowAsync(path, row, ct);
            await UpdateSummaryAsync(path, trade.TradeType, ct);

            _logger.LogInformation(
                "CSV trade opened — {Type} {Symbol} × {Qty} @ ${Price:F2}",
                trade.TradeType, trade.Symbol, trade.Quantity, trade.EntryPrice);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Finds the matching open row and rewrites it with exit data, then updates the summary.
    /// Matches by OrderId first (reliable). Falls back to fuzzy symbol+direction/quantity match
    /// for rows written before the OrderId column was added.
    /// </summary>
    public async Task CloseTradeAsync(TradeRecord trade, CancellationToken ct = default)
    {
        var semaphore = GetSemaphore(trade.TradeType);
        await semaphore.WaitAsync(ct);
        try
        {
            var path = GetPath(trade.TradeType);
            await RewriteTradeRowAsync(path, trade, ct);
            await UpdateSummaryAsync(path, trade.TradeType, ct);

            _logger.LogInformation(
                "CSV trade closed — {Type} {Symbol} | Outcome: {Outcome} | P&L: {PnL:+$#,##0.00;-$#,##0.00}",
                trade.TradeType, trade.Symbol, trade.Result, trade.PnL ?? 0);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Returns the total realized P&L for all trades closed today in ET across both CSV files.
    /// Used by TradeGuard to enforce the daily loss limit before accepting new entries.
    /// Reads without acquiring the write semaphore, call sites are read-only and the
    /// minor race window between a concurrent write and this read is acceptable.
    /// </summary>
    public async Task<decimal> GetTodayRealizedPnLAsync(CancellationToken ct = default)
    {
        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        var options = await ReadTodayPnLFromFileAsync(_optionsPath, TradeType.Options, todayEt, ct);
        var stocks  = await ReadTodayPnLFromFileAsync(_stocksPath,  TradeType.Stock,   todayEt, ct);
        return options + stocks;
    }

    /// <summary>
    /// Archives the current week's CSV files and resets the working files to open positions only.
    /// Three outputs per file:
    ///   archive/ — full copy of the original (open + closed), complete record
    ///   archive/weekly/ — closed trades only for realized weekly P&L tracking
    ///   working file — open positions only, fresh summary for next week
    /// Called by MarketSchedulerService every Friday at 4:30pm ET.
    /// </summary>
    public async Task ArchiveWeekAsync(CancellationToken ct = default)
    {
        await _optionsLock.WaitAsync(ct);
        await _stocksLock.WaitAsync(ct);

        try
        {
            var today      = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);
            var dateSuffix = today.ToString("yyyy-MM-dd");

            await ArchiveFileAsync(
                _optionsPath,
                Path.Combine(_archiveDir, $"options_trades_{dateSuffix}.csv"),
                Path.Combine(_weeklyDir,  $"options_trades_{dateSuffix}.csv"),
                TradeType.Options,
                ct);

            await ArchiveFileAsync(
                _stocksPath,
                Path.Combine(_archiveDir, $"stocks_trades_{dateSuffix}.csv"),
                Path.Combine(_weeklyDir,  $"stocks_trades_{dateSuffix}.csv"),
                TradeType.Stock,
                ct);

            _logger.LogInformation(
                "Weekly CSV archive complete — full copy in {Archive}, weekly P&L in {Weekly}, week ending {Date}",
                _archiveDir, _weeklyDir, dateSuffix);
        }
        finally
        {
            _stocksLock.Release();
            _optionsLock.Release();
        }
    }

    // -- Helpers --

    // Reads all closed trades from a single CSV file for today and sums their P&L.
    // Options pnlIdx=23, Stocks pnlIdx=19 — indices stable; OrderId is appended after P&L%.
    private static async Task<decimal> ReadTodayPnLFromFileAsync(
        string path,
        TradeType tradeType,
        DateOnly todayEt,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return 0m;

        var lines = await File.ReadAllLinesAsync(path, ct);
        var total = 0m;

        var statusIdx     = tradeType == TradeType.Options ? 18 : 14;
        var closedDateIdx = 2;
        var pnlIdx        = tradeType == TradeType.Options ? 23 : 19;

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(",,")) continue;

            var cols = line.Split(',');
            if (cols.Length <= pnlIdx) continue;
            if (cols[statusIdx] != "Closed") continue;
            if (!DateOnly.TryParse(cols[closedDateIdx], out var closedDate)) continue;
            if (closedDate != todayEt) continue;

            if (decimal.TryParse(cols[pnlIdx].TrimStart('+'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var pnl))
                total += pnl;
        }

        return total;
    }

    // Three-way archive:
    //   fullArchivePath  — exact copy of source (open + closed), preserves complete history
    //   weeklyPath       — closed trades only for realized P&L review
    //   sourcePath       — rewritten with open rows only, fresh summary for next week
    private async Task ArchiveFileAsync(
        string sourcePath,
        string fullArchivePath,
        string weeklyPath,
        TradeType tradeType,
        CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Archive skipped — {Path} does not exist.", sourcePath);
            return;
        }

        var lines     = await File.ReadAllLinesAsync(sourcePath, ct);
        var header    = tradeType == TradeType.Options ? OptionsHeader : StocksHeader;
        var cols      = header.Split(',');
        var statusIdx = Array.IndexOf(cols, "Status");
        var pnlIdx    = Array.IndexOf(cols, "P&L");

        var dataLines = lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith(",,"))
            .ToList();

        var openLines   = dataLines.Where(l =>
            l.Split(',').Length > statusIdx && l.Split(',')[statusIdx] == "Open").ToList();
        var closedLines = dataLines.Where(l =>
            l.Split(',').Length > statusIdx && l.Split(',')[statusIdx] == "Closed").ToList();

        // Full archive — exact copy of working file with complete summary
        var fullLines = new List<string> { header };
        fullLines.AddRange(dataLines);
        AppendSummary(fullLines, dataLines, statusIdx, pnlIdx);
        await File.WriteAllLinesAsync(fullArchivePath, fullLines, ct);

        _logger.LogDebug(
            "Full archive written — {Count} trade(s) to {Path}", dataLines.Count, fullArchivePath);

        // Weekly archive — closed trades only with realized P&L summary
        var weeklyLines = new List<string> { header };
        weeklyLines.AddRange(closedLines);
        AppendSummary(weeklyLines, closedLines, statusIdx, pnlIdx);
        await File.WriteAllLinesAsync(weeklyPath, weeklyLines, ct);

        _logger.LogDebug(
            "Weekly archive written — {Count} closed trade(s) to {Path}", closedLines.Count, weeklyPath);

        // Working file — open rows only, fresh summary for next week
        var workingLines = new List<string> { header };
        workingLines.AddRange(openLines);
        AppendSummary(workingLines, openLines, statusIdx, pnlIdx);
        await File.WriteAllLinesAsync(sourcePath, workingLines, ct);

        _logger.LogDebug(
            "Working file reset — {Open} open position(s) carried forward, {Closed} closed removed",
            openLines.Count, closedLines.Count);
    }

    // Appends a recalculated summary block to the given line list based on the provided data rows.
    private static void AppendSummary(
        List<string> lines,
        List<string> dataLines,
        int statusIdx,
        int pnlIdx)
    {
        var total  = dataLines.Count;
        var closed = dataLines.Count(l => l.Split(',').Length > statusIdx &&
                                          l.Split(',')[statusIdx] == "Closed");
        var open   = total - closed;

        var wins = dataLines.Count(l =>
        {
            var c = l.Split(',');
            return c.Length > statusIdx && c[statusIdx] == "Closed" &&
                   decimal.TryParse(c.Length > pnlIdx ? c[pnlIdx].TrimStart('+') : "",
                       NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0;
        });

        var losses = dataLines.Count(l =>
        {
            var c = l.Split(',');
            return c.Length > statusIdx && c[statusIdx] == "Closed" &&
                   decimal.TryParse(c.Length > pnlIdx ? c[pnlIdx].TrimStart('+') : "",
                       NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p < 0;
        });

        var totalPnl = dataLines
            .Where(l =>
            {
                var c = l.Split(',');
                return c.Length > statusIdx && c[statusIdx] == "Closed";
            })
            .Sum(l =>
            {
                var c = l.Split(',');
                return decimal.TryParse(c.Length > pnlIdx ? c[pnlIdx].TrimStart('+') : "",
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
            });

        var winRate = closed > 0 ? (decimal)wins / closed * 100 : 0;
        var pnlSign = totalPnl >= 0 ? "+" : "";

        lines.Add("");
        lines.Add(",,SUMMARY");
        lines.Add($",,Total Trades,{total}");
        lines.Add($",,Open,{open},Closed,{closed}");
        lines.Add($",,Wins,{wins},Losses,{losses},Win Rate,{winRate:F1}%");
        lines.Add($",,Total P&L,{pnlSign}{totalPnl:F2}");
    }

    private void EnsureHeadersExist()
    {
        if (!File.Exists(_optionsPath))
            File.WriteAllText(_optionsPath, OptionsHeader + Environment.NewLine);

        if (!File.Exists(_stocksPath))
            File.WriteAllText(_stocksPath, StocksHeader + Environment.NewLine);
    }

    private static string FormatLatency(int? ms) => ms?.ToString() ?? "";

    private static string FormatSlippage(decimal? pct) =>
        pct.HasValue ? $"{(pct >= 0 ? "+" : "")}{pct:F2}%" : "";

    private string BuildOpenRow(TradeRecord t)
    {
        var et     = TimeZoneInfo.ConvertTime(t.OpenedAt, EasternTime);
        var xScore = t.XScore.ToString("F0");
        var rank   = t.DiscordRank ?? "";

        if (t.TradeType == TradeType.Options)
        {
            return string.Join(",",
                et.ToString("yyyy-MM-dd"),
                et.ToString("HH:mm:ss"),
                "", "",
                t.Symbol,
                t.OptionsContract ?? "",
                t.Direction ?? "",
                t.Strike?.ToString("F0") ?? "",
                FormatExpiration(t.Expiration),
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                FormatLatency(t.LatencyMs),
                FormatSlippage(t.SlippagePct),
                "", "", "", "",
                t.Status.ToString(),
                t.Result.ToString(),
                t.UserName ?? "",
                xScore,
                rank,
                "", "",
                t.OrderId ?? "");
        }
        else
        {
            return string.Join(",",
                et.ToString("yyyy-MM-dd"),
                et.ToString("HH:mm:ss"),
                "", "",
                t.Symbol,
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                FormatLatency(t.LatencyMs),
                FormatSlippage(t.SlippagePct),
                "", "", "", "",
                t.Status.ToString(),
                t.Result.ToString(),
                t.UserName ?? "",
                xScore,
                rank,
                "", "",
                t.OrderId ?? "");
        }
    }

    private string BuildClosedRow(TradeRecord t)
    {
        var openEt  = TimeZoneInfo.ConvertTime(t.OpenedAt, EasternTime);
        var closeEt = t.ClosedAt.HasValue
            ? TimeZoneInfo.ConvertTime(t.ClosedAt.Value, EasternTime)
            : (DateTimeOffset?)null;
        var pnlSign = t.PnL >= 0 ? "+" : "";
        var xScore  = t.XScore.ToString("F0");
        var rank    = t.DiscordRank ?? "";

        if (t.TradeType == TradeType.Options)
        {
            return string.Join(",",
                openEt.ToString("yyyy-MM-dd"),
                openEt.ToString("HH:mm:ss"),
                closeEt?.ToString("yyyy-MM-dd") ?? "",
                closeEt?.ToString("HH:mm:ss")   ?? "",
                t.Symbol,
                t.OptionsContract ?? "",
                t.Direction ?? "",
                t.Strike?.ToString("F0") ?? "",
                FormatExpiration(t.Expiration),
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                FormatLatency(t.LatencyMs),
                FormatSlippage(t.SlippagePct),
                t.ExitPrice?.ToString("F2")  ?? "",
                t.ExitAmount?.ToString("F2") ?? "",
                FormatLatency(t.ExitLatencyMs),
                FormatSlippage(t.ExitSlippagePct),
                t.Status.ToString(),
                t.Result.ToString(),
                t.UserName ?? "",
                xScore,
                rank,
                $"{pnlSign}{t.PnL:F2}",
                $"{pnlSign}{t.PnLPercent:F2}%",
                t.OrderId ?? "");
        }
        else
        {
            return string.Join(",",
                openEt.ToString("yyyy-MM-dd"),
                openEt.ToString("HH:mm:ss"),
                closeEt?.ToString("yyyy-MM-dd") ?? "",
                closeEt?.ToString("HH:mm:ss")   ?? "",
                t.Symbol,
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                FormatLatency(t.LatencyMs),
                FormatSlippage(t.SlippagePct),
                t.ExitPrice?.ToString("F2")  ?? "",
                t.ExitAmount?.ToString("F2") ?? "",
                FormatLatency(t.ExitLatencyMs),
                FormatSlippage(t.ExitSlippagePct),
                t.Status.ToString(),
                t.Result.ToString(),
                t.UserName ?? "",
                xScore,
                rank,
                $"{pnlSign}{t.PnL:F2}",
                $"{pnlSign}{t.PnLPercent:F2}%",
                t.OrderId ?? "");
        }
    }

    private static async Task AppendRowAsync(string path, string row, CancellationToken ct)
    {
        await File.AppendAllTextAsync(path, row + Environment.NewLine, ct);
    }

    private static async Task StripSummaryAsync(string path, CancellationToken ct)
    {
        var lines = (await File.ReadAllLinesAsync(path, ct)).ToList();
        var summaryStart = lines.FindIndex(l => l.StartsWith(",,SUMMARY"));
        if (summaryStart >= 0)
            lines = lines.Take(summaryStart).ToList();

        while (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        await File.WriteAllLinesAsync(path, lines, ct);
    }

    private async Task RewriteTradeRowAsync(string path, TradeRecord trade, CancellationToken ct)
    {
        var lines   = await File.ReadAllLinesAsync(path, ct);
        var updated = false;

        var exitPriceCol = trade.TradeType == TradeType.Options ? 14 : 10;
        var xScoreCol    = trade.TradeType == TradeType.Options ? OptionsXScoreCol : StocksXScoreCol;
        var orderIdCol   = trade.TradeType == TradeType.Options ? OptionsOrderIdCol : StocksOrderIdCol;

        for (var i = 0; i < lines.Length; i++)
        {
            if (i == 0 || lines[i].StartsWith(",,") || string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var cols = lines[i].Split(',');

            // Match by OrderId first, reliable for rows written with this version.
            // Rows written before the OrderId column was added have no value here and
            // fall through to the fuzzy match for backward compatibility.
            var matchedByOrderId = !string.IsNullOrEmpty(trade.OrderId)
                && cols.Length > orderIdCol
                && cols[orderIdCol] == trade.OrderId;
 
            // Fuzzy match applies only to pre-migration rows that have no OrderId.
            // A row that already carries an OrderId must be matched by that OrderId
            // falling through to fuzzy would close the wrong row when multiple open
            // positions share the same symbol and direction.
            var rowHasOrderId  = cols.Length > orderIdCol && !string.IsNullOrEmpty(cols[orderIdCol]);
            var matchedByFuzzy = !matchedByOrderId
                && !rowHasOrderId
                && cols.Length > 4
                && cols[4] == trade.Symbol
                && (trade.TradeType == TradeType.Options
                    ? cols[6] == (trade.Direction ?? "")
                    : cols[5] == trade.Quantity.ToString())
                && cols.Length > exitPriceCol
                && string.IsNullOrEmpty(cols[exitPriceCol]);

            if (!matchedByOrderId && !matchedByFuzzy) continue;

            if (matchedByFuzzy)
                _logger.LogDebug(
                    "CSV close: matched {Symbol} by fuzzy key (pre-migration row without OrderId)",
                    trade.Symbol);

            // Recover entry metrics from the open row when not carried in TradeGuard.
            if (trade.LatencyMs is null && cols.Length > 12 &&
                int.TryParse(cols[12], out var ms))
                trade.LatencyMs = ms;

            if (trade.SlippagePct is null && cols.Length > 13 &&
                decimal.TryParse(cols[13].TrimEnd('%').TrimStart('+'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var sp))
                trade.SlippagePct = sp;

            // Preserve the xScore from the open row when LoadFromDatabase reset it to 0.
            if (trade.XScore == 0m && cols.Length > xScoreCol &&
                decimal.TryParse(cols[xScoreCol], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var existingXScore) &&
                existingXScore > 0m)
            {
                trade.XScore = existingXScore;
            }

            lines[i] = BuildClosedRow(trade);
            updated  = true;
            break;
        }

        if (!updated)
        {
            _logger.LogWarning(
                "CSV close: could not find open row for {Symbol} — appending closed row",
                trade.Symbol);
            var allLines = lines.ToList();
            allLines.Add(BuildClosedRow(trade));
            lines = allLines.ToArray();
        }

        await File.WriteAllLinesAsync(path, lines, ct);
    }

    private async Task UpdateSummaryAsync(string path, TradeType tradeType, CancellationToken ct)
    {
        var lines = (await File.ReadAllLinesAsync(path, ct)).ToList();

        var summaryStart = lines.FindIndex(l => l.StartsWith(",,SUMMARY"));
        if (summaryStart >= 0)
            lines = lines.Take(summaryStart).ToList();

        while (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        var header      = tradeType == TradeType.Options ? OptionsHeader : StocksHeader;
        var cols        = header.Split(',');
        var pnlIndex    = Array.IndexOf(cols, "P&L");
        var statusIndex = Array.IndexOf(cols, "Status");

        var trades = lines
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith(",,"))
            .Select(l => l.Split(','))
            .Where(c => c.Length > pnlIndex)
            .ToList();

        var total  = trades.Count;
        var closed = trades.Count(c => c[statusIndex] == "Closed");
        var open   = total - closed;

        var wins = trades.Count(c =>
            c[statusIndex] == "Closed" &&
            decimal.TryParse(c[pnlIndex].TrimStart('+'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var p) &&
            p > 0);

        var losses = trades.Count(c =>
            c[statusIndex] == "Closed" &&
            decimal.TryParse(c[pnlIndex].TrimStart('+'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var p) &&
            p < 0);

        var totalPnl = trades
            .Where(c => c[statusIndex] == "Closed")
            .Sum(c => decimal.TryParse(c[pnlIndex].TrimStart('+'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var p) ? p : 0);

        var winRate = closed > 0 ? (decimal)wins / closed * 100 : 0;
        var pnlSign = totalPnl >= 0 ? "+" : "";

        lines.Add("");
        lines.Add(",,SUMMARY");
        lines.Add($",,Total Trades,{total}");
        lines.Add($",,Open,{open},Closed,{closed}");
        lines.Add($",,Wins,{wins},Losses,{losses},Win Rate,{winRate:F1}%");
        lines.Add($",,Total P&L,{pnlSign}{totalPnl:F2}");

        await File.WriteAllLinesAsync(path, lines, ct);
    }

    private static string FormatExpiration(string? expiration)
    {
        if (expiration is null) return "";
        return DateTimeOffset.TryParse(expiration, out var dt)
            ? dt.ToString("MMM dd yyyy")
            : expiration;
    }

    private SemaphoreSlim GetSemaphore(TradeType t) =>
        t == TradeType.Options ? _optionsLock : _stocksLock;

    private string GetPath(TradeType t) =>
        t == TradeType.Options ? _optionsPath : _stocksPath;
}