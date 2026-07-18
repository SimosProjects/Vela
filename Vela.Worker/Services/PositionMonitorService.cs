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

    // Test-only observability hook: the broker-fill handler is registered as a fire-and-forget
    // Action (matching IBrokerService's callback-style contract), so there is no return value
    // for a caller to await. Tests that invoke the captured handler directly can await this
    // instead of a fixed delay to know the dispatched HandleBrokerFillAsync call has genuinely
    // completed. Never read by production code.
    internal Task? LastFillDispatch { get; private set; }

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
                LastFillDispatch = HandleBrokerFillAsync(entryOrderId, fillPrice, outcome, stoppingToken));

        // Fires when a stop/target order's bounded completion wait expires with only a
        // partial fill confirmed — must correct quantity, never record a false full close.
        _broker.RegisterBrokerPartialFillHandler(
            partialFill => _ = HandleBrokerPartialFillAsync(partialFill, stoppingToken));

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

    // Fired by IbkrBrokerService when a stop/target order's bounded completion wait expires
    // with the order confirmed only partially filled (see the 2026-07-17 UBER incident — a
    // partial execDetails event was previously mistaken for a full close). Must NOT close
    // trade_metrics/open_positions/TradeGuard as if the full position closed — only the
    // confirmed-sold quantity is corrected, and the position stays open and tracked for
    // whatever genuinely remains.
    private async Task HandleBrokerPartialFillAsync(BrokerPartialFillEvent partialFill, CancellationToken ct)
    {
        var trade = _guard.GetOpenTrades()
            .FirstOrDefault(t => t.OrderId == partialFill.EntryOrderId);

        if (trade is null)
        {
            _logger.LogWarning(
                "Broker partial fill received for OrderId {OrderId} but no matching open position found.",
                partialFill.EntryOrderId);
            return;
        }

        _guard.UpdatePositionQuantity(trade.OrderId, partialFill.RemainingQty);

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOpenPositionRepository>();
            await repo.UpdateQuantityAsync(trade.OrderId, partialFill.RemainingQty, ct);
        }

        _logger.LogError(
            "PARTIAL {Outcome} ONLY — {Symbol} confirmed {SoldQty} of {OriginalQty} sold @ " +
            "${Price:F2}. {RemainingQty} contracts remain open at IBKR. {ProtectionNote}",
            partialFill.Outcome, trade.Symbol, partialFill.ConfirmedSoldQty, trade.Quantity,
            partialFill.FillPrice, partialFill.RemainingQty, partialFill.ProtectionNote);

        await _discord.NotifyCriticalAsync(
            $"⚠️ Partial {partialFill.Outcome} Only — {trade.Symbol}",
            $"**{trade.Symbol}** {partialFill.Outcome} order confirmed only " +
            $"{partialFill.ConfirmedSoldQty} of {trade.Quantity} sold @ ${partialFill.FillPrice:F2} " +
            $"within the fill-confirmation window. {partialFill.RemainingQty} contracts remain open " +
            $"at IBKR — position quantity has been corrected in Vela, trade_metrics was NOT closed.\n" +
            $"{partialFill.ProtectionNote}",
            ct);
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