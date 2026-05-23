using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Enforces trading safety rules before any order is placed.
// Rules checked in order:
//   1. Daily trade count < 10
//   2. Symbol already open (any instrument type) — prevents doubled exposure on same underlying
//   3. Total open exposure + new trade cost <= account balance
//   4. No duplicate open position for same trader + contract
//   5. Averaging rules (one average per position, at 50% budget)
public class TradeGuard
{
    private const int MaxDailyTrades = 10;

    private readonly IBrokerService _broker;
    private readonly ILogger<TradeGuard> _logger;

    // In-memory open positions, keyed by match key (userName + contractSymbol or symbol)
    private readonly Dictionary<string, TradeRecord> _openTrades = new();
    private readonly Lock _lock = new();

    // Daily trade counter that resets at midnight ET
    private int      _dailyTradeCount = 0;
    private DateOnly _countDate       = DateOnly.MinValue;

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
                    AlertId         = p.AlertId,
                    OrderId         = p.OrderId,
                    StopOrderId     = p.StopOrderId,
                    TargetOrderId   = p.TargetOrderId,
                    UserName        = p.UserName,
                    Symbol          = p.Symbol,
                    TradeType       = tradeType,
                    OptionsContract = p.OptionsContract,
                    Direction       = p.Direction,
                    Strike          = p.Strike,
                    Expiration      = p.Expiration,
                    Quantity        = p.Quantity,
                    EntryPrice      = p.EntryPrice,
                    EntryAmount     = p.EntryAmount,
                    StopPrice       = p.StopPrice,
                    TargetPrice     = p.TargetPrice,
                    OpenedAt        = p.OpenedAt,
                    IsAverage       = p.IsAverage,
                    HasAveraged     = p.HasAveraged,
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

    /// <summary>
    /// Seeds the daily trade counter from a persisted count on startup.
    /// Prevents the counter resetting to zero after a Worker restart within the same trading day.
    /// </summary>
    public void SeedDailyCount(int count)
    {
        var todayEt = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Date;

        var today = DateOnly.FromDateTime(todayEt);

        lock (_lock)
        {
            _dailyTradeCount = count;
            _countDate       = today;
            _logger.LogInformation(
                "TradeGuard: seeded daily trade count {Count} for {Date}", count, today);
        }
    }

    /// <summary>
    /// Returns null if the order is allowed, or a rejection reason string if blocked.
    /// </summary>
    public async Task<string?> CheckAsync(TradeOrder order, CancellationToken ct = default)
    {
        ResetDailyCountIfNewDay();

        // 1. Daily limit
        if (_dailyTradeCount >= MaxDailyTrades)
            return $"Daily trade limit reached ({MaxDailyTrades}/day)";

        lock (_lock)
        {
            // 2. Symbol-level check, block any new entry if the underlying symbol already
            // has an open position regardless of instrument type. Prevents holding TSLA stock
            // and a TSLA option simultaneously, which doubles exposure on one underlying.
            // Averaging is exempt since it is deliberately adding to an existing position.
            if (!order.IsAverage)
            {
                var symbolAlreadyOpen = _openTrades.Values
                    .Any(t => string.Equals(t.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase));

                if (symbolAlreadyOpen)
                    return $"Position already open for {order.Symbol} — only one instrument per underlying allowed";
            }

            // 3. Contract-level duplicate and averaging rules
            var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);

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

        // 4. Exposure check
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

    /// <summary>
    /// Called after a successful PlaceOrderAsync to register the position in memory
    /// and persist it to the database.
    /// </summary>
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

    /// <summary>
    /// Called when a position closes. Removes it from open trades and populates exit data.
    /// </summary>
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

    /// <summary>Returns the open TradeRecord for a given position, or null if not found.</summary>
    public TradeRecord? FindOpenTrade(string userName, string? contractSymbol, string symbol)
    {
        var matchKey = BuildMatchKey(userName, contractSymbol, symbol);
        lock (_lock)
        {
            return _openTrades.GetValueOrDefault(matchKey);
        }
    }

    /// <summary>Exposes all open trades for PositionMonitorService.</summary>
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

    // Options: userName + OCC symbol. Stocks: userName + ticker symbol.
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