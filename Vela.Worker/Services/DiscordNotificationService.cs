using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Sends trading notifications to Discord via webhooks.
/// Two channels are used: the main alerts channel for approved risk engine signals,
/// and the trade execution channel for broker-confirmed fills and closes.
/// </summary>
public class DiscordNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly string? _webhookUrl;
    private readonly string? _executionWebhookUrl;
    private readonly string? _criticalWebhookUrl;
    private readonly string? _summaryWebhookUrl;

    public DiscordNotificationService(
        ILogger<DiscordNotificationService> logger)
    {
        _logger              = logger;
        _webhookUrl          = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        _executionWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_TRADE_EXECUTION_WEBHOOK_URL");
        _criticalWebhookUrl  = Environment.GetEnvironmentVariable("DISCORD_CRITICAL_WEBHOOK_URL");
        _summaryWebhookUrl   = Environment.GetEnvironmentVariable("DISCORD_SUMMARY_WEBHOOK_URL");
        _httpClient          = new HttpClient();

        if (string.IsNullOrWhiteSpace(_webhookUrl))
            _logger.LogWarning("DISCORD_WEBHOOK_URL not set — alert notifications disabled.");

        if (string.IsNullOrWhiteSpace(_executionWebhookUrl))
            _logger.LogWarning("DISCORD_TRADE_EXECUTION_WEBHOOK_URL not set — execution notifications disabled.");
    }

    /// <summary>
    /// Posts an approved alert to the alerts Discord channel.
    /// Fires when the risk engine approves an alert, before any order is placed.
    /// Independent of IB Gateway, this is a signal notification, not a trade confirmation.
    /// </summary>
    public async Task NotifyApprovedAlertAsync(
        Alert alert,
        AlertClassification classification,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl))
            return;

        try
        {
            var embed   = BuildAlertEmbed(alert, classification);
            var payload = new { embeds = new[] { embed } };

            var response = await _httpClient.PostAsJsonAsync(
                _webhookUrl, payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord alert webhook returned {Status}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Discord alert notification — continuing.");
        }
    }

    /// <summary>
    /// Posts an order filled notification to the trade execution Discord channel.
    /// Only fires after IB Gateway confirms the entry fill, not on alert approval.
    /// </summary>
    public async Task NotifyOrderPlacedAsync(
        TradeRecord trade,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_executionWebhookUrl)) return;

        try
        {
            var typeEmoji = trade.TradeType == TradeType.Options
                ? (trade.Direction == "call" ? "📈" : "📉")
                : "🏦";

            var embed = new
            {
                title       = $"{typeEmoji} ORDER FILLED — {trade.Symbol}",
                description = FormatContract(trade),
                color       = trade.Direction == "call" ? 0x2ECC71 : trade.Direction == "put" ? 0xE74C3C : 0x3498DB,
                fields      = new[]
                {
                    new { name = "Symbol",   value = trade.Symbol,                    inline = true },
                    new { name = "Qty",      value = trade.Quantity.ToString(),        inline = true },
                    new { name = "Entry",    value = $"${trade.EntryPrice:F2}",        inline = true },
                    new { name = "Stop",     value = $"${trade.StopPrice:F2}",         inline = true },
                    new { name = "Target",   value = $"${trade.TargetPrice:F2}",       inline = true },
                    new { name = "Amount",   value = $"${trade.EntryAmount:F2}",       inline = true },
                },
                footer    = new { text = "Vela Execution" },
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_executionWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send order filled Discord notification.");
        }
    }

    /// <summary>
    /// Posts a position closed notification to the trade execution Discord channel.
    /// Only fires after IB Gateway confirms the exit fill.
    /// </summary>
    public async Task NotifyPositionClosedAsync(
        TradeRecord trade,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_executionWebhookUrl)) return;

        try
        {
            var isWin   = trade.PnL >= 0;
            var emoji   = isWin ? "✅" : "🛑";
            var color   = isWin ? 0x2ECC71 : 0xE74C3C;
            var pnlSign = trade.PnL >= 0 ? "+" : "";

            var embed = new
            {
                title  = $"{emoji} POSITION CLOSED — {trade.Symbol}",
                color,
                fields = new[]
                {
                    new { name = "Symbol",  value = trade.Symbol,                          inline = true },
                    new { name = "Outcome", value = trade.Result.ToString(),                inline = true },
                    new { name = "Exit",    value = $"${trade.ExitPrice:F2}",              inline = true },
                    new { name = "P&L",     value = $"{pnlSign}${trade.PnL:F2}",           inline = true },
                    new { name = "P&L %",   value = $"{pnlSign}{trade.PnLPercent:F2}%",    inline = true },
                    new { name = "Amount",  value = $"${trade.ExitAmount:F2}",             inline = true },
                },
                footer    = new { text = "Vela Execution" },
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_executionWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send position closed Discord notification.");
        }
    }

    /// <summary>
    /// Posts a partial close notification to the trade execution Discord channel.
    /// Fires when a 1DTE position is partially closed at 3pm ET with the remainder
    /// riding overnight as a lotto play.
    /// </summary>
    public async Task NotifyPartialCloseAsync(
        TradeRecord trade,
        int quantityClosed,
        decimal fillPrice,
        decimal partialPnl,
        int remainingQuantity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_executionWebhookUrl)) return;

        try
        {
            var isWin   = partialPnl >= 0;
            var emoji   = isWin ? "✅" : "🛑";
            var pnlSign = partialPnl >= 0 ? "+" : "";

            var embed = new
            {
                title  = $"{emoji} PARTIAL CLOSE — {trade.Symbol}",
                color  = isWin ? 0x2ECC71 : 0xE74C3C,
                fields = new[]
                {
                    new { name = "Symbol",       value = trade.Symbol,                             inline = true },
                    new { name = "Sold",         value = $"{quantityClosed} contracts",            inline = true },
                    new { name = "Fill Price",   value = $"${fillPrice:F2}",                       inline = true },
                    new { name = "Partial P&L",  value = $"{pnlSign}${partialPnl:F2}",            inline = true },
                    new { name = "Remaining",    value = $"{remainingQuantity} contracts (lotto)", inline = true },
                    new { name = "Stop",         value = "Cancelled — overnight lotto hold",       inline = true },
                },
                footer    = new { text = "Vela Execution" },
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_executionWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send partial close Discord notification.");
        }
    }

    /// <summary>
    /// Posts a critical system alert to the critical Discord channel.
    /// Used for infrastructure events, Gateway connect/disconnect, unrecoverable errors.
    /// </summary>
    public async Task NotifyCriticalAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_criticalWebhookUrl))
            return;

        try
        {
            var embed = new
            {
                title,
                description = message,
                color       = 0xFF0000,
                footer      = new { text = "Vela Critical" },
                timestamp   = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_criticalWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send critical Discord notification.");
        }
    }

    /// <summary>
    /// Posts a plain text IB account/position snapshot to the summary Discord channel,
    /// wrapped in a code block. Reuses the summary webhook, no separate channel or env var.
    /// </summary>
    public async Task NotifyIbSnapshotAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_summaryWebhookUrl)) return;

        try
        {
            var payload = new { content = $"```\n{content}\n```" };
            await _httpClient.PostAsJsonAsync(_summaryWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send IB snapshot Discord notification.");
        }
    }

    /// <summary>
    /// Builds a plain text account and open positions snapshot for the Discord snapshot alert.
    /// Stop loss orders are matched by LocalSymbol (options, spaces stripped) or Symbol (stocks)
    /// against OrderType containing TRAIL or STP, missing stop always prints a warning line since
    /// an unprotected position is the failure mode this message exists to surface.
    /// Take profit orders are matched the same way against OrderType "LMT" — most positions are
    /// trail-only by design, so a missing take profit block is simply omitted, not warned on.
    /// </summary>
    public static string BuildSnapshotMessage(
        AccountSnapshot account,
        List<IbkrPosition> positions,
        List<IbkrOpenOrder> orders)
    {
        var lines = new List<string>
        {
            "======== ACCOUNT ========",
            $"Net Liquidation: {FormatCurrency(account.NetLiquidation)}",
            $"Cash: {FormatCurrency(account.TotalCash)}",
            $"Buying Power: {FormatCurrency(account.BuyingPower)}",
            $"Today's P&L: {FormatSignedCurrency(account.TodayPnL)}",
            "=========================",
            "OPEN POSITIONS",
            "========================="
        };

        if (positions.Count == 0)
        {
            lines.Add("No open positions.");
        }
        else
        {
            for (var i = 0; i < positions.Count; i++)
            {
                AppendPositionLines(lines, positions[i], orders);
                if (i < positions.Count - 1)
                    lines.Add("-------------------------");
            }
        }

        lines.Add("=========================");

        return string.Join("\n", lines);
    }

    // -- Helpers --

    // Builds a human readable contract description for the execution embed.
    // Options: "RBLX Aug 21 2026 $60.00 Call"
    // Stocks:  "RBLX"
    private static string? FormatContract(TradeRecord trade)
    {
        if (trade.TradeType != TradeType.Options) return null;

        var expiry = trade.Expiration is not null &&
                     DateTimeOffset.TryParse(trade.Expiration, out var dt)
            ? dt.ToString("MMM dd yyyy")
            : trade.Expiration ?? "—";

        var strike    = trade.Strike.HasValue ? $"${trade.Strike:F2}" : "—";
        var direction = trade.Direction is not null
            ? char.ToUpper(trade.Direction[0]) + trade.Direction[1..]
            : "—";

        return $"{trade.Symbol} {expiry} {strike} {direction}";
    }

    private static object BuildAlertEmbed(Alert alert, AlertClassification classification)
    {
        var color = classification.Category switch
        {
            AlertCategory.CallOptionEntry => 0x2ECC71,
            AlertCategory.PutOptionEntry  => 0xE74C3C,
            AlertCategory.StockEntry      => 0x3498DB,
            _                             => 0x95A5A6
        };

        var title = classification.Category switch
        {
            AlertCategory.CallOptionEntry => $"📈 CALL — **{alert.Symbol}**",
            AlertCategory.PutOptionEntry  => $"📉 PUT — **{alert.Symbol}**",
            AlertCategory.StockEntry      => $"🏦 STOCK — **{alert.Symbol}**",
            _                             => $"⚡ ALERT — **{alert.Symbol}**"
        };

        var description = alert.ContractDescription
            ?? $"{alert.Symbol} {alert.Direction?.ToUpper()} {alert.Side?.ToUpper()}";

        var fields = new List<object>
        {
            Field("Symbol", alert.Symbol ?? "—",                true),
            Field("Trader", alert.UserName ?? "—",               true),
            Field("xScore", alert.XScore?.ToString("F0") ?? "—", true),
        };

        if (alert.PricePaid.HasValue)
            fields.Add(Field("Entry Price", $"${alert.PricePaid:F2}", true));

        if (alert.Strike.HasValue)
            fields.Add(Field("Strike", $"${alert.Strike:F0}", true));

        if (alert.Expiration is not null &&
            DateTimeOffset.TryParse(alert.Expiration, out var exp))
            fields.Add(Field("Expiration", exp.ToString("MMM dd yyyy"), true));

        if (!string.IsNullOrWhiteSpace(alert.Risk))
            fields.Add(Field("Risk", alert.Risk, true));

        if (!string.IsNullOrWhiteSpace(alert.OriginalMessage))
            fields.Add(Field("Message", alert.OriginalMessage, false));

        return new
        {
            title,
            description,
            color,
            fields,
            footer    = new { text = "Vela Alert" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    private static object Field(string name, string value, bool inline) =>
        new { name, value, inline };

    // Appends the Symbol / Quantity / Avg Cost / Orders block for a single position.
    private static void AppendPositionLines(
        List<string> lines, IbkrPosition position, List<IbkrOpenOrder> orders)
    {
        var isOptions = position.SecType == "OPT";

        // Options avgCost is IBKR's per-contract cost (already x100), divide down to the
        // per-share premium — same convention as StartupReconciliationService.BuildManualPosition.
        var entryPrice = isOptions ? position.AvgCost / 100m : position.AvgCost;
        var key = PositionKey(position);
        var matchingOrders = orders.Where(o => OrderKey(o) == key).ToList();

        var stopOrder = matchingOrders.FirstOrDefault(o =>
            o.OrderType.Contains("TRAIL", StringComparison.OrdinalIgnoreCase) ||
            o.OrderType.Contains("STP", StringComparison.OrdinalIgnoreCase));
        var targetOrder = matchingOrders.FirstOrDefault(o => o.OrderType == "LMT");

        lines.Add(position.Symbol);
        lines.Add($"{position.Quantity} {(isOptions ? "Contracts" : "Shares")}");
        lines.Add($"Avg Cost: ${entryPrice:F2}");
        lines.Add("Orders");

        if (stopOrder is not null)
        {
            lines.Add("✓ Stop Loss");
            lines.Add($"{stopOrder.Action} {stopOrder.Quantity:0} @ {stopOrder.AuxPrice ?? 0:F2}");
            lines.Add(stopOrder.Status);
        }
        else
        {
            lines.Add("⚠️ NO STOP LOSS FOUND");
        }

        if (targetOrder is not null)
        {
            lines.Add("✓ Take Profit");
            lines.Add($"{targetOrder.Action} {targetOrder.Quantity:0} @ {targetOrder.LmtPrice ?? 0:F2}");
            lines.Add(targetOrder.Status);
        }
    }

    private static string PositionKey(IbkrPosition position) =>
        position.SecType == "OPT" ? (position.LocalSymbol ?? "").Replace(" ", "") : position.Symbol;

    private static string OrderKey(IbkrOpenOrder order) =>
        order.SecType == "OPT" ? (order.LocalSymbol ?? "").Replace(" ", "") : order.Symbol;

    private static string FormatCurrency(decimal value) => $"${value:N0}";

    private static string FormatSignedCurrency(decimal value)
    {
        var sign = value < 0 ? "-" : "+";
        return $"{sign}${Math.Abs(value):N0}";
    }
}