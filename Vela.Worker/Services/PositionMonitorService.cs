using Vela.Worker.Engine;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Background service that monitors open positions for broker-side closes.
/// Subscribes to the broker fill handler to detect when IBKR executes a
/// trailing stop or target order without a corresponding Xtrades exit alert.
/// Position polling is intentionally omitted — OCA orders on IBKR handle
/// stop and target execution broker-side and notify via execDetails callbacks.
/// </summary>
public class PositionMonitorService : BackgroundService
{
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly CsvTradeLogger _csv;
    private readonly DiscordNotificationService _discord;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PositionMonitorService> _logger;

    public PositionMonitorService(
        TradeGuard guard,
        IBrokerService broker,
        CsvTradeLogger csv,
        DiscordNotificationService discord,
        IServiceScopeFactory scopeFactory,
        ILogger<PositionMonitorService> logger)
    {
        _guard        = guard;
        _broker       = broker;
        _csv          = csv;
        _discord      = discord;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Position monitor service started.");

        // Subscribe to broker-side fills so trailing stops and targets are detected immediately.
        // IBKR fires execDetails when an OCA stop or target order fills — no polling needed.
        _broker.RegisterBrokerFillHandler(
            (entryOrderId, fillPrice, outcome) =>
                _ = HandleBrokerFillAsync(entryOrderId, fillPrice, outcome, stoppingToken));

        return Task.CompletedTask;
    }

    // -- Helpers --

    // Fired by IbkrBrokerService when a broker-side stop or target order fills.
    // Finds the matching open trade by entry order ID and closes it.
    private async Task HandleBrokerFillAsync(
        string entryOrderId,
        decimal fillPrice,
        TradeOutcome outcome,
        CancellationToken ct)
    {
        var trade = _guard.GetOpenTrades()
            .FirstOrDefault(t => t.OrderId == entryOrderId);

        if (trade is null)
        {
            _logger.LogWarning(
                "Broker fill received for OrderId {OrderId} but no matching open position found.",
                entryOrderId);
            return;
        }

        _logger.LogInformation(
            "Broker-side {Outcome} detected for {Symbol} @ ${Price:F2} — closing position.",
            outcome, trade.Symbol, fillPrice);

        await CloseAndRecordAsync(trade, fillPrice, outcome, ct);
    }

    private async Task CloseAndRecordAsync(
        TradeRecord trade,
        decimal fillPrice,
        TradeOutcome outcome,
        CancellationToken ct)
    {
        var closedTrade = _guard.RegisterClose(
            trade.UserName,
            trade.OptionsContract,
            trade.Symbol,
            fillPrice,
            outcome);

        if (closedTrade is null)
        {
            _logger.LogWarning(
                "Position monitor: RegisterClose returned null for {Symbol} — already closed?",
                trade.Symbol);
            return;
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.DeleteAsync(trade.OrderId, ct);
        }

        await _csv.CloseTradeAsync(closedTrade, ct);
        _guard.LogExposureUpdate();

        _logger.LogInformation(
            "POSITION CLOSED — {Symbol} × {Qty} @ ${Price:F2} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
            closedTrade.Symbol, closedTrade.Quantity, fillPrice,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0, outcome);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);

        // Write exit metrics so broker-side closes appear in analytics alongside Xtrades exits
        if (closedTrade.PnL.HasValue && closedTrade.PnLPercent.HasValue)
        {
            using var metricScope = _scopeFactory.CreateScope();
            var metrics = metricScope.ServiceProvider.GetRequiredService<ITradeMetricsRepository>();
            await metrics.CloseAsync(
                orderId:         trade.OrderId,
                exitPrice:       fillPrice,
                exitAmount:      closedTrade.ExitAmount ?? 0m,
                pnl:             closedTrade.PnL.Value,
                pnlPct:          closedTrade.PnLPercent.Value,
                outcome:         outcome.ToString(),
                closedAt:        closedTrade.ClosedAt ?? DateTimeOffset.UtcNow,
                exitLatencyMs:   null,
                exitSlippagePct: null,
                ct:              ct);
        }
    }
}