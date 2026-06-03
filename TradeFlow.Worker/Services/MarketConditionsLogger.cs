using System.Globalization;
using System.Text.Json;
using TradeFlow.Worker.Configuration;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Fetches daily market conditions from Yahoo Finance and writes a row to market_conditions.csv.
/// Called at 9:00am ET on each market day before the open, providing context for daily performance review.
/// Also calculates the morning chop score and sets the MarketRegimeService for the session —
/// a choppy regime automatically blocks high risk and lotto trades regardless of config flags.
/// All data sourced from Yahoo Finance free API.
/// </summary>
public class MarketConditionsLogger
{
    private readonly string _csvPath;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketConditionsLogger> _logger;
    private readonly RiskEngineOptions _riskOptions;
    private readonly MarketRegimeService _regime;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly string CsvHeader =
        "Date," +
        "SPY Price,SPY Prev Close,SPY Gap %,SPY 50MA,SPY vs 50MA %,SPY 200MA,SPY vs 200MA %," +
        "QQQ Price,QQQ Prev Close,QQQ Gap %,QQQ 50MA,QQQ vs 50MA %,QQQ 200MA,QQQ vs 200MA %," +
        "VIX,VIX Prev,VIX Delta %,SPY ADX,Chop Score,Market Bias";

    public MarketConditionsLogger(
        IConfiguration config,
        ILogger<MarketConditionsLogger> logger,
        IOptions<RiskEngineOptions> riskOptions,
        MarketRegimeService regime)
    {
        _logger      = logger;
        _riskOptions = riskOptions.Value;
        _regime      = regime;
        _httpClient  = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var configuredDir = config["Trades:Directory"];
        var tradesDir = configuredDir is not null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredDir))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "trades"));

        Directory.CreateDirectory(tradesDir);
        _csvPath = Path.Combine(tradesDir, "market_conditions.csv");

        EnsureHeader();
    }

    /// <summary>
    /// Fetches market data from Yahoo Finance, appends a row to market_conditions.csv,
    /// and sets the MarketRegimeService for the trading session.
    /// Called at 9:00am ET by MarketSchedulerService.
    /// </summary>
    public async Task LogMarketConditionsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Market conditions: fetching SPY, QQQ, VIX data from Yahoo Finance.");

        try
        {
            var spy = await FetchYahooDataAsync("SPY", includeAdx: true, ct);
            var qqq = await FetchYahooDataAsync("QQQ", includeAdx: false, ct);
            var vix = await FetchYahooDataAsync("^VIX", includeAdx: false, ct);

            if (spy is null || qqq is null || vix is null)
            {
                _logger.LogWarning("Market conditions: could not fetch all data — skipping log.");
                return;
            }

            var spyGapPct   = spy.PrevClose > 0 ? (spy.Price - spy.PrevClose) / spy.PrevClose * 100 : 0;
            var spy50MaPct  = spy.Ma50  > 0 ? (spy.Price - spy.Ma50)  / spy.Ma50  * 100 : 0;
            var spy200MaPct = spy.Ma200 > 0 ? (spy.Price - spy.Ma200) / spy.Ma200 * 100 : 0;

            var qqqGapPct   = qqq.PrevClose > 0 ? (qqq.Price - qqq.PrevClose) / qqq.PrevClose * 100 : 0;
            var qqq50MaPct  = qqq.Ma50  > 0 ? (qqq.Price - qqq.Ma50)  / qqq.Ma50  * 100 : 0;
            var qqq200MaPct = qqq.Ma200 > 0 ? (qqq.Price - qqq.Ma200) / qqq.Ma200 * 100 : 0;

            var vixDeltaPct = vix.PrevClose > 0
                ? (vix.Price - vix.PrevClose) / vix.PrevClose * 100
                : 0;

            // -- Chop score: 1 point per signal, max 4 --
            var chopScore = 0;

            if (Math.Abs(vixDeltaPct) >= (decimal)_riskOptions.ChopVixSpikePct)
                chopScore++; // VIX spiking vs yesterday

            if (spy.Adx > 0 && spy.Adx < (decimal)_riskOptions.ChopAdxThreshold)
                chopScore++; // No clear trend (low ADX)

            if (spy50MaPct >= (decimal)_riskOptions.ChopSpyExtendedPct)
                chopScore++; // SPY extended above 50MA, pullback risk

            if (vix.Price >= (decimal)_riskOptions.ChopVixLevel)
                chopScore++; // Elevated fear environment

            _regime.SetRegime(chopScore, _riskOptions.ChopMinSignals);

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
                F(vix.Price),     F(vix.PrevClose), Pct(vixDeltaPct),
                F(spy.Adx),
                chopScore,
                bias);

            await File.AppendAllTextAsync(_csvPath, row + Environment.NewLine, ct);

            _logger.LogInformation(
                "Market conditions logged — SPY ${Spy:F2} ({SpyGap:+0.00;-0.00}% gap) vs 50MA {Spy50:+0.00;-0.00}% | " +
                "VIX {Vix:F2} ({VixDelta:+0.00;-0.00}%) | SPY ADX {Adx:F1} | ChopScore: {Chop}/4 | Bias: {Bias}",
                spy.Price, spyGapPct, spy50MaPct,
                vix.Price, vixDeltaPct,
                spy.Adx, chopScore, bias);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market conditions: failed to fetch or log data.");
        }
    }

    // -- Helpers --

    // Creates or updates the CSV header. Rewrites the header row if it does not match
    // the current schema, preserving all existing data rows below it.
    private void EnsureHeader()
    {
        if (!File.Exists(_csvPath))
        {
            File.WriteAllText(_csvPath, CsvHeader + Environment.NewLine);
            return;
        }

        var firstLine = File.ReadLines(_csvPath).FirstOrDefault() ?? "";
        if (firstLine == CsvHeader) return;

        var allLines = File.ReadAllLines(_csvPath);
        allLines[0]  = CsvHeader;
        File.WriteAllLines(_csvPath, allLines);

        _logger.LogInformation(
            "Market conditions: CSV header updated to include chop score and regime columns.");
    }

    // Fetches daily OHLCV data for the last 200 days from Yahoo Finance.
    // Returns current price, previous close, 50MA, 200MA, and optionally ADX(14).
    private async Task<SymbolData?> FetchYahooDataAsync(
        string symbol, bool includeAdx, CancellationToken ct)
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

            var result    = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta      = result.GetProperty("meta");
            var price     = meta.GetProperty("regularMarketPrice").GetDecimal();
            var prevClose = meta.GetProperty("regularMarketPreviousClose").GetDecimal();
            var quote     = result.GetProperty("indicators").GetProperty("quote")[0];

            var closes = ExtractDecimals(quote, "close");
            var ma50   = closes.Count >= 50  ? closes.TakeLast(50).Average()  : 0m;
            var ma200  = closes.Count >= 200 ? closes.TakeLast(200).Average() : closes.Average();

            var adx = 0m;
            if (includeAdx)
            {
                var highs = ExtractDecimals(quote, "high");
                var lows  = ExtractDecimals(quote, "low");
                adx = CalculateAdx(highs, lows, closes);
            }

            return new SymbolData(price, prevClose, ma50, ma200, adx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market conditions: failed to fetch data for {Symbol}", symbol);
            return null;
        }
    }

    // Extracts a numeric array field from a Yahoo Finance quote element, skipping nulls.
    private static List<decimal> ExtractDecimals(JsonElement quote, string field)
    {
        return quote.GetProperty(field)
            .EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetDecimal())
            .ToList();
    }

    // Calculates ADX(14) using Wilder's smoothing from OHLCV data.
    // ADX below 20 = choppy/non-trending. ADX above 25 = trending. Returns 0 if insufficient data.
    private static decimal CalculateAdx(
        IList<decimal> highs,
        IList<decimal> lows,
        IList<decimal> closes,
        int period = 14)
    {
        if (highs.Count < period * 2 + 1) return 0m;

        var trs  = new List<decimal>();
        var pdms = new List<decimal>();
        var ndms = new List<decimal>();

        for (var i = 1; i < highs.Count; i++)
        {
            var tr = Math.Max(
                highs[i] - lows[i],
                Math.Max(
                    Math.Abs(highs[i] - closes[i - 1]),
                    Math.Abs(lows[i]  - closes[i - 1])));

            var upMove   = highs[i] - highs[i - 1];
            var downMove = lows[i - 1] - lows[i];

            pdms.Add(upMove   > downMove && upMove   > 0 ? upMove   : 0m);
            ndms.Add(downMove > upMove   && downMove > 0 ? downMove : 0m);
            trs.Add(tr);
        }

        var atr  = trs.Take(period).Sum();
        var apdm = pdms.Take(period).Sum();
        var andm = ndms.Take(period).Sum();

        var dxValues = new List<decimal>();

        for (var i = period; i < trs.Count; i++)
        {
            atr  = atr  - atr  / period + trs[i];
            apdm = apdm - apdm / period + pdms[i];
            andm = andm - andm / period + ndms[i];

            if (atr == 0) continue;

            var pdi = 100m * apdm / atr;
            var ndi = 100m * andm / atr;
            var sum = pdi + ndi;

            if (sum == 0) continue;

            dxValues.Add(100m * Math.Abs(pdi - ndi) / sum);
        }

        if (dxValues.Count < period) return 0m;

        var adx = dxValues.Take(period).Average();
        for (var i = period; i < dxValues.Count; i++)
            adx = (adx * (period - 1) + dxValues[i]) / period;

        return Math.Round(adx, 2);
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

    private record SymbolData(decimal Price, decimal PrevClose, decimal Ma50, decimal Ma200, decimal Adx = 0m);
}