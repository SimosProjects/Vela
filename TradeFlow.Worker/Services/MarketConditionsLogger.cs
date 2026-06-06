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
        "VIX,VIX Prev,VIX Delta %,SPY ADX,SPY PDI,SPY NDI,Chop Score,Market Bias";

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

            // Strong downtrend: ADX is trending but -DI leads +DI, indicating bearish direction.
            // Differs from the no-trend signal — a strong bear trend is just as bad for calls as chop.
            var spyBearishTrend = spy.Adx >= (decimal)_riskOptions.ChopAdxThreshold &&
                                  spy.NDi  > spy.PDi + (decimal)_riskOptions.ChopBearishDiDiff;

            // SPY below its 50MA signals bearish market structure at open.
            var spyBelowMa = spy50MaPct < -(decimal)_riskOptions.ChopSpyBelowMaPct;

            // -- Chop score: 1 point per signal, max 6 --
            var chopScore = 0;

            if (Math.Abs(vixDeltaPct) >= (decimal)_riskOptions.ChopVixSpikePct)
                chopScore++; // VIX spiking vs yesterday

            if (spy.Adx > 0 && spy.Adx < (decimal)_riskOptions.ChopAdxThreshold)
                chopScore++; // No clear trend (low ADX)

            if (spy50MaPct >= (decimal)_riskOptions.ChopSpyExtendedPct)
                chopScore++; // SPY extended above 50MA, pullback risk

            if (vix.Price >= (decimal)_riskOptions.ChopVixLevel)
                chopScore++; // Elevated fear environment

            if (spyBearishTrend)
                chopScore++; // Strong downtrend detected via -DI > +DI

            if (spyBelowMa)
                chopScore++; // Bearish market structure — SPY below 50MA

            _regime.SetRegime(chopScore, _riskOptions.ChopMinSignals);

            var bias = DetermineMarketBias(spy.Price, spy.Ma50, spy.Ma200, vix.Price, spy.PDi, spy.NDi);

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
                F(spy.Adx),       F(spy.PDi),       F(spy.NDi),
                chopScore,
                bias);

            await File.AppendAllTextAsync(_csvPath, row + Environment.NewLine, ct);

            _logger.LogInformation(
                "Market conditions logged — SPY ${Spy:F2} ({SpyGap:+0.00;-0.00}% gap) vs 50MA {Spy50:+0.00;-0.00}% | " +
                "VIX {Vix:F2} ({VixDelta:+0.00;-0.00}%) | SPY ADX {Adx:F1} (+DI {PDi:F1} / -DI {NDi:F1}) | " +
                "ChopScore: {Chop}/6 | Bias: {Bias}",
                spy.Price, spyGapPct, spy50MaPct,
                vix.Price, vixDeltaPct,
                spy.Adx, spy.PDi, spy.NDi,
                chopScore, bias);
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
            "Market conditions: CSV header updated to include PDI/NDI and directional regime columns.");
    }

    // Fetches daily OHLCV data for the last 200 days from Yahoo Finance.
    // Returns current price, previous close, 50MA, 200MA, and optionally ADX(14) with PDI/NDI.
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

            if (!doc.RootElement.TryGetProperty("chart", out var chartEl))
            {
                _logger.LogWarning("Market conditions: no 'chart' key in response for {Symbol}", symbol);
                return null;
            }

            if (!chartEl.TryGetProperty("result", out var resultsEl) ||
                resultsEl.ValueKind == JsonValueKind.Null ||
                resultsEl.GetArrayLength() == 0)
            {
                _logger.LogWarning("Market conditions: empty result array for {Symbol}", symbol);
                return null;
            }

            var result = resultsEl[0];

            if (!result.TryGetProperty("meta", out var meta))
            {
                _logger.LogWarning("Market conditions: no 'meta' in result for {Symbol}", symbol);
                return null;
            }

            var price = meta.TryGetProperty("regularMarketPrice", out var priceEl)
                ? priceEl.GetDecimal()
                : meta.TryGetProperty("chartPreviousClose", out var fallbackEl)
                    ? fallbackEl.GetDecimal()
                    : 0m;

            var prevClose = meta.TryGetProperty("regularMarketPreviousClose", out var prevEl)
                ? prevEl.GetDecimal()
                : 0m;

            if (price == 0m)
            {
                _logger.LogWarning("Market conditions: could not determine price for {Symbol}", symbol);
                return null;
            }

            if (!result.TryGetProperty("indicators", out var indicators) ||
                !indicators.TryGetProperty("quote", out var quoteArr) ||
                quoteArr.GetArrayLength() == 0)
            {
                _logger.LogWarning("Market conditions: no quote data for {Symbol}", symbol);
                return new SymbolData(price, prevClose, 0m, 0m);
            }

            var quote  = quoteArr[0];
            var closes = ExtractDecimals(quote, "close");

            if (closes.Count > 0)
                prevClose = closes[^1];

            var ma50  = closes.Count >= 50  ? closes.TakeLast(50).Average()  : 0m;
            var ma200 = closes.Count >= 200 ? closes.TakeLast(200).Average() : closes.Count > 0 ? closes.Average() : 0m;

            var adx = 0m;
            var pdi = 0m;
            var ndi = 0m;

            if (includeAdx)
            {
                var highs = ExtractDecimals(quote, "high");
                var lows  = ExtractDecimals(quote, "low");
                (adx, pdi, ndi) = CalculateAdx(highs, lows, closes);
            }

            return new SymbolData(price, prevClose, ma50, ma200, adx, pdi, ndi);
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
    // Returns ADX strength plus the final +DI and -DI values for trend direction.
    // +DI > -DI = bullish trend. -DI > +DI = bearish trend.
    // ADX below 20 = choppy/non-trending. ADX above 25 = trending.
    private static (decimal Adx, decimal PDi, decimal NDi) CalculateAdx(
        IList<decimal> highs,
        IList<decimal> lows,
        IList<decimal> closes,
        int period = 14)
    {
        if (highs.Count < period * 2 + 1) return (0m, 0m, 0m);

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

        var dxValues  = new List<decimal>();
        var finalPdi  = 0m;
        var finalNdi  = 0m;

        for (var i = period; i < trs.Count; i++)
        {
            atr  = atr  - atr  / period + trs[i];
            apdm = apdm - apdm / period + pdms[i];
            andm = andm - andm / period + ndms[i];

            if (atr == 0) continue;

            var pdi = 100m * apdm / atr;
            var ndi = 100m * andm / atr;
            var sum = pdi + ndi;

            finalPdi = pdi;
            finalNdi = ndi;

            if (sum == 0) continue;

            dxValues.Add(100m * Math.Abs(pdi - ndi) / sum);
        }

        if (dxValues.Count < period) return (0m, 0m, 0m);

        var adx = dxValues.Take(period).Average();
        for (var i = period; i < dxValues.Count; i++)
            adx = (adx * (period - 1) + dxValues[i]) / period;

        return (Math.Round(adx, 2), Math.Round(finalPdi, 2), Math.Round(finalNdi, 2));
    }

    // Determines overall market bias from SPY position relative to moving averages,
    // VIX level, and ADX trend direction (+DI vs -DI).
    private static string DetermineMarketBias(
        decimal spyPrice, decimal ma50, decimal ma200, decimal vix,
        decimal pdi, decimal ndi)
    {
        var aboveBoth    = spyPrice > ma50 && spyPrice > ma200;
        var belowBoth    = spyPrice < ma50 && spyPrice < ma200;
        var bullishTrend = pdi > ndi;
        var bearishTrend = ndi > pdi;

        if (aboveBoth && bullishTrend && vix < 20) return "Bullish";
        if (belowBoth && bearishTrend && vix > 25) return "Bearish";
        if (aboveBoth && bullishTrend)             return "Cautiously Bullish";
        if (aboveBoth && bearishTrend)             return "Caution — Trend Reversal";
        if (belowBoth)                             return "Cautiously Bearish";
        return "Neutral";
    }

    private static string F(decimal value)   => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Pct(decimal value) => $"{(value >= 0 ? "+" : "")}{value:F2}%";

    private record SymbolData(
        decimal Price,
        decimal PrevClose,
        decimal Ma50,
        decimal Ma200,
        decimal Adx  = 0m,
        decimal PDi  = 0m,
        decimal NDi  = 0m);
}