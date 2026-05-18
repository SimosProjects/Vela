using System.Globalization;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

// Writes and maintains two CSV trade logs, one for options, one for stocks.
// Each file has a header row, one row per trade, and a summary block at the bottom.
// Thread-safe via SemaphoreSlim, both AlertPollingService and SignalRListenerService
// may call OpenTrade/CloseTrade concurrently.
public class CsvTradeLogger
{
    private readonly string _optionsPath;
    private readonly string _stocksPath;
    private readonly ILogger<CsvTradeLogger> _logger;

    // Separate locks per file, options and stocks writes never block each other
    private readonly SemaphoreSlim _optionsLock = new(1, 1);
    private readonly SemaphoreSlim _stocksLock  = new(1, 1);

    private static readonly string OptionsHeader =
        "Date Opened,Time Opened,Date Closed,Time Closed," +
        "Symbol,Contract,Direction,Strike,Expiration," +
        "Contracts,Entry Price,Entry Amount," +
        "Exit Price,Exit Amount,Status,Result,P&L,P&L %";

    private static readonly string StocksHeader =
        "Date Opened,Time Opened,Date Closed,Time Closed," +
        "Symbol,Shares,Entry Price,Entry Amount," +
        "Exit Price,Exit Amount,Status,Result,P&L,P&L %";

    public CsvTradeLogger(
        IConfiguration config,
        ILogger<CsvTradeLogger> logger)
    {
        _logger = logger;

        // Anchor to binary directory to ensure consistent path resolution
        // regardless of the working directory when the process starts.
        var configuredDir = config["Trades:Directory"];
        var tradesDir = configuredDir is not null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredDir))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "trades"));

        Directory.CreateDirectory(tradesDir);

        _optionsPath = Path.Combine(tradesDir, "options_trades.csv");
        _stocksPath  = Path.Combine(tradesDir, "stocks_trades.csv");

        EnsureHeadersExist();
    }

    /// <summary>
    /// Appends a new open trade row to the appropriate CSV file and updates the summary.
    /// </summary>
    /// <param name="trade">The trade record to log.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OpenTradeAsync(TradeRecord trade, CancellationToken ct = default)
    {
        var semaphore = GetSemaphore(trade.TradeType);
        await semaphore.WaitAsync(ct);
        try
        {
            var path = GetPath(trade.TradeType);

            // Strip summary block before appending so the new row lands before it
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
    /// Finds the matching open row by symbol and rewrites it with exit data, then updates the summary.
    /// </summary>
    /// <param name="trade">The closed trade record with exit data populated.</param>
    /// <param name="ct">Cancellation token.</param>
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

    // -- Helpers --
    private void EnsureHeadersExist()
    {
        if (!File.Exists(_optionsPath))
            File.WriteAllText(_optionsPath, OptionsHeader + Environment.NewLine);

        if (!File.Exists(_stocksPath))
            File.WriteAllText(_stocksPath, StocksHeader + Environment.NewLine);
    }

    private string BuildOpenRow(TradeRecord t)
    {
        var et = t.OpenedAt.ToLocalTime();

        if (t.TradeType == TradeType.Options)
        {
            return string.Join(",",
                et.ToString("yyyy-MM-dd"),
                et.ToString("HH:mm:ss"),
                "",           // Date Closed
                "",           // Time Closed
                t.Symbol,
                t.OptionsContract ?? "",
                t.Direction ?? "",
                t.Strike?.ToString("F0") ?? "",
                FormatExpiration(t.Expiration),
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                "",           // Exit Price
                "",           // Exit Amount
                t.Status,
                t.Result,
                "",           // P&L
                "");          // P&L %
        }
        else
        {
            return string.Join(",",
                et.ToString("yyyy-MM-dd"),
                et.ToString("HH:mm:ss"),
                "",
                "",
                t.Symbol,
                t.Quantity,
                t.EntryPrice.ToString("F2"),
                t.EntryAmount.ToString("F2"),
                "",
                "",
                t.Status,
                t.Result,
                "",
                "");
        }
    }

    private string BuildClosedRow(TradeRecord t)
    {
        var openEt  = t.OpenedAt.ToLocalTime();
        var closeEt = t.ClosedAt?.ToLocalTime();
        var pnlSign = t.PnL >= 0 ? "+" : "";

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
                t.ExitPrice?.ToString("F2")  ?? "",
                t.ExitAmount?.ToString("F2") ?? "",
                t.Status,
                t.Result,
                $"{pnlSign}{t.PnL:F2}",
                $"{pnlSign}{t.PnLPercent:F2}%");
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
                t.ExitPrice?.ToString("F2")  ?? "",
                t.ExitAmount?.ToString("F2") ?? "",
                t.Status,
                t.Result,
                $"{pnlSign}{t.PnL:F2}",
                $"{pnlSign}{t.PnLPercent:F2}%");
        }
    }

    private async Task AppendRowAsync(string path, string row, CancellationToken ct)
    {
        await File.AppendAllTextAsync(path, row + Environment.NewLine, ct);
    }

    // Removes the summary block from the end of the file before writing new rows
    private static async Task StripSummaryAsync(string path, CancellationToken ct)
    {
        var lines = (await File.ReadAllLinesAsync(path, ct)).ToList();
        var summaryStart = lines.FindIndex(l => l.StartsWith(",,SUMMARY"));
        if (summaryStart >= 0)
            lines = lines.Take(summaryStart).ToList();
        await File.WriteAllLinesAsync(path, lines, ct);
    }

    // Reads the file, finds the row matching trade.OrderId, replaces it, rewrites the file
    private async Task RewriteTradeRowAsync(string path, TradeRecord trade, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        var updated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            // Skip header, summary, and empty lines
            if (i == 0 || lines[i].StartsWith(",,") || string.IsNullOrWhiteSpace(lines[i]))
                continue;

            // Match on OrderId embedded in the row and we find it by searching for the order ID
            // Since OrderId isn't a CSV column we match by symbol + entry price + open date
            // as a composite key (OrderId is stored in memory in TradeGuard, not in CSV)
            var cols = lines[i].Split(',');
            if (cols.Length > 4 &&
                cols[4] == trade.Symbol &&
                cols[6] == (trade.TradeType == TradeType.Options ? trade.Direction ?? "" : trade.Quantity.ToString()) &&
                string.IsNullOrEmpty(cols[12])) // Exit Price column is empty = still open
            {
                lines[i] = BuildClosedRow(trade);
                updated = true;
                break;
            }
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

    // Rewrites the summary block at the end of the file
    private async Task UpdateSummaryAsync(string path, TradeType tradeType, CancellationToken ct)
    {
        var lines = (await File.ReadAllLinesAsync(path, ct)).ToList();

        // Remove existing summary block
        var summaryStart = lines.FindIndex(l => l.StartsWith(",,SUMMARY"));
        if (summaryStart >= 0)
            lines = lines.Take(summaryStart).ToList();

        // Parse trade rows and skip header and empty lines
        var header      = tradeType == TradeType.Options ? OptionsHeader : StocksHeader;
        var cols        = header.Split(',');
        var pnlIndex    = Array.IndexOf(cols, "P&L");
        var statusIndex = Array.IndexOf(cols, "Status");
        var resultIndex = Array.IndexOf(cols, "Result");

        var trades = lines
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith(",,"))
            .Select(l => l.Split(','))
            .Where(c => c.Length > pnlIndex)
            .ToList();

        var total  = trades.Count;
        var closed = trades.Count(c => c[statusIndex] == "Closed");
        var open   = total - closed;

        var wins   = trades.Count(c =>
            c[statusIndex] == "Closed" &&
            decimal.TryParse(c[pnlIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var p) &&
            p > 0);

        var losses = trades.Count(c =>
            c[statusIndex] == "Closed" &&
            decimal.TryParse(c[pnlIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var p) &&
            p < 0);

        var totalPnl = trades
            .Where(c => c[statusIndex] == "Closed")
            .Sum(c => decimal.TryParse(
                c[pnlIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0);

        var winRate = closed > 0
            ? (decimal)wins / closed * 100
            : 0;

        var pnlSign = totalPnl >= 0 ? "+" : "";

        // Append summary block
        lines.Add("");
        lines.Add($",,SUMMARY");
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