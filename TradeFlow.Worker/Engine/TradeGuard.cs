using TradeFlow.Worker.Data;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Enforces trading safety rules before any order is placed.
// Rules checked in order:
//   1. Daily trade count < 10
//   2. Total open exposure + new trade cost <= account balance
//   3. No duplicate open position for same trader + contract
//   4. Averaging rules (one average per position, at 50% budget)
public class TradeGuard
{
    private const int MaxDailyTrades = 10;

    private readonly IBrokerService _broker;
    private readonly ILogger<TradeGuard> _logger;

    // In-memory open positions, keyed by match key (userName + contractSymbol or symbol)
    private readonly Dictionary<string, TradeRecord> _openTrades = new();
    private readonly Lock _lock = new();

    // Daily trade counter that resets at midnight ET
    private int     _dailyTradeCount = 0;
    private DateOnly _countDate      = DateOnly.MinValue;

    public TradeGuard(IBrokerService broker, ILogger<TradeGuard> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    /// <summary>
    /// Seeds TradeGuard in-memory state from persisted open positions on startup.
    /// Ensures exit alerts and position monitoring work correctly after a Worker restart.
    /// </summary>
    public void LoadFromDatabase(IEnumerable<OpenPosition> positions)
    {
        lock (_lock)
        {
            foreach (var p in positions)
            {
                var tradeType = Enum.TryParse<TradeType>(p.TradeType, out var tt)
                    ? tt : TradeType.Options;

                var record = new TradeRecord
                {
                    AlertId        = p.AlertId,
                    OrderId        = p.OrderId,
                    StopOrderId    = p.StopOrderId,
                    TargetOrderId  = p.TargetOrderId,
                    UserName       = p.UserName,
                    Symbol         = p.Symbol,
                    TradeType      = tradeType,
                    OptionsContract = p.OptionsContract,
                    Direction      = p.Direction,
                    Strike         = p.Strike,
                    Expiration     = p.Expiration,
                    Quantity       = p.Quantity,
                    EntryPrice     = p.EntryPrice,
                    EntryAmount    = p.EntryAmount,
                    StopPrice      = p.StopPrice,
                    TargetPrice    = p.TargetPrice,
                    OpenedAt       = p.OpenedAt,
                    IsAverage      = p.IsAverage,
                    HasAveraged    = p.HasAveraged,
                };

                var matchKey = BuildMatchKey(p.UserName, p.OptionsContract, p.Symbol);
                _openTrades[matchKey] = record;

                _logger.LogInformation(
                    "TradeGuard: restored position {Symbol} OrderId: {OrderId} from database",
                    p.Symbol, p.OrderId);
            }

            _logger.LogInformation(
                "TradeGuard: loaded {Count} open position(s) from database", _openTrades.Count);
        }
    }

    // Returns null if the order is allowed, or a reason string if blocked
    public async Task<string?> CheckAsync(TradeOrder order, CancellationToken ct = default)
    {
        ResetDailyCountIfNewDay();

        // 1. Daily limit
        if (_dailyTradeCount >= MaxDailyTrades)
            return $"Daily trade limit reached ({MaxDailyTrades}/day)";

        // 2. Duplicate position check
        var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);
        lock (_lock)
        {
            if (_openTrades.TryGetValue(matchKey, out var existing))
            {
                if (!order.IsAverage)
                    return $"Position already open for {order.Symbol} — use averaging";

                if (existing.HasAveraged)
                    return $"Already averaged into {order.Symbol} — only one average allowed";
            }
            else if (order.IsAverage)
            {
                return $"No open position found for {order.Symbol} — cannot average";
            }
        }

        // 3. Exposure check
        var balance   = await _broker.GetAccountBalanceAsync(ct);
        var openValue = await _broker.GetOpenPositionsValueAsync(ct);
        var available = balance - openValue;

        if (order.BudgetUsed > available)
        {
            return $"Insufficient available balance — need ${order.BudgetUsed:F2}, " +
                   $"available ${available:F2} (balance ${balance:F2}, open ${openValue:F2})";
        }

        return null;
    }

    // Called after a successful PlaceOrderAsync, registers the position in memory
    public void RegisterOpen(TradeOrder order, BrokerOrderResult result)
    {
        ResetDailyCountIfNewDay();

        var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);

        lock (_lock)
        {
            if (order.IsAverage && _openTrades.TryGetValue(matchKey, out var existing))
            {
                existing.HasAveraged = true;
                _logger.LogInformation(
                    "TradeGuard: averaged into {Symbol} — position updated", order.Symbol);
                return;
            }

            var record = new TradeRecord
            {
                AlertId         = order.AlertId,
                OrderId         = result.OrderId,
                StopOrderId     = result.StopOrderId,
                TargetOrderId   = result.TargetOrderId,
                UserName        = order.UserName,
                Symbol          = order.Symbol,
                TradeType       = order.TradeType,
                OptionsContract = order.OptionsContractSymbol,
                Direction       = order.Direction,
                Strike          = order.Strike,
                Expiration      = order.Expiration,
                Quantity        = result.FillQuantity,
                EntryPrice      = result.FillPrice,
                EntryAmount     = result.FillAmount,
                StopPrice       = order.StopPrice,
                TargetPrice     = order.TargetPrice,
                OpenedAt        = result.FilledAt,
                IsAverage       = order.IsAverage,
            };

            _openTrades[matchKey] = record;
            _dailyTradeCount++;

            _logger.LogInformation(
                "TradeGuard: position opened — {Symbol} | daily count: {Count}/{Max}",
                order.Symbol, _dailyTradeCount, MaxDailyTrades);
        }
    }

    // Called when a position closes, removes from open trades and populates exit data
    public TradeRecord? RegisterClose(
        string userName,
        string? contractSymbol,
        string symbol,
        decimal exitPrice,
        TradeOutcome outcome)
    {
        var matchKey = BuildMatchKey(userName, contractSymbol, symbol);

        lock (_lock)
        {
            if (!_openTrades.TryGetValue(matchKey, out var trade))
            {
                _logger.LogWarning(
                    "TradeGuard: no open position found for {Symbol} — cannot close", symbol);
                return null;
            }

            var multiplier = trade.TradeType == TradeType.Options ? 100m : 1m;
            var exitAmount = exitPrice * trade.Quantity * multiplier;
            var pnl        = exitAmount - trade.EntryAmount;
            var pnlPct     = pnl / trade.EntryAmount * 100;

            trade.ExitPrice  = exitPrice;
            trade.ExitAmount = exitAmount;
            trade.PnL        = pnl;
            trade.PnLPercent = pnlPct;
            trade.ClosedAt   = DateTimeOffset.UtcNow;
            trade.Status     = TradeStatus.Closed;
            trade.Result     = outcome;

            _openTrades.Remove(matchKey);
            return trade;
        }
    }

    // Returns the open TradeRecord for a given position, or null if not found
    public TradeRecord? FindOpenTrade(string userName, string? contractSymbol, string symbol)
    {
        var matchKey = BuildMatchKey(userName, contractSymbol, symbol);
        lock (_lock)
        {
            return _openTrades.GetValueOrDefault(matchKey);
        }
    }

    // Exposes all open trades for PositionMonitorService
    public IReadOnlyList<TradeRecord> GetOpenTrades()
    {
        lock (_lock)
        {
            return _openTrades.Values.ToList();
        }
    }

    /// <summary>Returns the number of trades placed today. Resets at midnight ET.</summary>
    public int GetDailyTradeCount()
    {
        ResetDailyCountIfNewDay();
        lock (_lock) { return _dailyTradeCount; }
    }

    // Options: userName + OCC symbol. Stocks: userName + ticker symbol
    private static string BuildMatchKey(string userName, string? contractSymbol, string symbol) =>
        contractSymbol is not null
            ? $"{userName}::{contractSymbol}"
            : $"{userName}::{symbol}";

    private void ResetDailyCountIfNewDay()
    {
        var todayEt = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;

        var today = DateOnly.FromDateTime(todayEt);

        lock (_lock)
        {
            if (today > _countDate)
            {
                _dailyTradeCount = 0;
                _countDate       = today;
                _logger.LogInformation("TradeGuard: daily trade count reset for {Date}", today);
            }
        }
    }
}