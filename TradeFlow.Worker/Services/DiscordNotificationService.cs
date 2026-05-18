using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Sends trading alert notifications to a Discord channel via webhook.
/// Called when an alert is approved by the risk engine, both from
/// the REST polling service and the SignalR live feed.
/// </summary>
public class DiscordNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly string? _webhookUrl;
    private readonly string? _criticalWebhookUrl;

    public DiscordNotificationService(
        ILogger<DiscordNotificationService> logger)
    {
        _logger     = logger;
        _webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        _criticalWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_CRITICAL_WEBHOOK_URL");
        _httpClient = new HttpClient();

        if (string.IsNullOrWhiteSpace(_webhookUrl))
            _logger.LogWarning(
                "DISCORD_WEBHOOK_URL not set — notifications disabled.");
    }

    /// <summary>
    /// Posts an approved alert to Discord as an embedded message.
    /// Silently skips if webhook URL is not configured.
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
            var embed = BuildEmbed(alert, classification);
            var payload = new { embeds = new[] { embed } };

            var response = await _httpClient.PostAsJsonAsync(
                _webhookUrl, payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord webhook returned {Status}: {Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Never let notification failure affect the main pipeline
            _logger.LogWarning(ex,
                "Failed to send Discord notification — continuing.");
        }
    }

    /// <summary>
    /// Posts an order placed notification to Discord showing fill details and bracket prices.
    /// Silently skips if the webhook URL is not configured.
    /// </summary>
    /// <param name="trade">The trade record with fill details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task NotifyOrderPlacedAsync(
        TradeRecord trade,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

        try
        {
            var typeEmoji = trade.TradeType == TradeType.Options
                ? (trade.Direction == "call" ? "📈" : "📉")
                : "🏦";

            var embed = new
            {
                title  = $"{typeEmoji} ORDER PLACED — {trade.Symbol}",
                color  = trade.Direction == "call" ? 0x2ECC71 : trade.Direction == "put" ? 0xE74C3C : 0x3498DB,
                fields = new[]
                {
                    new { name = "Symbol", value = trade.Symbol, inline = true },
                    new { name = "Contracts", value = trade.Quantity.ToString(), inline = true },
                    new { name = "Entry", value = $"${trade.EntryPrice:F2}", inline = true },
                    new { name = "Stop", value = $"${trade.StopPrice:F2}", inline = true },
                    new { name = "Target", value = $"${trade.TargetPrice:F2}", inline = true },
                    new { name = "Amount", value = $"${trade.EntryAmount:F2}", inline = true },
                },
                footer    = new { text = "TradeFlow Order" },
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_webhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send order placed Discord notification.");
        }
    }

    /// <summary>
    /// Posts a position closed notification to Discord showing the P&amp;L result.
    /// Silently skips if the webhook URL is not configured.
    /// </summary>
    /// <param name="trade">The closed trade record with P&amp;L populated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task NotifyPositionClosedAsync(
        TradeRecord trade,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

        try
        {
            var isWin  = trade.PnL >= 0;
            var emoji  = isWin ? "✅" : "🛑";
            var color  = isWin ? 0x2ECC71 : 0xE74C3C;
            var pnlSign = trade.PnL >= 0 ? "+" : "";

            var embed = new
            {
                title  = $"{emoji} POSITION CLOSED — {trade.Symbol}",
                color,
                fields = new[]
                {
                    new { name = "Symbol",   value = trade.Symbol, inline = true },
                    new { name = "Outcome",  value = trade.Result.ToString(), inline = true },
                    new { name = "Exit",     value = $"${trade.ExitPrice:F2}", inline = true },
                    new { name = "P&L",      value = $"{pnlSign}${trade.PnL:F2}", inline = true },
                    new { name = "P&L %",    value = $"{pnlSign}{trade.PnLPercent:F2}%", inline = true },
                    new { name = "Amount",   value = $"${trade.ExitAmount:F2}", inline = true },
                },
                footer    = new { text = "TradeFlow Trade Result" },
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(_webhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send position closed Discord notification.");
        }
    }

    /// <summary>
    /// Posts a critical system alert to the dedicated critical Discord channel.
    /// Used for infrastructure problems requiring immediate intervention.
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
                color       = 0xFF0000, // red
                footer      = new { text = "TradeFlow Critical" },
                timestamp   = DateTimeOffset.UtcNow.ToString("o")
            };

            var payload = new { embeds = new[] { embed } };
            await _httpClient.PostAsJsonAsync(
                _criticalWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send critical Discord notification.");
        }
    }

    private static object BuildEmbed(
        Alert alert,
        AlertClassification classification)
    {
        // Color: green for calls, red for puts, blue for stock
        var color = classification.Category switch
        {
            AlertCategory.CallOptionEntry => 0x2ECC71,  // green
            AlertCategory.PutOptionEntry  => 0xE74C3C,  // red
            AlertCategory.StockEntry      => 0x3498DB,  // blue
            _                             => 0x95A5A6   // gray
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
            Field("Symbol",  alert.Symbol ?? "—",         true),
            Field("Trader",  alert.UserName ?? "—",        true),
            Field("xScore",  alert.XScore?.ToString("F0") ?? "—", true),
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
            footer  = new { text = "TradeFlow Alert System" },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };
    }

    private static object Field(string name, string value, bool inline) =>
        new { name, value, inline };
}