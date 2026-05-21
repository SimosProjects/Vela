using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Background service that monitors open positions for broker-side closes.
/// Subscribes to the broker fill handler to detect when IBKR executes a
/// trailing stop or target order without a corresponding Xtrades exit alert.
/// Also polls open positions periodically as a fallback when market data is available.
/// </summary>
public class PositionMonitorService : BackgroundService
{
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly CsvTradeLogger _csv;
    private readonly DiscordNotificationService _discord;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PositionMonitorService> _logger;

    private const int PollIntervalSeconds = 60;

    public PositionMonitorService(
        TradeGuard guard,
        IBrokerService broker,
        CsvTradeLogger csv,
        DiscordNotificationService discord,
        IServiceScopeFactory scopeFactory,
        ILogger<PositionMonitorService> logger)
    {
        _guard = guard;
        _broker = broker;
        _csv = csv;
        _discord = discord;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Position monitor service started.");

        // Subscribe to broker-side fills so trailing stops and targets are detected immediately
        _broker.RegisterBrokerFillHandler(
            (entryOrderId, fillPrice, outcome) =>
                _ = HandleBrokerFillAsync(entryOrderId, fillPrice, outcome, stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);

            try
            {
                await CheckOpenPositionsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Position monitor cycle failed, will retry next interval.");
            }
        }

        _logger.LogInformation("Position monitor service stopped.");
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

    // Iterates all open positions and checks whether stop or target has been hit via polling.
    // This is a fallback for when market data is available — the primary detection is via execDetails.
    private async Task CheckOpenPositionsAsync(CancellationToken ct)
    {
        var openTrades = _guard.GetOpenTrades();

        if (openTrades.Count == 0)
            return;

        _logger.LogDebug(
            "Position monitor checking {Count} open position(s).", openTrades.Count);

        foreach (var trade in openTrades)
        {
            await CheckPositionAsync(trade, ct);
        }
    }

    private async Task CheckPositionAsync(TradeRecord trade, CancellationToken ct)
    {
        var currentPrice = await _broker.GetCurrentPositionPriceAsync(trade, ct);
        if (currentPrice <= 0)
            return;

        var outcome = EvaluatePosition(trade, currentPrice);
        if (outcome == TradeOutcome.Open)
            return;

        _logger.LogInformation(
            "Position monitor: {Symbol} hit {Outcome} at ${Price:F2}",
            trade.Symbol, outcome, currentPrice);

        await CloseAndRecordAsync(trade, currentPrice, outcome, ct);
    }

    private static TradeOutcome EvaluatePosition(TradeRecord trade, decimal currentPrice)
    {
        if (currentPrice <= trade.StopPrice)
            return TradeOutcome.StoppedOut;

        if (currentPrice >= trade.TargetPrice)
            return TradeOutcome.TargetHit;

        return TradeOutcome.Open;
    }

    private async Task CloseAndRecordAsync(
        TradeRecord trade,
        decimal fillPrice,
        TradeOutcome outcome,
        CancellationToken ct)
    {
        // For broker-side closes the position is already closed — we just need to record it.
        // For polling-detected closes we need to place a close order first.
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

        _logger.LogInformation(
            "POSITION CLOSED — {Symbol} × {Qty} @ ${Price:F2} | " +
            "P&L: {PnL:+$#,##0.00;-$#,##0.00} ({PnLPct:+0.00;-0.00}%) | Outcome: {Outcome}",
            closedTrade.Symbol, closedTrade.Quantity, fillPrice,
            closedTrade.PnL ?? 0, closedTrade.PnLPercent ?? 0, outcome);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);
    }
}