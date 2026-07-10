using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Services;

namespace Vela.Worker;

/// <summary>
/// Background service that polls for Spyglass alerts written by Vela.Api and routes
/// each pending alert through the full risk, sizing, and broker execution pipeline.
/// Marks each alert with the risk engine outcome so the alerts table reflects the
/// same approved/rejected state as Xtrades alerts.
/// </summary>
public class SpyglassAlertConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscordNotificationService _discord;
    private readonly BrokerExecutionService _execution;
    private readonly RiskEngineService _riskEngine;
    private readonly IAlertNormalizer _normalizer;
    private readonly ILogger<SpyglassAlertConsumerService> _logger;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public SpyglassAlertConsumerService(
        IServiceScopeFactory scopeFactory,
        DiscordNotificationService discord,
        BrokerExecutionService execution,
        RiskEngineService riskEngine,
        IAlertNormalizer normalizer,
        ILogger<SpyglassAlertConsumerService> logger)
    {
        _scopeFactory = scopeFactory;
        _discord = discord;
        _execution = execution;
        _riskEngine = riskEngine;
        _normalizer = normalizer;
        _logger = logger;
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
            var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

            var pending = await db.Alerts
                .Where(a => a.UserName == "SPYGLASS" && a.RiskReason == "spyglass_pending")
                .ToListAsync(ct);

            if (pending.Count == 0)
                return;

            _logger.LogDebug("Spyglass consumer: found {Count} pending alert(s).", pending.Count);

            foreach (var entity in pending)
            {
                await ProcessAlertAsync(entity, db, repo, ct);
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
        AlertEntity entity,
        VelaDbContext db,
        IAlertRepository repo,
        CancellationToken ct)
    {
        try
        {
            // Block re-entry on a symbol already traded and closed today (ET).
            // Prevents the scanner re-flagging a symbol whose position closed at target
            // or stop earlier in the same session from opening a second position.
            // ClosedAt comparisons stay in SQL; the ET conversion runs client-side
            // since TimeZoneInfo.ConvertTime cannot be translated by the Npgsql provider.
            var todayEt = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

            var closedTimesToday = await db.TradeMetrics
                .Where(t => t.Symbol == entity.Symbol && t.ClosedAt.HasValue)
                .Select(t => t.ClosedAt!.Value)
                .ToListAsync(ct);

            var tradedToday = closedTimesToday.Any(closedAt =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(closedAt, EasternTime).DateTime) == todayEt);

            if (tradedToday)
            {
                _logger.LogInformation(
                    "Spyglass alert skipped: {Symbol} already traded and closed today.",
                    entity.Symbol);
                await repo.UpdateRiskResultAsync(entity.Id, false, "symbol_traded_today", ct);
                return;
            }

            var alert = BuildAlert(entity);
            var normalized = _normalizer.Normalize(alert);
            var classification = AlertClassifier.Classify(normalized);
            var riskResult = _riskEngine.Evaluate(normalized);

            await repo.UpdateRiskResultAsync(entity.Id, riskResult.Approved, riskResult.Reason, ct);

            if (!riskResult.Approved)
            {
                _logger.LogInformation(
                    "Spyglass alert rejected: {Symbol} — {Reason}",
                    entity.Symbol, riskResult.Reason);
                return;
            }

            _logger.LogInformation(
                "Spyglass alert approved: {Symbol} setups={Strategy} price={Price:F2}{Target} — routing to execution.",
                entity.Symbol,
                entity.Strategy,
                entity.ActualPriceAtTimeOfAlert,
                entity.PriceTarget.HasValue ? $" target={entity.PriceTarget:F2}" : string.Empty);

            await _discord.NotifyApprovedAlertAsync(normalized, classification, ct);
            await _execution.HandleEntryAsync(normalized, classification, isAverage: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Spyglass consumer: failed to process alert {Id} ({Symbol}) — will retry next cycle.",
                entity.Id, entity.Symbol);
        }
    }

    // Reconstructs an Alert DTO from a persisted AlertEntity so the normal execution
    // pipeline receives the same shape it would from AlertPollingService or SignalR.
    // PricePaid is left null so the normalizer fills it from ActualPriceAtTimeOfAlert,
    // producing 0% slippage — matching the market-order intent of Spyglass alerts.
    // PriceTarget flows through so PositionSizer uses the Spyglass-computed target
    // instead of the configured StockTargetMultiple.
    private static Alert BuildAlert(AlertEntity entity) => new(
        Id:                       entity.Id,
        UserId:                   null,
        UserName:                 entity.UserName,
        Symbol:                   entity.Symbol,
        Type:                     entity.Type,
        Direction:                entity.Direction,
        Strike:                   null,
        Expiration:               null,
        OptionsContractSymbol:    null,
        ContractDescription:      null,
        Side:                     entity.Side,
        Status:                   entity.Status,
        Result:                   entity.Result,
        ActualPriceAtTimeOfAlert: entity.ActualPriceAtTimeOfAlert,
        ActualPriceAtTimeOfExit:  null,
        PricePaid:                null,
        PriceAtExit:              null,
        HighestPrice:             null,
        LowestPrice:              null,
        LastCheckedPrice:         entity.LastCheckedPrice,
        PriceTarget:              entity.PriceTarget,
        Risk:                     entity.Risk,
        LastKnownPercentProfit:   entity.LastKnownPercentProfit,
        IsProfitableTrade:        entity.IsProfitableTrade,
        XScore:                   entity.XScore,
        CanAverage:               entity.CanAverage,
        TimeOfEntryAlert:         entity.TimeOfEntryAlert?.ToString("o"),
        TimeOfFullExitAlert:      null,
        FormattedLength:          entity.FormattedLength,
        IsSwing:                  entity.IsSwing,
        IsBullish:                entity.IsBullish,
        IsShort:                  entity.IsShort,
        Strategy:                 entity.Strategy,
        OriginalMessage:          entity.OriginalMessage,
        OriginalExitMessage:      null,
        UserMeta:                 null,
        DiscordRank:              null);
}