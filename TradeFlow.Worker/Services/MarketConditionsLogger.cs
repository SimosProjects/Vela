using System.Globalization;
using System.Text.Json;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Fetches daily market conditions and writes a row to market_conditions.csv.
/// SPY and VIX data is sourced from IB Gateway via reqHistoricalData for reliability.
/// QQQ is fetched from Yahoo Finance (CSV logging only, not used for regime calculation).
/// Falls back to Yahoo Finance for SPY/VIX if Gateway returns no data.
/// Called at 9:20am ET on each market day by MarketSchedulerService.
/// Sets the MarketRegimeService tier (Bullish/Choppy/Bearish) and sizing multiplier
/// for the session based on SPY MA cascade, VIX level, and ADX trend direction.
/// </summary>
public class MarketConditionsLogger
{
    private readonly string _csvPath;
    private readonly HttpClient _httpClient;
    private readonly IBrokerService _broker;
    private readonly ILogger<MarketConditionsLogger> _logger;
    private readonly RiskEngineOptions _riskOptions;
    private readonly MarketRegimeService _regime;
    private readonly SystemStateService _systemState;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly string CsvHeader =
        "Date," +
        "SPY Price,SPY Prev Close,SPY Gap %," +
        "SPY 20MA,SPY vs 20MA %,SPY 50MA,SPY vs 50MA %,SPY 200MA,SPY vs 200MA %," +
        "QQQ Price,QQQ Prev Close,QQQ Gap %,QQQ 50MA,QQQ vs 50MA %,QQQ 200MA,QQQ vs 200MA %," +
        "VIX,VIX Prev,VIX Delta %,SPY ADX,SPY PDI,SPY NDI," +
        "Regime Tier,Sizing Multiplier,Block Calls,Chop Score,Market Bias";

    public MarketConditionsLogger(
        IConfiguration config,
        IBrokerService broker,
        ILogger<MarketConditionsLogger> logger,
        IOptions<RiskEngineOptions> riskOptions,
        MarketRegimeService regime,
        SystemStateService systemState)
    {
        _broker      = broker;
        _logger      = logger;
        _riskOptions = riskOptions.Value;
        _regime      = regime;
        _systemState = systemState;
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
    /// Fetches market data, appends a row to market_conditions.csv, and sets the
    /// MarketRegimeService tier and sizing multiplier for the trading session.
    /// SPY and VIX are fetched from Gateway first, Yahoo Finance as fallback.
    /// Called at 9:20am ET by MarketSchedulerService.
    /// </summary>
    public async Task LogMarketConditionsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Market conditions: fetching SPY and VIX data from Gateway.");

        try
        {
            var spyBars = await _broker.GetHistoricalBarsAsync("SPY", 200, ct);
            var vixBars = await _broker.GetHistoricalBarsAsync("VIX", 5, ct);

            SymbolData? spy = null;
            SymbolData? vix = null;

            if (spyBars.Count >= 50)
            {
                spy = BuildSymbolDataFromBars(spyBars, includeAdx: true);
                _logger.LogInformation(
                    "Market conditions: SPY data from Gateway — {Count} bars, price ${Price:F2}",
                    spyBars.Count, spy.Price);
            }
            else
            {
                _logger.LogWarning(
                    "Market conditions: Gateway returned {Count} SPY bars — falling back to Yahoo.",
                    spyBars.Count);
                spy = await FetchYahooDataAsync("SPY", includeAdx: true, ct);
            }

            if (vixBars.Count >= 2)
            {
                vix = BuildSymbolDataFromBars(vixBars, includeAdx: false);
                _logger.LogInformation(
                    "Market conditions: VIX data from Gateway — price ${Price:F2}", vix.Price);
            }
            else
            {
                _logger.LogWarning(
                    "Market conditions: Gateway returned {Count} VIX bars — falling back to Yahoo.",
                    vixBars.Count);
                vix = await FetchYahooDataAsync("^VIX", includeAdx: false, ct);
            }

            // QQQ is CSV logging only, not part of regime calculation
            var qqq = await FetchYahooDataAsync("QQQ", includeAdx: false, ct);

            if (spy is null || vix is null)
            {
                _logger.LogWarning("Market conditions: could not fetch SPY or VIX — skipping.");
                return;
            }

            var spyGapPct   = spy.PrevClose > 0 ? (spy.Price - spy.PrevClose) / spy.PrevClose * 100 : 0;
            var spy20MaPct  = spy.Ma20  > 0 ? (spy.Price - spy.Ma20)  / spy.Ma20  * 100 : 0;
            var spy50MaPct  = spy.Ma50  > 0 ? (spy.Price - spy.Ma50)  / spy.Ma50  * 100 : 0;
            var spy200MaPct = spy.Ma200 > 0 ? (spy.Price - spy.Ma200) / spy.Ma200 * 100 : 0;

            var qqqGapPct   = qqq is not null && qqq.PrevClose > 0
                ? (qqq.Price - qqq.PrevClose) / qqq.PrevClose * 100 : 0;
            var qqq50MaPct  = qqq is not null && qqq.Ma50  > 0
                ? (qqq.Price - qqq.Ma50)  / qqq.Ma50  * 100 : 0;
            var qqq200MaPct = qqq is not null && qqq.Ma200 > 0
                ? (qqq.Price - qqq.Ma200) / qqq.Ma200 * 100 : 0;

            var vixDeltaPct = vix.PrevClose > 0
                ? (vix.Price - vix.PrevClose) / vix.PrevClose * 100 : 0;

            // -- Chop score signals (0-6) --
            var spyBearishTrend = spy.Adx >= (decimal)_riskOptions.ChopAdxThreshold &&
                                  spy.NDi  > spy.PDi + (decimal)_riskOptions.ChopBearishDiDiff;

            var spyBelowMa50 = spy50MaPct < -(decimal)_riskOptions.ChopSpyBelowMaPct;

            var chopScore = 0;
            if (Math.Abs(vixDeltaPct) >= (decimal)_riskOptions.ChopVixSpikePct) chopScore++;
            if (spy.Adx > 0 && spy.Adx < (decimal)_riskOptions.ChopAdxThreshold) chopScore++;
            if (spy50MaPct >= (decimal)_riskOptions.ChopSpyExtendedPct) chopScore++;
            if (vix.Price >= (decimal)_riskOptions.ChopVixLevel) chopScore++;
            if (spyBearishTrend) chopScore++;
            if (spyBelowMa50) chopScore++;

            // -- Regime tier cascade --
            // 200MA = master switch. 50MA = weekly ceiling. 20MA = daily trigger.
            var (tier, sizingMultiplier, blockCalls) = DetermineRegimeTier(
                spy.Price, spy.Ma20, spy.Ma50, spy.Ma200, vix.Price);

            _regime.SetRegime(
                chopScore, _riskOptions.ChopMinSignals,
                tier, sizingMultiplier, blockCalls,
                spy.Ma20, spy.Ma50, spy.Ma200);

            _systemState.UpdateRegime(
                tier.ToString(), sizingMultiplier, blockCalls,
                spy.Price, spy.Ma20, spy.Ma50, spy.Ma200,
                vix.Price, (decimal)vixDeltaPct, chopScore);

            var bias = DetermineMarketBias(
                spy.Price, spy.Ma20, spy.Ma50, spy.Ma200,
                vix.Price, spy.PDi, spy.NDi);

            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

            var row = string.Join(",",
                today.ToString("yyyy-MM-dd"),
                F(spy.Price),     F(spy.PrevClose), Pct(spyGapPct),
                F(spy.Ma20),      Pct(spy20MaPct),
                F(spy.Ma50),      Pct(spy50MaPct),
                F(spy.Ma200),     Pct(spy200MaPct),
                qqq is not null ? F(qqq.Price)     : "",
                qqq is not null ? F(qqq.PrevClose) : "",
                qqq is not null ? Pct(qqqGapPct)   : "",
                qqq is not null ? F(qqq.Ma50)      : "",
                qqq is not null ? Pct(qqq50MaPct)  : "",
                qqq is not null ? F(qqq.Ma200)     : "",
                qqq is not null ? Pct(qqq200MaPct) : "",
                F(vix.Price),     F(vix.PrevClose), Pct(vixDeltaPct),
                F(spy.Adx),       F(spy.PDi),       F(spy.NDi),
                tier.ToString(),  Pct((decimal)((sizingMultiplier - 1) * 100 + 100) - 100),
                blockCalls.ToString(),
                chopScore,
                bias);

            await File.AppendAllTextAsync(_csvPath, row + Environment.NewLine, ct);

            _logger.LogInformation(
                "Market conditions logged — SPY ${Spy:F2} | 20MA {Spy20:+0.00;-0.00}% | " +
                "50MA {Spy50:+0.00;-0.00}% | 200MA {Spy200:+0.00;-0.00}% | " +
                "VIX {Vix:F2} ({VixDelta:+0.00;-0.00}%) | ADX {Adx:F1} (+DI {PDi:F1} -DI {NDi:F1}) | " +
                "Regime: {Tier} ({Multiplier:P0}) | BlockCalls: {Block} | ChopScore: {Chop}/6 | Bias: {Bias}",
                spy.Price, spy20MaPct, spy50MaPct, spy200MaPct,
                vix.Price, vixDeltaPct,
                spy.Adx, spy.PDi, spy.NDi,
                tier, sizingMultiplier, blockCalls, chopScore, bias);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market conditions: failed to fetch or log data.");
        }
    }

    // -- Helpers --

    // Determines the three-tier regime using the MA cascade:
    //   200MA = master switch (below = cap at Choppy minimum)
    //   50MA  = weekly ceiling (below = no Bullish sizing even on up days)
    //   20MA  = daily trigger (primary sizing and call gate)
    //   VIX   = confirmation layer
    private (RegimeTier Tier, decimal SizingMultiplier, bool BlockCalls) DetermineRegimeTier(
        decimal spyPrice, decimal ma20, decimal ma50, decimal ma200, decimal vix)
    {
        var bullishMultiplier = (decimal)_riskOptions.RegimeBullishSizingPct;
        var choppyMultiplier  = (decimal)_riskOptions.RegimeChoppySizingPct;
        var bearishMultiplier = (decimal)_riskOptions.RegimeBearishSizingPct;
        var blockCallsInBearish = _riskOptions.RegimeBearishBlockCalls;
        var vixBearish  = (decimal)_riskOptions.RegimeVixBearishThreshold;
        var vixChoppy   = (decimal)_riskOptions.RegimeVixChoppyThreshold;
        var below50Pct  = (decimal)_riskOptions.RegimeSpyBelow50MaPct;

        // Master switch: SPY below 200MA = never full size regardless of daily signals
        var belowMa200 = ma200 > 0 && spyPrice < ma200;

        // Weekly ceiling: SPY below 50MA = maximum Choppy sizing
        var belowMa50 = ma50 > 0 && spyPrice < ma50;

        // Below configurable % threshold of 50MA triggers Bearish tier
        var wellBelowMa50 = below50Pct > 0 && ma50 > 0 &&
                            (ma50 - spyPrice) / ma50 * 100 >= below50Pct;

        // Daily trigger: SPY below 20MA
        var belowMa20 = ma20 > 0 && spyPrice < ma20;

        if (belowMa200 || wellBelowMa50 || (belowMa50 && belowMa20 && vix >= vixBearish))
            return (RegimeTier.Bearish, bearishMultiplier, blockCallsInBearish);

        if (belowMa20 && vix >= vixBearish)
            return (RegimeTier.Bearish, bearishMultiplier, blockCallsInBearish);

        if (belowMa50 || (belowMa20 && vix >= vixChoppy) || vix >= vixChoppy)
            return (RegimeTier.Choppy, choppyMultiplier, false);

        if (belowMa200)
            return (RegimeTier.Choppy, choppyMultiplier, false);

        return (RegimeTier.Bullish, bullishMultiplier, false);
    }

    // Builds a SymbolData from a list of historical bars.
    // Computes 20MA, 50MA, 200MA from closes, and optionally ADX(14).
    // Price = last bar close, PrevClose = second to last bar close.
    private static SymbolData BuildSymbolDataFromBars(
        IList<HistoricalBar> bars, bool includeAdx)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var price     = closes[^1];
        var prevClose = closes.Count >= 2 ? closes[^2] : price;

        var ma20  = closes.Count >= 20  ? closes.TakeLast(20).Average()  : 0m;
        var ma50  = closes.Count >= 50  ? closes.TakeLast(50).Average()  : 0m;
        var ma200 = closes.Count >= 200 ? closes.TakeLast(200).Average() : closes.Average();

        var adx = 0m;
        var pdi = 0m;
        var ndi = 0m;

        if (includeAdx)
        {
            var highs = bars.Select(b => b.High).ToList();
            var lows  = bars.Select(b => b.Low).ToList();
            (adx, pdi, ndi) = CalculateAdx(highs, lows, closes);
        }

        return new SymbolData(price, prevClose, ma20, ma50, ma200, adx, pdi, ndi);
    }

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

        _logger.LogInformation("Market conditions: CSV header updated.");
    }

    // Fetches daily OHLCV data from Yahoo Finance.
    // Used as fallback when Gateway returns insufficient bars, and for QQQ (CSV logging only).
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

            if (!doc.RootElement.TryGetProperty("chart", out var chartEl) ||
                !chartEl.TryGetProperty("result", out var resultsEl) ||
                resultsEl.ValueKind == JsonValueKind.Null ||
                resultsEl.GetArrayLength() == 0)
            {
                _logger.LogWarning("Market conditions: unexpected Yahoo response for {Symbol}", symbol);
                return null;
            }

            var result = resultsEl[0];
            if (!result.TryGetProperty("meta", out var meta)) return null;

            var price = meta.TryGetProperty("regularMarketPrice", out var priceEl)
                ? priceEl.GetDecimal()
                : meta.TryGetProperty("chartPreviousClose", out var fallbackEl)
                    ? fallbackEl.GetDecimal() : 0m;

            var prevClose = meta.TryGetProperty("regularMarketPreviousClose", out var prevEl)
                ? prevEl.GetDecimal() : 0m;

            if (price == 0m) return null;

            if (!result.TryGetProperty("indicators", out var indicators) ||
                !indicators.TryGetProperty("quote", out var quoteArr) ||
                quoteArr.GetArrayLength() == 0)
                return new SymbolData(price, prevClose, 0m, 0m, 0m);

            var quote  = quoteArr[0];
            var closes = ExtractDecimals(quote, "close");

            if (closes.Count > 0) prevClose = closes[^1];

            var ma20  = closes.Count >= 20  ? closes.TakeLast(20).Average()  : 0m;
            var ma50  = closes.Count >= 50  ? closes.TakeLast(50).Average()  : 0m;
            var ma200 = closes.Count >= 200 ? closes.TakeLast(200).Average() : closes.Count > 0
                ? closes.Average() : 0m;

            var adx = 0m; var pdi = 0m; var ndi = 0m;
            if (includeAdx)
            {
                var highs = ExtractDecimals(quote, "high");
                var lows  = ExtractDecimals(quote, "low");
                (adx, pdi, ndi) = CalculateAdx(highs, lows, closes);
            }

            return new SymbolData(price, prevClose, ma20, ma50, ma200, adx, pdi, ndi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market conditions: Yahoo Finance failed for {Symbol}", symbol);
            return null;
        }
    }

    private static List<decimal> ExtractDecimals(JsonElement quote, string field)
    {
        return quote.GetProperty(field)
            .EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetDecimal())
            .ToList();
    }

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

        var dxValues = new List<decimal>();
        var finalPdi = 0m;
        var finalNdi = 0m;

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

    private static string DetermineMarketBias(
        decimal spyPrice, decimal ma20, decimal ma50, decimal ma200,
        decimal vix, decimal pdi, decimal ndi)
    {
        var aboveAll     = spyPrice > ma20 && spyPrice > ma50 && spyPrice > ma200;
        var belowAll     = spyPrice < ma20 && spyPrice < ma50 && spyPrice < ma200;
        var bullishTrend = pdi > ndi;
        var bearishTrend = ndi > pdi;

        if (aboveAll && bullishTrend && vix < 18)  return "Bullish";
        if (belowAll && bearishTrend && vix > 25)  return "Bearish";
        if (aboveAll && bullishTrend)              return "Cautiously Bullish";
        if (spyPrice > ma50 && spyPrice < ma20)    return "Caution — Below 20MA";
        if (spyPrice < ma50 && spyPrice > ma200)   return "Cautiously Bearish";
        if (belowAll)                              return "Bearish";
        return "Neutral";
    }

    private static string F(decimal value)   => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Pct(decimal value) => $"{(value >= 0 ? "+" : "")}{value:F2}%";

    private record SymbolData(
        decimal Price,
        decimal PrevClose,
        decimal Ma20,
        decimal Ma50,
        decimal Ma200,
        decimal Adx = 0m,
        decimal PDi = 0m,
        decimal NDi = 0m);
}