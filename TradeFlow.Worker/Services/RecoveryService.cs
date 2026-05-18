using System.Globalization;
using TradeFlow.Worker.Engine;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Runs once on startup before the main services begin processing alerts.
/// Reads open positions from the CSV trade log, reconciles them against IBKR,
/// checks Xtrades REST for any exit signals that arrived while the service was down,
/// and re-registers surviving positions in TradeGuard memory.
/// </summary>
public class RecoveryService : IHostedService
{
    private readonly TradeGuard _guard;
    private readonly IBrokerService _broker;
    private readonly CsvTradeLogger _csv;
    private readonly IAlertApiClient _alertClient;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<RecoveryService> _logger;
    private readonly IConfiguration _config;

    public RecoveryService(
        TradeGuard guard,
        IBrokerService broker,
        CsvTradeLogger csv,
        IAlertApiClient alertClient,
        DiscordNotificationService discord,
        ILogger<RecoveryService> logger,
        IConfiguration config)
    {
        _guard = guard;
        _broker = broker;
        _csv = csv;
        _alertClient = alertClient;
        _discord = discord;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Executes the recovery process on startup. Completes before the main
    /// alert pipeline begins accepting new alerts.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Recovery service starting. Checking for open positions.");

        try
        {
            await ReconcileOpenPositionsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Recovery failure should not prevent the service from starting
            _logger.LogError(ex, "Recovery service encountered an error. Continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Reads open positions from CSV, checks IBKR and Xtrades, re-registers survivors
    private async Task ReconcileOpenPositionsAsync(CancellationToken ct)
    {
        var openTrades = ReadOpenTradesFromCsv();

        if (openTrades.Count == 0)
        {
            _logger.LogInformation("Recovery: no open positions found in CSV.");
            return;
        }

        _logger.LogInformation(
            "Recovery: found {Count} open position(s) in CSV. Reconciling.",
            openTrades.Count);

        // Fetch recent Xtrades alerts once, used to check for missed exit signals
        var recentAlerts = await FetchRecentAlertsAsync(ct);

        foreach (var trade in openTrades)
        {
            await ReconcileTradeAsync(trade, recentAlerts, ct);
        }
    }

    private async Task ReconcileTradeAsync(
        TradeRecord trade,
        List<Alert> recentAlerts,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Recovery: reconciling {Symbol} opened at {OpenedAt}",
            trade.Symbol, trade.OpenedAt);

        // Step 1: Check if IBKR already closed the position (stop or target hit while down)
        var ibkrPrice = await _broker.GetCurrentPositionPriceAsync(trade, ct);
        if (ibkrPrice <= 0)
        {
            // Position not found in IBKR, likely already closed
            _logger.LogWarning(
                "Recovery: {Symbol} not found in IBKR positions. May have been closed while offline.",
                trade.Symbol);

            // Mark as unknown and skip rather than making assumptions
            return;
        }

        // Step 2: Check if price has already passed stop or target
        if (ibkrPrice <= trade.StopPrice)
        {
            _logger.LogInformation(
                "Recovery: {Symbol} current price ${Price:F2} is at or below stop ${Stop:F2}. Closing.",
                trade.Symbol, ibkrPrice, trade.StopPrice);

            await CloseAndLogAsync(trade, ibkrPrice, TradeOutcome.StoppedOut, ct);
            return;
        }

        if (ibkrPrice >= trade.TargetPrice)
        {
            _logger.LogInformation(
                "Recovery: {Symbol} current price ${Price:F2} is at or above target ${Target:F2}. Closing.",
                trade.Symbol, ibkrPrice, trade.TargetPrice);

            await CloseAndLogAsync(trade, ibkrPrice, TradeOutcome.TargetHit, ct);
            return;
        }

        // Step 3: Check Xtrades for a missed exit signal
        var missedExit = FindMissedExitSignal(trade, recentAlerts);
        if (missedExit is not null)
        {
            var exitPrice = missedExit.PriceAtExit ?? ibkrPrice;

            _logger.LogInformation(
                "Recovery: found missed exit signal for {Symbol} at ${Price:F2}. Closing.",
                trade.Symbol, exitPrice);

            await CloseAndLogAsync(trade, exitPrice, TradeOutcome.XtradesExit, ct);
            return;
        }

        // Step 4: Price is between stop and target, no missed exit signal
        // Re-register in TradeGuard so the position is monitored going forward
        _logger.LogInformation(
            "Recovery: {Symbol} is healthy at ${Price:F2}. Re-registering in TradeGuard.",
            trade.Symbol, ibkrPrice);

        ReRegisterTrade(trade);
    }

    // Looks for a side:stc or side:btc alert from the same trader on the same contract
    // that arrived after the position was opened
    private static Alert? FindMissedExitSignal(TradeRecord trade, List<Alert> recentAlerts)
    {
        return recentAlerts.FirstOrDefault(a =>
            (a.Side?.ToLower() is "stc" or "btc") &&
            string.Equals(a.OptionsContractSymbol, trade.OptionsContract,
                StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(a.TimeOfEntryAlert, out var alertTime) &&
            alertTime > trade.OpenedAt);
    }

    private async Task CloseAndLogAsync(
        TradeRecord trade,
        decimal exitPrice,
        TradeOutcome outcome,
        CancellationToken ct)
    {
        try
        {
            await _broker.ClosePositionAsync(trade, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Recovery: failed to close {Symbol} via broker.", trade.Symbol);
        }

        var closedTrade = _guard.RegisterClose(
            trade.AlertId,
            trade.OptionsContract,
            trade.Symbol,
            exitPrice,
            outcome);

        if (closedTrade is null) return;

        await _csv.CloseTradeAsync(closedTrade, ct);

        _logger.LogInformation(
            "Recovery: {Symbol} closed. Outcome: {Outcome} P&L: {PnL:+$#,##0.00;-$#,##0.00}",
            closedTrade.Symbol, outcome, closedTrade.PnL ?? 0);

        await _discord.NotifyPositionClosedAsync(closedTrade, ct);
    }

    // Re-registers an open trade back into TradeGuard after a restart
    private void ReRegisterTrade(TradeRecord trade)
    {
        var order = new TradeOrder(
            AlertId:               trade.AlertId,
            UserName:              string.Empty,
            Symbol:                trade.Symbol,
            TradeType:             trade.TradeType,
            OptionsContractSymbol: trade.OptionsContract,
            Direction:             trade.Direction,
            Strike:                trade.Strike,
            Expiration:            trade.Expiration,
            Quantity:              trade.Quantity,
            EstimatedEntryPrice:   trade.EntryPrice,
            BudgetUsed:            trade.EntryAmount,
            StopPrice:             trade.StopPrice,
            TargetPrice:           trade.TargetPrice);

        var result = new BrokerOrderResult(
            OrderId:       trade.OrderId,
            StopOrderId:   trade.StopOrderId,
            TargetOrderId: trade.TargetOrderId,
            FillPrice:     trade.EntryPrice,
            FillQuantity:  trade.Quantity,
            FillAmount:    trade.EntryAmount,
            Status:        OrderStatus.Filled,
            FilledAt:      trade.OpenedAt);

        _guard.RegisterOpen(order, result);

        _logger.LogInformation(
            "Recovery: {Symbol} re-registered in TradeGuard.", trade.Symbol);
    }

    // Fetches up to 100 recent alerts from Xtrades to check for missed exit signals
    private async Task<List<Alert>> FetchRecentAlertsAsync(CancellationToken ct)
    {
        try
        {
            var alerts = await _alertClient.GetAlertsAsync(ct, pageSize: 100);
            _logger.LogInformation(
                "Recovery: fetched {Count} recent alerts from Xtrades.", alerts.Count);
            return alerts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Recovery: could not fetch recent alerts from Xtrades. Skipping missed exit check.");
            return [];
        }
    }

    // Reads all open trade rows from both CSV files
    private List<TradeRecord> ReadOpenTradesFromCsv()
    {
        var tradesDir = _config["Trades:Directory"]
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "trades");

        tradesDir = Path.GetFullPath(tradesDir);

        var trades = new List<TradeRecord>();

        trades.AddRange(ReadOpenTradesFromFile(
            Path.Combine(tradesDir, "options_trades.csv"), TradeType.Options));

        trades.AddRange(ReadOpenTradesFromFile(
            Path.Combine(tradesDir, "stocks_trades.csv"), TradeType.Stock));

        return trades;
    }

    private List<TradeRecord> ReadOpenTradesFromFile(string path, TradeType tradeType)
    {
        if (!File.Exists(path))
            return [];

        var trades = new List<TradeRecord>();
        var lines = File.ReadAllLines(path);

        foreach (var line in lines.Skip(1))
        {
            // Skip summary lines and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(",,"))
                continue;

            var trade = ParseCsvRow(line, tradeType);
            if (trade is not null && trade.Status == TradeStatus.Open)
                trades.Add(trade);
        }

        return trades;
    }

    private static TradeRecord? ParseCsvRow(string line, TradeType tradeType)
    {
        try
        {
            var cols = line.Split(',');

            if (tradeType == TradeType.Options)
            {
                // Options CSV column order:
                // Date Opened, Time Opened, Date Closed, Time Closed,
                // Symbol, Contract, Direction, Strike, Expiration,
                // Contracts, Entry Price, Entry Amount,
                // Exit Price, Exit Amount, Status, Result, P&L, P&L %

                if (cols.Length < 18) return null;

                var status = Enum.TryParse<TradeStatus>(cols[14], out var s)
                    ? s : TradeStatus.Open;

                if (status != TradeStatus.Open) return null;

                decimal.TryParse(cols[10], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var optEp);
                decimal.TryParse(cols[11], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var optEa);

                return new TradeRecord
                {
                    AlertId         = string.Empty,
                    OrderId         = string.Empty,
                    StopOrderId     = null,
                    TargetOrderId   = null,
                    UserName        = string.Empty,
                    Symbol          = cols[4],
                    TradeType       = TradeType.Options,
                    OptionsContract = cols[5],
                    Direction       = cols[6],
                    Strike          = decimal.TryParse(cols[7], out var strike) ? strike : null,
                    Expiration      = cols[8],
                    Quantity        = int.TryParse(cols[9], out var qty) ? qty : 0,
                    EntryPrice      = optEp,
                    EntryAmount     = optEa,
                    StopPrice       = optEp * 0.50m,
                    TargetPrice     = optEp * 3.00m,
                    OpenedAt        = DateTimeOffset.TryParse(
                                         $"{cols[0]} {cols[1]}",
                                         out var optOpened) ? optOpened : DateTimeOffset.UtcNow,
                    Status          = TradeStatus.Open,
                };
            }
            else
            {
                // Stocks CSV column order:
                // Date Opened, Time Opened, Date Closed, Time Closed,
                // Symbol, Shares, Entry Price, Entry Amount,
                // Exit Price, Exit Amount, Status, Result, P&L, P&L %

                if (cols.Length < 14) return null;

                var status = Enum.TryParse<TradeStatus>(cols[10], out var s)
                    ? s : TradeStatus.Open;

                if (status != TradeStatus.Open) return null;

                var entryPrice = decimal.TryParse(cols[6], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var stkEp) ? stkEp : 0m;
                decimal.TryParse(cols[7], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var stkEa);

                return new TradeRecord
                {
                    AlertId         = string.Empty,
                    OrderId         = string.Empty,
                    StopOrderId     = null,
                    TargetOrderId   = null,
                    UserName        = string.Empty,
                    Symbol          = cols[4],
                    TradeType       = TradeType.Stock,
                    OptionsContract = null,
                    Direction       = null,
                    Strike          = null,
                    Expiration      = null,
                    Quantity        = int.TryParse(cols[5], out var stkQty) ? stkQty : 0,
                    EntryPrice      = entryPrice,
                    EntryAmount     = stkEa,
                    StopPrice       = entryPrice * 0.85m,
                    TargetPrice     = entryPrice * 1.30m,
                    OpenedAt        = DateTimeOffset.TryParse(
                                         $"{cols[0]} {cols[1]}",
                                         out var stkOpened) ? stkOpened : DateTimeOffset.UtcNow,
                    Status          = TradeStatus.Open,
                };
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

}