using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Enforces trading safety rules before any order is placed.
// Rules checked in order:
//   1. Symbol already open — prevents doubled exposure on same underlying
//   2. Contract duplicate and averaging rules
//   3. Daily exposure cap
//   4. Stock sub-cap
//   5. Hard balance check
//   6. Daily loss limit — blocks new entries when today's realized P&L falls below the threshold
//
// Account balance and open positions value are cached and refreshed every 30 seconds
// in the background to avoid blocking the trade execution path with IBKR round trips.
public class TradeGuard
{
    private const int CacheRefreshSeconds   = 30;
    private const int InitialRefreshDelayMs = 500;

    private static readonly TimeZoneInfo EasternTime =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly IBrokerService _broker;
    private readonly ILogger<TradeGuard> _logger;
    private readonly double _maxDailyExposurePct;
    private readonly double _stockDailyAllocationPct;
    private readonly double _marginPct;
    private readonly int _maxPositionsPerSymbol;
    private readonly decimal _dailyLossLimit;
    private readonly CsvTradeLogger? _csv;

    // In-memory open positions, keyed by match key (userName + contractSymbol or symbol)
    private readonly Dictionary<string, TradeRecord> _openTrades = new();
    private readonly Lock _lock = new();

    // Cached IBKR account values, refreshed every 30s in the background
    private decimal _cachedBalance   = 0m;
    private decimal _cachedOpenValue = 0m;
    private readonly Lock _cacheLock = new();

    private CancellationTokenSource? _refreshCts;

    public TradeGuard(
        IBrokerService broker,
        IOptions<RiskEngineOptions> riskOptions,
        ILogger<TradeGuard> logger,
        CsvTradeLogger? csv = null)
    {
        _broker                  = broker;
        _logger                  = logger;
        _maxDailyExposurePct     = riskOptions.Value.MaxDailyExposurePct;
        _stockDailyAllocationPct = riskOptions.Value.StockDailyAllocationPct;
        _marginPct               = riskOptions.Value.MarginPct;
        _maxPositionsPerSymbol   = riskOptions.Value.MaxPositionsPerSymbol;
        _dailyLossLimit          = riskOptions.Value.DailyLossLimit;
        _csv                     = csv;
    }

    /// <summary>
    /// Starts the background cache refresh loop.
    /// Called once from Program.cs after the IBKR connection is established.
    /// </summary>
    public void StartCacheRefresh(CancellationToken ct)
    {
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RefreshCacheLoopAsync(_refreshCts.Token), _refreshCts.Token);
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
    /// Returns null if the order is allowed, or a rejection reason string if blocked.
    /// Checks position limits, exposure caps, and the daily loss limit in sequence.
    /// Uses cached balance and open positions value, no IBKR round trips on the hot path.
    /// </summary>
    public async Task<string?> CheckAsync(TradeOrder order, CancellationToken ct = default)
    {
        // -- Position checks under lock --
        string? positionBlock = null;
        lock (_lock)
        {
            // Block any new entry if the underlying symbol already has the maximum number of open
            // positions regardless of instrument type. Averaging is exempt since it is deliberately
            // adding to an existing position.
            if (!order.IsAverage)
            {
                var symbolCount = _openTrades.Values
                    .Count(t => string.Equals(
                        t.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase));

                if (symbolCount >= _maxPositionsPerSymbol)
                    positionBlock =
                        $"Max positions per symbol reached for {order.Symbol} " +
                        $"({symbolCount}/{_maxPositionsPerSymbol})";
            }

            if (positionBlock is null)
            {
                var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);

                if (_openTrades.TryGetValue(matchKey, out var existing))
                {
                    if (!order.IsAverage)
                        positionBlock = $"Position already open for {order.Symbol} — use averaging";
                    else if (existing.HasAveraged)
                        positionBlock = $"Already averaged into {order.Symbol} — only one average allowed";
                }
                else if (order.IsAverage)
                {
                    positionBlock = $"No open position found for {order.Symbol} — cannot average";
                }
            }
        }

        if (positionBlock is not null)
            return positionBlock;

        // -- Exposure cap checks using cached values --
        decimal balance, openValue;
        lock (_cacheLock)
        {
            balance   = _cachedBalance;
            openValue = _cachedOpenValue;
        }

        if (balance > 0)
        {
            var todayOpenedValue    = GetTodayOpenedValue();
            var carryOverValue      = openValue - todayOpenedValue;
            var availableBalance    = balance - carryOverValue;
            var effectiveBalance    = availableBalance * (1m + (decimal)_marginPct);
            var maxDailyDeployment  = effectiveBalance * (decimal)(_maxDailyExposurePct / 100.0);
            var deployableRemaining = maxDailyDeployment - todayOpenedValue;

            if (order.BudgetUsed > deployableRemaining)
                return
                    $"Daily exposure cap reached — need ${order.BudgetUsed:F2}, " +
                    $"deployable ${deployableRemaining:F2} " +
                    $"(cap ${maxDailyDeployment:F2}, today open ${todayOpenedValue:F2})";

            // Stock sub-cap limits how much of the daily cap stocks can consume.
            if (order.TradeType == TradeType.Stock && _stockDailyAllocationPct > 0)
            {
                var todayStockValue    = GetTodayOpenedValueByType(TradeType.Stock);
                var maxStockDeployment = maxDailyDeployment * (decimal)(_stockDailyAllocationPct / 100.0);

                if (order.BudgetUsed + todayStockValue > maxStockDeployment)
                    return
                        $"Stock daily allocation cap reached — need ${order.BudgetUsed:F2}, " +
                        $"stock deployable ${Math.Max(0, maxStockDeployment - todayStockValue):F2} " +
                        $"(stock cap ${maxStockDeployment:F2}, stock open today ${todayStockValue:F2})";
            }

            // Hard balance check — never deploy more than available cash.
            var available = balance - openValue;
            if (order.BudgetUsed > available)
                return
                    $"Insufficient available balance — need ${order.BudgetUsed:F2}, " +
                    $"available ${available:F2} (balance ${balance:F2}, open ${openValue:F2})";
        }

        // Only active when DailyLossLimit is set to a negative value and CsvTradeLogger is injected.
        if (_dailyLossLimit < 0 && _csv is not null)
        {
            var todayPnl = await _csv.GetTodayRealizedPnLAsync(ct);
            if (todayPnl <= _dailyLossLimit)
            {
                _logger.LogWarning(
                    "Daily loss limit reached — today's realized P&L ${PnL:F2} at or below limit ${Limit:F2}. " +
                    "No new entries will be accepted for the rest of the session.",
                    todayPnl, _dailyLossLimit);

                return
                    $"Daily loss limit reached — today's realized P&L ${todayPnl:F2} " +
                    $"(limit ${_dailyLossLimit:F2})";
            }
        }

        return null;
    }

    /// <summary>
    /// Called after a successful PlaceOrderAsync to register the position in memory.
    /// </summary>
    public void RegisterOpen(TradeOrder order, BrokerOrderResult result)
    {
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

            _logger.LogInformation(
                "TradeGuard: position opened — {Symbol} | open positions: {Count}",
                order.Symbol, _openTrades.Count);
        }
    }

    /// <summary>
    /// Updates a position's quantity after a partial close.
    /// Called when part of a 1DTE position is sold at 3pm and the remainder rides overnight.
    /// Also clears the stop order ID since the trail stop is cancelled after partial close.
    /// </summary>
    public void UpdateAfterPartialClose(
        string userName,
        string? contractSymbol,
        string symbol,
        int newQuantity)
    {
        var matchKey = BuildMatchKey(userName, contractSymbol, symbol);

        lock (_lock)
        {
            if (!_openTrades.TryGetValue(matchKey, out var trade))
            {
                _logger.LogWarning(
                    "TradeGuard: no open position found for {Symbol} — cannot update after partial close",
                    symbol);
                return;
            }

            trade.Quantity    = newQuantity;
            trade.StopOrderId = null;

            _logger.LogInformation(
                "TradeGuard: partial close recorded — {Symbol} quantity updated to {Qty}, stop removed",
                symbol, newQuantity);
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

    /// <summary>
    /// Logs a snapshot of current exposure after a position closes.
    /// </summary>
    public void LogExposureUpdate()
    {
        decimal balance, openValue;
        lock (_cacheLock)
        {
            balance   = _cachedBalance;
            openValue = _cachedOpenValue;
        }

        var todayOpenedValue    = GetTodayOpenedValue();
        var carryOverValue      = openValue - todayOpenedValue;
        var availableBalance    = balance - carryOverValue;
        var effectiveBalance    = availableBalance * (1m + (decimal)_marginPct);
        var maxDailyDeployment  = effectiveBalance * (decimal)(_maxDailyExposurePct / 100.0);
        var deployableRemaining = maxDailyDeployment - todayOpenedValue;

        _logger.LogInformation(
            "Exposure update — balance ${Balance:F2} | carry-over ${CarryOver:F2} | " +
            "today open ${TodayOpen:F2} | cap ${Cap:F2} | available ${Available:F2}",
            balance, carryOverValue, todayOpenedValue, maxDailyDeployment, deployableRemaining);
    }

    /// <summary>
    /// Seeds the account cache directly. Used in unit tests to simulate IBKR balance responses
    /// without starting the background refresh loop.
    /// </summary>
    public void SetCacheForTesting(decimal balance, decimal openValue)
    {
        lock (_cacheLock)
        {
            _cachedBalance   = balance;
            _cachedOpenValue = openValue;
        }
    }

    // -- Helpers --

    // Returns the total entry amount of positions opened today in ET.
    private decimal GetTodayOpenedValue()
    {
        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        lock (_lock)
        {
            return _openTrades.Values
                .Where(t => DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(t.OpenedAt, EasternTime).DateTime) == todayEt)
                .Sum(t => t.EntryAmount);
        }
    }

    // Returns the total entry amount of positions of a specific type opened today in ET.
    private decimal GetTodayOpenedValueByType(TradeType tradeType)
    {
        var todayEt = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EasternTime).DateTime);

        lock (_lock)
        {
            return _openTrades.Values
                .Where(t =>
                    t.TradeType == tradeType &&
                    DateOnly.FromDateTime(
                        TimeZoneInfo.ConvertTime(t.OpenedAt, EasternTime).DateTime) == todayEt)
                .Sum(t => t.EntryAmount);
        }
    }

    // Refreshes cached balance and open positions value every 30 seconds.
    private async Task RefreshCacheLoopAsync(CancellationToken ct)
    {
        await Task.Delay(InitialRefreshDelayMs, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var balance   = await _broker.GetAccountBalanceAsync(ct);
                var openValue = await _broker.GetOpenPositionsValueAsync(ct);

                lock (_cacheLock)
                {
                    _cachedBalance   = balance;
                    _cachedOpenValue = openValue;
                }

                var todayOpenedValue    = GetTodayOpenedValue();
                var carryOverValue      = openValue - todayOpenedValue;
                var availableBalance    = balance - carryOverValue;
                var effectiveBalance    = availableBalance * (1m + (decimal)_marginPct);
                var maxDailyDeployment  = effectiveBalance * (decimal)(_maxDailyExposurePct / 100.0);
                var deployableRemaining = maxDailyDeployment - todayOpenedValue;

                _logger.LogDebug(
                    "TradeGuard cache refreshed — balance ${Balance:F2} | carry-over ${CarryOver:F2} | " +
                    "today open ${TodayOpen:F2} | cap ${Cap:F2} | available ${Available:F2}",
                    balance, carryOverValue, todayOpenedValue, maxDailyDeployment, deployableRemaining);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TradeGuard cache refresh failed — using stale values");
            }

            await Task.Delay(TimeSpan.FromSeconds(CacheRefreshSeconds), ct);
        }
    }

    private static string BuildMatchKey(string userName, string? contractSymbol, string symbol) =>
        contractSymbol is not null
            ? $"{userName}::{contractSymbol}"
            : $"{userName}::{symbol}";
}