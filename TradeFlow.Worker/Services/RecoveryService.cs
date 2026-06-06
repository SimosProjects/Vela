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

        if (trade.TradeType == TradeType.Options && IsExpired(trade))
        {
            _logger.LogWarning(
                "Recovery: {Symbol} contract expired on {Expiration}. Closing at $0.",
                trade.Symbol, trade.Expiration);

            await CloseAndLogAsync(trade, 0m, TradeOutcome.Expired, ct);
            return;
        }

        var (ibkrPrice, ibkrQty) = await _broker.GetCurrentPositionPriceAsync(trade, ct);

        if (ibkrPrice <= 0 || ibkrQty <= 0)
        {
            _logger.LogWarning(
                "Recovery: {Symbol} not found in IBKR positions (price={Price} qty={Qty}). " +
                "May have been closed while offline or is a ghost short — skipping.",
                trade.Symbol, ibkrPrice, ibkrQty);
            return;
        }

        if (ibkrQty != trade.Quantity)
        {
            _logger.LogWarning(
                "Recovery: {Symbol} qty mismatch — CSV has {CsvQty} but IBKR reports {IbkrQty}. " +
                "Using IBKR qty.",
                trade.Symbol, trade.Quantity, ibkrQty);
            trade = trade with { Quantity = ibkrQty };
        }

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

        _logger.LogInformation(
            "Recovery: {Symbol} is healthy at ${Price:F2} x{Qty}. Re-registering in TradeGuard.",
            trade.Symbol, ibkrPrice, ibkrQty);

        ReRegisterTrade(trade);
    }

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
            TargetPrice:           trade.TargetPrice,
            TrailPercent:          trade.TradeType == TradeType.Options ? 50.0 : 15.0,
            XScore:                trade.XScore,
            DiscordRank:           trade.DiscordRank);

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
                // Options column layout (0-indexed):
                // 0  Date Opened        1  Time Opened
                // 2  Date Closed        3  Time Closed
                // 4  Symbol             5  Contract
                // 6  Direction          7  Strike
                // 8  Expiration         9  Contracts
                // 10 Entry Price        11 Entry Amount
                // 12 Entry Latency      13 Entry Slippage %
                // 14 Exit Price         15 Exit Amount
                // 16 Exit Latency       17 Exit Slippage %
                // 18 Status             19 Result
                // 20 UserName           21 XScore
                // 22 DiscordRank        23 P&L
                // 24 P&L %

                if (cols.Length < 23) return null;

                if (!Enum.TryParse<TradeStatus>(cols[18], out var status) ||
                    status != TradeStatus.Open)
                    return null;

                decimal.TryParse(cols[10], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var entryPrice);
                decimal.TryParse(cols[11], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var entryAmount);
                decimal.TryParse(cols[21], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var xScore);

                var discordRank = cols.Length > 22
                    ? cols[22].Trim()
                    : string.Empty;

                return new TradeRecord
                {
                    AlertId         = string.Empty,
                    OrderId         = string.Empty,
                    StopOrderId     = null,
                    TargetOrderId   = null,
                    UserName        = cols[20],
                    XScore          = xScore,
                    DiscordRank     = string.IsNullOrEmpty(discordRank) ? null : discordRank,
                    Symbol          = cols[4],
                    TradeType       = TradeType.Options,
                    OptionsContract = cols[5],
                    Direction       = cols[6],
                    Strike          = decimal.TryParse(cols[7], out var strike) ? strike : null,
                    Expiration      = cols[8],
                    Quantity        = int.TryParse(cols[9], out var qty) ? qty : 0,
                    EntryPrice      = entryPrice,
                    EntryAmount     = entryAmount,
                    StopPrice       = entryPrice * 0.50m,
                    TargetPrice     = entryPrice * 3.00m,
                    OpenedAt        = DateTimeOffset.TryParse(
                                         $"{cols[0]} {cols[1]}",
                                         out var opened) ? opened : DateTimeOffset.UtcNow,
                    Status          = TradeStatus.Open,
                };
            }
            else
            {
                // Stocks column layout (0-indexed):
                // 0  Date Opened        1  Time Opened
                // 2  Date Closed        3  Time Closed
                // 4  Symbol             5  Shares
                // 6  Entry Price        7  Entry Amount
                // 8  Entry Latency      9  Entry Slippage %
                // 10 Exit Price         11 Exit Amount
                // 12 Exit Latency       13 Exit Slippage %
                // 14 Status             15 Result
                // 16 UserName           17 XScore
                // 18 DiscordRank        19 P&L
                // 20 P&L %

                if (cols.Length < 19) return null;

                if (!Enum.TryParse<TradeStatus>(cols[14], out var status) ||
                    status != TradeStatus.Open)
                    return null;

                var entryPrice = decimal.TryParse(cols[6], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var ep) ? ep : 0m;
                decimal.TryParse(cols[7], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var entryAmount);
                decimal.TryParse(cols[17], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var xScore);

                var discordRank = cols.Length > 18
                    ? cols[18].Trim()
                    : string.Empty;

                return new TradeRecord
                {
                    AlertId         = string.Empty,
                    OrderId         = string.Empty,
                    StopOrderId     = null,
                    TargetOrderId   = null,
                    UserName        = cols[16],
                    XScore          = xScore,
                    DiscordRank     = string.IsNullOrEmpty(discordRank) ? null : discordRank,
                    Symbol          = cols[4],
                    TradeType       = TradeType.Stock,
                    OptionsContract = null,
                    Direction       = null,
                    Strike          = null,
                    Expiration      = null,
                    Quantity        = int.TryParse(cols[5], out var qty) ? qty : 0,
                    EntryPrice      = entryPrice,
                    EntryAmount     = entryAmount,
                    StopPrice       = entryPrice * 0.85m,
                    TargetPrice     = entryPrice * 2.00m,
                    OpenedAt        = DateTimeOffset.TryParse(
                                         $"{cols[0]} {cols[1]}",
                                         out var opened) ? opened : DateTimeOffset.UtcNow,
                    Status          = TradeStatus.Open,
                };
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsExpired(TradeRecord trade)
    {
        if (trade.Expiration is null) return false;

        if (!DateTimeOffset.TryParseExact(
                trade.Expiration,
                "MMM dd yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiration))
            return false;

        var todayEt = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;

        return expiration.Date < todayEt;
    }
}