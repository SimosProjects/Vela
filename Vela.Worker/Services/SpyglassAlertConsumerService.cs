using Vela.Worker.Data;
using Vela.Worker.Services;

namespace Vela.Worker;

/// <summary>
/// Background service that polls for Spyglass alerts written by Vela.Api and
/// processes each pending alert by logging it and sending a Discord notification.
/// Marks each processed alert as <c>spyglass_notified</c> so it is not re-processed.
/// Phase 8 only: no broker execution. Execution will be wired in a later phase.
/// </summary>
public class SpyglassAlertConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<SpyglassAlertConsumerService> _logger;

    public SpyglassAlertConsumerService(
        IServiceScopeFactory scopeFactory,
        DiscordNotificationService discord,
        ILogger<SpyglassAlertConsumerService> logger)
    {
        _scopeFactory = scopeFactory;
        _discord      = discord;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Spyglass alert consumer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }

        _logger.LogInformation("Spyglass alert consumer stopped.");
    }

    // -- Helpers --

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db   = scope.ServiceProvider.GetRequiredService<VelaDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

            var pending = await db.Alerts
                .Where(a => a.UserName == "SPYGLASS" && a.RiskReason == "spyglass_pending")
                .ToListAsync(ct);

            if (pending.Count == 0)
                return;

            _logger.LogDebug("Spyglass consumer: found {Count} pending alert(s).", pending.Count);

            foreach (var alert in pending)
            {
                await ProcessAlertAsync(alert, repo, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spyglass consumer: poll cycle failed — will retry in 60s.");
        }
    }

    private async Task ProcessAlertAsync(
        AlertEntity alert,
        IAlertRepository repo,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "Spyglass alert received: {Symbol} setups={Strategy} score={XScore} price={Price:F2}",
                alert.Symbol, alert.Strategy, alert.XScore, alert.ActualPriceAtTimeOfAlert);

            await _discord.NotifyCriticalAsync(
                title: $"Spyglass Alert: {alert.Symbol}",
                message: $"{alert.OriginalMessage}\nPrice: {alert.ActualPriceAtTimeOfAlert:C}",
                ct);

            await repo.UpdateRiskReasonAsync(alert.Id, "spyglass_notified", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Spyglass consumer: failed to process alert {Id} ({Symbol}) — will retry next cycle.",
                alert.Id, alert.Symbol);
        }
    }
}