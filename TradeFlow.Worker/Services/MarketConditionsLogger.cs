using System.Globalization;
using System.Text.Json;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Fetches daily market conditions from Yahoo Finance and writes a row to market_conditions.csv.
/// Called at 9:00am ET on each market day before the open, providing context for daily performance review.
/// Data includes SPY and QQQ price vs moving averages and VIX level.
/// All data sourced from Yahoo Finance free API.
/// </summary>
public class MarketConditionsLogger
{
    private readonly string _csvPath;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketConditionsLogger> _logger;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly string CsvHeader =
        "Date," +
        "SPY Price,SPY Prev Close,SPY Gap %,SPY 50MA,SPY vs 50MA %,SPY 200MA,SPY vs 200MA %," +
        "QQQ Price,QQQ Prev Close,QQQ Gap %,QQQ 50MA,QQQ vs 50MA %,QQQ 200MA,QQQ vs 200MA %," +
        "VIX,Market Bias";

    public MarketConditionsLogger(
        IConfiguration config,
        ILogger<MarketConditionsLogger> logger)
    {
        _logger     = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var configuredDir = config["Trades:Directory"];
        var tradesDir = configuredDir is not null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredDir))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "trades"));

        Directory.CreateDirectory(tradesDir);
        _csvPath = Path.Combine(tradesDir, "market_conditions.csv");

        if (!File.Exists(_csvPath))
            File.WriteAllText(_csvPath, CsvHeader + Environment.NewLine);
    }

    /// <summary>
    /// Fetches market data from Yahoo Finance and appends a row to market_conditions.csv.
    /// Called at 9:00am ET by MarketSchedulerService.
    /// </summary>
    public async Task LogMarketConditionsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Market conditions: fetching SPY, QQQ, VIX data from Yahoo Finance.");

        try
        {
            var spy = await FetchYahooDataAsync("SPY", ct);
            var qqq = await FetchYahooDataAsync("QQQ", ct);
            var vix = await FetchYahooDataAsync("^VIX", ct);

            if (spy is null || qqq is null || vix is null)
            {
                _logger.LogWarning("Market conditions: could not fetch all data — skipping log.");
                return;
            }

            var spyGapPct    = spy.PrevClose > 0 ? (spy.Price - spy.PrevClose) / spy.PrevClose * 100 : 0;
            var spy50MaPct   = spy.Ma50  > 0 ? (spy.Price - spy.Ma50)  / spy.Ma50  * 100 : 0;
            var spy200MaPct  = spy.Ma200 > 0 ? (spy.Price - spy.Ma200) / spy.Ma200 * 100 : 0;

            var qqqGapPct    = qqq.PrevClose > 0 ? (qqq.Price - qqq.PrevClose) / qqq.PrevClose * 100 : 0;
            var qqq50MaPct   = qqq.Ma50  > 0 ? (qqq.Price - qqq.Ma50)  / qqq.Ma50  * 100 : 0;
            var qqq200MaPct  = qqq.Ma200 > 0 ? (qqq.Price - qqq.Ma200) / qqq.Ma200 * 100 : 0;

            var bias = DetermineMarketBias(spy.Price, spy.Ma50, spy.Ma200, vix.Price);

            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

            var row = string.Join(",",
                today.ToString("yyyy-MM-dd"),
                F(spy.Price),     F(spy.PrevClose), Pct(spyGapPct),
                F(spy.Ma50),      Pct(spy50MaPct),
                F(spy.Ma200),     Pct(spy200MaPct),
                F(qqq.Price),     F(qqq.PrevClose), Pct(qqqGapPct),
                F(qqq.Ma50),      Pct(qqq50MaPct),
                F(qqq.Ma200),     Pct(qqq200MaPct),
                F(vix.Price),
                bias);

            await File.AppendAllTextAsync(_csvPath, row + Environment.NewLine, ct);

            _logger.LogInformation(
                "Market conditions logged — SPY ${Spy:F2} ({SpyGap:+0.00;-0.00}% gap) vs 50MA {Spy50:+0.00;-0.00}% | " +
                "QQQ ${Qqq:F2} ({QqqGap:+0.00;-0.00}% gap) | VIX {Vix:F2} | Bias: {Bias}",
                spy.Price, spyGapPct, spy50MaPct,
                qqq.Price, qqqGapPct,
                vix.Price, bias);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market conditions: failed to fetch or log data.");
        }
    }

    // -- Helpers --

    // Fetches daily OHLCV data for the last 200 days from Yahoo Finance.
    // Returns current price, previous close, 50MA and 200MA.
    private async Task<SymbolData?> FetchYahooDataAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
                      "?interval=1d&range=200d";

            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Market conditions: Yahoo Finance returned {Status} for {Symbol}",
                    (int)response.StatusCode, symbol);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var result = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result")[0];

            var meta      = result.GetProperty("meta");
            var price     = meta.GetProperty("regularMarketPrice").GetDecimal();
            var prevClose = meta.GetProperty("chartPreviousClose").GetDecimal();

            var closes = result
                .GetProperty("indicators")
                .GetProperty("quote")[0]
                .GetProperty("close")
                .EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetDecimal())
                .ToList();

            var ma50  = closes.Count >= 50  ? closes.TakeLast(50).Average()  : 0m;
            var ma200 = closes.Count >= 200 ? closes.TakeLast(200).Average() : closes.Average();

            return new SymbolData(price, prevClose, ma50, ma200);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market conditions: failed to fetch data for {Symbol}", symbol);
            return null;
        }
    }

    // Determines overall market bias from SPY position relative to moving averages and VIX level.
    private static string DetermineMarketBias(decimal spyPrice, decimal ma50, decimal ma200, decimal vix)
    {
        var aboveBoth = spyPrice > ma50 && spyPrice > ma200;
        var belowBoth = spyPrice < ma50 && spyPrice < ma200;

        if (aboveBoth && vix < 20) return "Bullish";
        if (belowBoth && vix > 25) return "Bearish";
        if (aboveBoth)             return "Cautiously Bullish";
        if (belowBoth)             return "Cautiously Bearish";
        return "Neutral";
    }

    private static string F(decimal value)   => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Pct(decimal value) => $"{(value >= 0 ? "+" : "")}{value:F2}%";

    private record SymbolData(decimal Price, decimal PrevClose, decimal Ma50, decimal Ma200);
}