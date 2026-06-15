namespace Vela.Analytics;

/// <summary>
/// Generates a self-contained HTML analytics report from a ReportData instance.
/// No external dependencies, pure HTML, CSS, and inline SVG.
/// Renders correctly in any modern browser and can be emailed as a single file.
/// </summary>
public class HtmlReportGenerator
{
    private readonly ILogger<HtmlReportGenerator> _logger;

    private static readonly TimeZoneInfo Et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // CSS extracted as a constant to avoid double-brace escaping inside interpolated strings
    private const string Css = @"
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
         background: #f0f4f8; color: #1a1a2e; font-size: 14px; }
  .header { background: linear-gradient(135deg, #1f4e79 0%, #2e75b6 100%);
            color: white; padding: 32px 40px; }
  .header h1 { font-size: 28px; font-weight: 700; margin-bottom: 4px; }
  .header .period { font-size: 14px; opacity: 0.85; margin-top: 6px; }
  .header .generated { font-size: 12px; opacity: 0.65; margin-top: 4px; }
  .container { max-width: 1200px; margin: 0 auto; padding: 24px 20px; }
  .section { background: white; border-radius: 8px; padding: 24px;
             margin-bottom: 20px; box-shadow: 0 1px 4px rgba(0,0,0,0.08); }
  .section h2 { font-size: 16px; font-weight: 700; color: #1f4e79;
                border-bottom: 2px solid #d6e4f0; padding-bottom: 10px;
                margin-bottom: 16px; }
  .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
              gap: 12px; }
  .kpi { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 6px;
         padding: 14px 16px; }
  .kpi .label { font-size: 11px; color: #64748b; text-transform: uppercase;
                letter-spacing: 0.5px; margin-bottom: 6px; }
  .kpi .value { font-size: 22px; font-weight: 700; color: #1f4e79; }
  .kpi .value.green { color: #16a34a; }
  .kpi .value.red { color: #dc2626; }
  .kpi .value.amber { color: #d97706; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th { background: #1f4e79; color: white; padding: 10px 12px;
       text-align: left; font-weight: 600; font-size: 12px; }
  td { padding: 9px 12px; border-bottom: 1px solid #f1f5f9; }
  tr:last-child td { border-bottom: none; }
  tr:nth-child(even) td { background: #f8fafc; }
  .badge { display: inline-block; padding: 2px 8px; border-radius: 12px;
           font-size: 11px; font-weight: 600; }
  .badge.win { background: #dcfce7; color: #16a34a; }
  .badge.loss { background: #fee2e2; color: #dc2626; }
  .badge.open { background: #dbeafe; color: #1d4ed8; }
  .badge.target { background: #dcfce7; color: #16a34a; }
  .badge.stopped { background: #fee2e2; color: #dc2626; }
  .badge.xtrades { background: #fef9c3; color: #854d0e; }
  .two-col { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
  .chart-container { width: 100%; overflow-x: auto; }
  svg.chart { width: 100%; min-width: 500px; }
  .no-data { text-align: center; color: #94a3b8; padding: 32px; font-style: italic; }
  @media (max-width: 700px) { .two-col { grid-template-columns: 1fr; } }
";

    public HtmlReportGenerator(ILogger<HtmlReportGenerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates the HTML report and saves it to the output directory.
    /// Returns the full path of the saved file.
    /// </summary>
    public async Task<string> GenerateAsync(ReportData data, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var periodLabel = data.ReportType switch
        {
            ReportType.Weekly  => "weekly",
            ReportType.Monthly => "monthly",
            ReportType.Custom  => "custom",
            _                  => "report"
        };

        var etNow    = TimeZoneInfo.ConvertTime(data.GeneratedAt, Et);
        var filename = $"{periodLabel}_{etNow:yyyy-MM-dd}.html";
        var path     = Path.Combine(outputDirectory, filename);

        var html = BuildHtml(data);
        await File.WriteAllTextAsync(path, html);

        _logger.LogInformation("HTML report written: {Path}", path);
        return path;
    }

    private static string BuildHtml(ReportData data)
    {
        var etFrom = TimeZoneInfo.ConvertTime(data.From, Et);
        var etTo   = TimeZoneInfo.ConvertTime(data.To.AddSeconds(-1), Et);
        var etGen  = TimeZoneInfo.ConvertTime(data.GeneratedAt, Et);

        var periodLabel = $"{etFrom:MMM dd, yyyy} \u2014 {etTo:MMM dd, yyyy} ET";
        var title       = $"Vela {data.ReportType} Report";

        var body = string.Concat(
            BuildSummarySection(data),
            BuildFilterSection(data),
            BuildWinLossSection(data),
            BuildTraderSection(data),
            BuildSymbolSection(data),
            BuildTypeSection(data),
            BuildLatencySection(data),
            BuildExposureSection(data),
            BuildChartSection(data),
            BuildTradesSection(data));

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{title}</title>
<style>{Css}</style>
</head>
<body>
<div class=""header"">
  <h1>Vela {data.ReportType} Report</h1>
  <div class=""period"">{periodLabel}</div>
  <div class=""generated"">Generated {etGen:MMM dd, yyyy HH:mm} ET</div>
</div>
<div class=""container"">
{body}
</div>
</body>
</html>";
    }

    // -- Section 1: Executive Summary --
    private static string BuildSummarySection(ReportData d)
    {
        var pnlClass = d.TotalPnL >= 0 ? "green" : "red";
        var pnlSign  = d.TotalPnL >= 0 ? "+" : "";
        var avgClass = d.AvgPnLPerTrade >= 0 ? "green" : "red";
        var avgSign  = d.AvgPnLPerTrade >= 0 ? "+" : "";

        return Section("1. Executive Summary",
            KpiGrid(
                Kpi("Total Trades",        d.TotalTrades.ToString()),
                Kpi("Closed Trades",       d.ClosedTrades.ToString()),
                Kpi("Open Trades",         d.OpenTrades.ToString(),  d.OpenTrades > 0 ? "amber" : ""),
                Kpi("Total P&amp;L",       $"{pnlSign}${d.TotalPnL:N2}", pnlClass),
                Kpi("Avg P&amp;L / Trade", $"{avgSign}${d.AvgPnLPerTrade:N2}", avgClass),
                Kpi("Win Rate",            $"{d.WinRatePct:F1}%",   d.WinRatePct >= 50 ? "green" : "red"),
                Kpi("Wins",                d.Wins.ToString(),        "green"),
                Kpi("Losses",              d.Losses.ToString(),      d.Losses > 0 ? "red" : "")));
    }

    // -- Section 2: Filter Analysis --
    private static string BuildFilterSection(ReportData d) =>
        Section("2. Trade Filter Analysis",
            KpiGrid(
                Kpi("Alerts Received",     d.TotalAlerts.ToString()),
                Kpi("Trades Taken",        d.TotalTrades.ToString()),
                Kpi("Filter Rate",         $"{d.FilterRatePct:F1}%"),
                Kpi("Alerts Filtered Out", (d.TotalAlerts - d.TotalTrades).ToString())));

    // -- Section 3: Win/Loss Breakdown --
    private static string BuildWinLossSection(ReportData d)
    {
        var largestWinSign  = d.LargestWin  >= 0 ? "+" : "";
        var largestLossSign = d.LargestLoss >= 0 ? "+" : "";
        var avgPnlSign      = d.AvgPnLPerTrade >= 0 ? "+" : "";
        var avgPnlClass     = d.AvgPnLPerTrade >= 0 ? "green" : "red";

        var outcomeTable = $@"<br><table>
    <thead><tr><th>Outcome Type</th><th>Count</th><th>% of Closed</th></tr></thead>
    <tbody>
      {OutcomeRow("Target Hit",         d.TargetHits,   d.ClosedTrades, "target")}
      {OutcomeRow("Stopped Out",        d.StoppedOuts,  d.ClosedTrades, "stopped")}
      {OutcomeRow("Xtrades Exit (STC)", d.XtradesExits, d.ClosedTrades, "xtrades")}
    </tbody>
  </table>";

        return Section("3. Win / Loss Breakdown",
            KpiGrid(
                Kpi("Win Rate",            $"{d.WinRatePct:F1}%",               d.WinRatePct >= 50 ? "green" : "red"),
                Kpi("Avg Win %",           $"+{d.AvgWinPct:F2}%",               "green"),
                Kpi("Avg Loss %",          $"{d.AvgLossPct:F2}%",               "red"),
                Kpi("Avg P&amp;L / Trade", $"{avgPnlSign}${d.AvgPnLPerTrade:N2}", avgPnlClass),
                Kpi("Largest Win",         $"{largestWinSign}${d.LargestWin:N2}",   "green"),
                Kpi("Largest Loss",        $"{largestLossSign}${d.LargestLoss:N2}", "red"),
                Kpi("Max Consec. Losses",  d.MaxConsecutiveLosses.ToString(), d.MaxConsecutiveLosses >= 3 ? "red" : ""),
                Kpi("Break Evens",         d.BreakEvens.ToString()))
            + outcomeTable);
    }

    // -- Section 4: Trader Performance --
    private static string BuildTraderSection(ReportData d)
    {
        if (d.TraderBreakdown.Count == 0)
            return Section("4. Trader Performance", NoData());

        var rows = string.Concat(d.TraderBreakdown.Select((t, i) =>
        {
            var winColor = t.AvgWinPct      >= 0 ? "#16a34a" : "#dc2626";
            var avgColor = t.AvgPnLPerTrade >= 0 ? "#16a34a" : "#dc2626";
            var totColor = t.TotalPnL       >= 0 ? "#16a34a" : "#dc2626";
            var winSign  = t.AvgWinPct      >= 0 ? "+" : "";
            var avgSign  = t.AvgPnLPerTrade >= 0 ? "+" : "";
            var totSign  = t.TotalPnL       >= 0 ? "+" : "";
            return $@"<tr>
        <td>{i + 1}</td>
        <td><strong>{t.TraderName}</strong></td>
        <td>{t.TotalTrades}</td>
        <td>{t.Wins}</td>
        <td>{t.Losses}</td>
        <td>{t.WinRatePct:F1}%</td>
        <td style=""color:{winColor}"">{winSign}{t.AvgWinPct:F2}%</td>
        <td style=""color:#dc2626"">{t.AvgLossPct:F2}%</td>
        <td style=""color:{avgColor};font-weight:600"">{avgSign}${t.AvgPnLPerTrade:N2}</td>
        <td style=""color:{totColor};font-weight:600"">{totSign}${t.TotalPnL:N2}</td>
      </tr>";
        }));

        return Section("4. Trader Performance (Ranked by Avg P&amp;L / Trade)",
            $@"<table>
    <thead><tr>
      <th>#</th><th>Trader</th><th>Trades</th><th>Wins</th><th>Losses</th>
      <th>Win Rate</th><th>Avg Win %</th><th>Avg Loss %</th>
      <th>Avg P&amp;L / Trade</th><th>Total P&amp;L</th>
    </tr></thead>
    <tbody>{rows}</tbody>
  </table>");
    }

    // -- Section 5: Symbol Performance --
    private static string BuildSymbolSection(ReportData d)
    {
        if (d.SymbolBreakdown.Count == 0)
            return Section("5. Symbol Performance", NoData());

        var rows = string.Concat(d.SymbolBreakdown.Select(s =>
        {
            var avgColor = s.AvgPnLPerTrade >= 0 ? "#16a34a" : "#dc2626";
            var totColor = s.TotalPnL       >= 0 ? "#16a34a" : "#dc2626";
            var avgSign  = s.AvgPnLPerTrade >= 0 ? "+" : "";
            var totSign  = s.TotalPnL       >= 0 ? "+" : "";
            return $@"<tr>
        <td><strong>{s.Symbol}</strong></td>
        <td>{s.TotalTrades}</td>
        <td>{s.Wins}</td>
        <td>{s.WinRatePct:F1}%</td>
        <td style=""color:{avgColor}"">{avgSign}${s.AvgPnLPerTrade:N2}</td>
        <td style=""color:{totColor};font-weight:600"">{totSign}${s.TotalPnL:N2}</td>
      </tr>";
        }));

        return Section("5. Symbol Performance",
            $@"<table>
    <thead><tr>
      <th>Symbol</th><th>Trades</th><th>Wins</th><th>Win Rate</th>
      <th>Avg P&amp;L / Trade</th><th>Total P&amp;L</th>
    </tr></thead>
    <tbody>{rows}</tbody>
  </table>");
    }

    // -- Section 6: Options vs Stocks --
    private static string BuildTypeSection(ReportData d) =>
        Section("6. Options vs Stocks",
            KpiGrid(
                Kpi("Options Trades", $"{d.OptionsTradesPct:F1}%"),
                Kpi("Stock Trades",   $"{d.StockTradesPct:F1}%")));

    // -- Section 7: Latency & Slippage --
    private static string BuildLatencySection(ReportData d) =>
        Section("7. Latency &amp; Slippage", $@"
  <div class=""two-col"">
    <div>
      <h3 style=""font-size:13px;color:#64748b;margin-bottom:12px"">LATENCY (alert received &#x2192; order filled)</h3>
      {KpiGrid(
          Kpi("Avg Latency", $"{d.AvgLatencyMs:N0} ms"),
          Kpi("p50 Latency", $"{d.P50LatencyMs:N0} ms"),
          Kpi("p95 Latency", $"{d.P95LatencyMs:N0} ms"),
          Kpi("Max Latency", $"{d.MaxLatencyMs:N0} ms", d.MaxLatencyMs > 5000 ? "amber" : ""))}
    </div>
    <div>
      <h3 style=""font-size:13px;color:#64748b;margin-bottom:12px"">SLIPPAGE (alerted price vs fill price)</h3>
      {KpiGrid(
          Kpi("Avg Slippage", $"{d.AvgSlippagePct:F3}%", d.AvgSlippagePct > 1 ? "amber" : ""),
          Kpi("Max Slippage", $"{d.MaxSlippagePct:F3}%", d.MaxSlippagePct > 2 ? "red"   : ""))}
    </div>
  </div>");

    // -- Section 8: Account Exposure --
    private static string BuildExposureSection(ReportData d) =>
        Section("8. Account Exposure Profile",
            KpiGrid(
                Kpi("Avg Exposure", $"{d.AvgExposurePct:F1}%", d.AvgExposurePct > 30 ? "amber" : ""),
                Kpi("Max Exposure", $"{d.MaxExposurePct:F1}%", d.MaxExposurePct > 50 ? "red"   : "")));

    // -- Section 9: Cumulative P&L Chart --
    private static string BuildChartSection(ReportData d)
    {
        if (d.DailyPnLSeries.Count == 0)
            return Section("9. Cumulative P&amp;L Chart", NoData("No closed trades to chart."));

        const int w = 900, h = 280;
        const int padL = 80, padR = 20, padT = 20, padB = 40;
        int chartW = w - padL - padR;
        int chartH = h - padT - padB;

        var values = d.DailyPnLSeries.Select(p => (double)p.CumulativePnL).ToList();
        var labels = d.DailyPnLSeries.Select(p => p.Date.ToString("MM/dd")).ToList();
        int n = values.Count;

        double minV = values.Min();
        double maxV = values.Max();
        if (Math.Abs(maxV - minV) < 0.01) { minV -= 1; maxV += 1; }

        double ScaleY(double v) => padT + chartH - (v - minV) / (maxV - minV) * chartH;
        double ScaleX(int i)    => padL + (n == 1 ? chartW / 2.0 : (double)i / (n - 1) * chartW);

        double zeroY   = ScaleY(0);
        var points     = string.Join(" ", values.Select((v, i) => $"{ScaleX(i):F1},{ScaleY(v):F1}"));
        var fillPoints = $"{points} {ScaleX(n - 1):F1},{zeroY:F1} {ScaleX(0):F1},{zeroY:F1}";

        var yTicks = string.Concat(Enumerable.Range(0, 5).Select(i =>
        {
            double v = minV + (maxV - minV) * i / 4.0;
            double y = ScaleY(v);
            var sign = v >= 0 ? "+" : "";
            return $@"<line x1=""{padL}"" y1=""{y:F1}"" x2=""{padL + chartW}"" y2=""{y:F1}"" stroke=""#f1f5f9"" stroke-width=""1""/>
      <text x=""{padL - 6}"" y=""{y + 4:F1}"" text-anchor=""end"" font-size=""11"" fill=""#64748b"">{sign}${v:N0}</text>";
        }));

        int step = Math.Max(1, n / 10);
        var xLabels = string.Concat(labels.Select((l, i) =>
        {
            if (i % step != 0 && i != n - 1) return "";
            return $@"<text x=""{ScaleX(i):F1}"" y=""{h - 6}"" text-anchor=""middle"" font-size=""11"" fill=""#64748b"">{l}</text>";
        }));

        var circles = string.Concat(values.Select((v, i) =>
            $@"<circle cx=""{ScaleX(i):F1}"" cy=""{ScaleY(v):F1}"" r=""3"" fill=""{(v >= 0 ? "#16a34a" : "#dc2626")}"" stroke=""white"" stroke-width=""1.5""/>"));

        var lineColor = values.Last() >= 0 ? "#16a34a" : "#dc2626";
        var finalPnL  = d.DailyPnLSeries.Last().CumulativePnL;
        var finalSign = finalPnL >= 0 ? "+" : "";
        var finalX    = ScaleX(n - 1);
        var finalY    = ScaleY(values.Last());

        return Section("9. Cumulative P&amp;L Chart", $@"
  <div class=""chart-container"">
    <svg class=""chart"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">
      {yTicks}
      <line x1=""{padL}"" y1=""{zeroY:F1}"" x2=""{padL + chartW}"" y2=""{zeroY:F1}""
            stroke=""#94a3b8"" stroke-width=""1"" stroke-dasharray=""4,3""/>
      <polygon points=""{fillPoints}"" fill=""{lineColor}"" fill-opacity=""0.1""/>
      <polyline points=""{points}"" fill=""none"" stroke=""{lineColor}""
                stroke-width=""2.5"" stroke-linejoin=""round"" stroke-linecap=""round""/>
      {circles}
      <text x=""{finalX + 6:F1}"" y=""{finalY + 4:F1}"" font-size=""12""
            font-weight=""700"" fill=""{lineColor}"">{finalSign}${finalPnL:N2}</text>
      {xLabels}
    </svg>
  </div>");
    }

    // -- Section 10: All Trades Detail --
    private static string BuildTradesSection(ReportData d)
    {
        if (d.AllTrades.Count == 0)
            return Section("10. All Trades", NoData());

        var rows = string.Concat(d.AllTrades.Select(t =>
        {
            var etReceived = TimeZoneInfo.ConvertTime(t.AlertReceivedAt, Et);
            var pnlColor   = t.PnL.HasValue ? (t.PnL >= 0 ? "#16a34a" : "#dc2626") : "#64748b";
            var pnlSign    = t.PnL.HasValue    && t.PnL    >= 0 ? "+" : "";
            var pnlPctSign = t.PnLPct.HasValue && t.PnLPct >= 0 ? "+" : "";
            var pnlStr     = t.PnL.HasValue    ? $"{pnlSign}${t.PnL:N2}"          : "\u2014";
            var pnlPctStr  = t.PnLPct.HasValue ? $"{pnlPctSign}{t.PnLPct:F2}%"   : "\u2014";

            var statusBadge = t.ClosedAt.HasValue
                ? (t.PnL >= 0
                    ? "<span class=\"badge win\">WIN</span>"
                    : "<span class=\"badge loss\">LOSS</span>")
                : "<span class=\"badge open\">OPEN</span>";

            var outcomeBadge = t.Outcome switch
            {
                "TargetHit"   => "<span class=\"badge target\">Target</span>",
                "StoppedOut"  => "<span class=\"badge stopped\">Stopped</span>",
                "XtradesExit" => "<span class=\"badge xtrades\">STC</span>",
                _             => ""
            };

            return $@"<tr>
        <td>{etReceived:MM/dd HH:mm}</td>
        <td>{t.TraderName}</td>
        <td><strong>{t.Symbol}</strong></td>
        <td>{t.TradeType} {t.Direction}</td>
        <td>{t.Quantity}</td>
        <td>${t.AlertedPrice:F2}</td>
        <td>${t.FillPrice:F2}</td>
        <td>{t.SlippagePct:F3}%</td>
        <td>${t.EntryAmount:N2}</td>
        <td>{t.LatencyMs:N0}ms</td>
        <td>{t.ExposurePct:F1}%</td>
        <td style=""color:{pnlColor};font-weight:600"">{pnlStr}</td>
        <td style=""color:{pnlColor}"">{pnlPctStr}</td>
        <td>{statusBadge} {outcomeBadge}</td>
      </tr>";
        }));

        return Section("10. All Trades \u2014 Detail", $@"
  <div style=""overflow-x:auto"">
    <table>
      <thead><tr>
        <th>Date ET</th><th>Trader</th><th>Symbol</th><th>Type</th><th>Qty</th>
        <th>Alerted $</th><th>Fill $</th><th>Slippage</th><th>Amount</th>
        <th>Latency</th><th>Exposure</th><th>P&amp;L $</th><th>P&amp;L %</th><th>Status</th>
      </tr></thead>
      <tbody>{rows}</tbody>
    </table>
  </div>");
    }

    // -- Helpers --

    private static string Kpi(string label, string value, string colorClass = "") =>
        $@"<div class=""kpi"">
      <div class=""label"">{label}</div>
      <div class=""value{(colorClass.Length > 0 ? " " + colorClass : "")}"">{value}</div>
    </div>";

    private static string KpiGrid(params string[] kpis) =>
        $@"<div class=""kpi-grid"">{string.Concat(kpis)}</div>";

    private static string Section(string title, string content) =>
        $@"<div class=""section"">
  <h2>{title}</h2>
  {content}
</div>";

    private static string NoData(string message = "No trades in this period.") =>
        $@"<div class=""no-data"">{message}</div>";

    private static string OutcomeRow(string label, int count, int total, string badgeClass) =>
        $@"<tr>
        <td><span class=""badge {badgeClass}"">{label}</span></td>
        <td>{count}</td>
        <td>{(total > 0 ? $"{(decimal)count / total * 100:F1}%" : "\u2014")}</td>
      </tr>";
}