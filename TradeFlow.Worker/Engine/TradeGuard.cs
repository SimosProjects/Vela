using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

/// <summary>
/// Returned by CheckAsync when an order is blocked. IsRoutine distinguishes
/// expected concurrent-path duplicates (polling + SignalR race) from genuine
/// policy blocks that the operator should act on.
/// </summary>
public record TradeGuardBlock(string Reason, bool IsRoutine = false);

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

    private readonly decimal _chopDailyLossLimit;
    private readonly MarketRegimeService? _regime;

    // In-memory open positions, keyed by match key (userName + contractSymbol or symbol)
    private readonly Dictionary<string, TradeRecord> _openTrades = new();

    // Tracks symbols reserved by CheckAsync but not yet confirmed by RegisterOpen.
    // Counted alongside _openTrades so concurrent ingestion paths cannot both pass
    // the per-symbol cap before either has called RegisterOpen.
    private readonly Dictionary<string, int> _pendingReservations = new();

    private readonly Lock _lock = new();

    // Cached IBKR account values, refreshed every 30s in the background
    private decimal _cachedBalance   = 0m;
    private decimal _cachedOpenValue = 0m;
    private readonly Lock _cacheLock = new();

    /// <summary>Last IBKR-fetched account balance. Refreshed every 30 seconds.</summary>
    public decimal CachedBalance   { get { lock (_cacheLock) return _cachedBalance;   } }

    /// <summary>Last IBKR-fetched open positions value. Refreshed every 30 seconds.</summary>
    public decimal CachedOpenValue { get { lock (_cacheLock) return _cachedOpenValue; } }

    private CancellationTokenSource? _refreshCts;

    public TradeGuard(
        IBrokerService broker,
        IOptions<RiskEngineOptions> riskOptions,
        ILogger<TradeGuard> logger,
        CsvTradeLogger? csv = null,
        MarketRegimeService? regime = null)
    {
        _broker                  = broker;
        _logger                  = logger;
        _maxDailyExposurePct     = riskOptions.Value.MaxDailyExposurePct;
        _stockDailyAllocationPct = riskOptions.Value.StockDailyAllocationPct;
        _marginPct               = riskOptions.Value.MarginPct;
        _maxPositionsPerSymbol   = riskOptions.Value.MaxPositionsPerSymbol;
        _dailyLossLimit          = riskOptions.Value.DailyLossLimit;
        _csv                     = csv;
        _chopDailyLossLimit      = riskOptions.Value.ChopDailyLossLimit;
        _regime                  = regime;
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
    /// Seeds TradeGuard in-memory state from persisted open positions on startup,
    /// restoring xScore from trade_metrics so it survives Worker restarts.
    /// Ensures exit alerts and position monitoring work correctly after a Worker restart.
    /// </summary>
    public void LoadFromDatabase(
        IEnumerable<OpenPosition> positions,
        IReadOnlyDictionary<string, decimal>? xScoresByOrderId = null)
    {
        lock (_lock)
        {
            foreach (var p in positions)
            {
                var tradeType = Enum.TryParse<TradeType>(p.TradeType, out var tt)
                    ? tt : TradeType.Options;

                decimal xScore = 0m;
                xScoresByOrderId?.TryGetValue(p.OrderId, out xScore);

                var record = new TradeRecord
                {
                    AlertId         = p.AlertId,
                    OrderId         = p.OrderId,
                    StopOrderId     = p.StopOrderId,
                    TargetOrderId   = p.TargetOrderId,
                    UserName        = p.UserName,
                    XScore          = xScore,
                    DiscordRank     = null,
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
                    "TradeGuard: restored position {Symbol} OrderId: {OrderId} XScore: {XScore} from database",
                    p.Symbol, p.OrderId, xScore);
            }

            _logger.LogInformation(
                "TradeGuard: loaded {Count} open position(s) from database", _openTrades.Count);
        }
    }

    /// <summary>
    /// Returns null if the order is allowed, or a TradeGuardBlock describing why it was blocked.
    /// IsRoutine on the block indicates whether this is an expected concurrent-path duplicate
    /// (polling + SignalR race) rather than a genuine policy violation worth operator attention.
    /// Checks position limits, exposure caps, and the daily loss limit in sequence.
    /// If all checks pass, atomically reserves a slot for this symbol so concurrent
    /// ingestion paths cannot both pass the per-symbol cap before either registers.
    /// The caller must call ReleaseReservation on any failure path before RegisterOpen.
    /// Uses cached balance and open positions value, no IBKR round trips on the hot path.
    /// </summary>
    public async Task<TradeGuardBlock?> CheckAsync(TradeOrder order, CancellationToken ct = default)
    {
        // -- Position checks under lock --
        TradeGuardBlock? positionBlock = null;
        lock (_lock)
        {
            if (!order.IsAverage)
            {
                // Count both confirmed open positions and pending reservations to prevent
                // concurrent paths from both passing the cap before either registers.
                var openCount = _openTrades.Values
                    .Count(t => string.Equals(
                        t.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase));
                _pendingReservations.TryGetValue(order.Symbol, out var pendingCount);
                var symbolCount = openCount + pendingCount;

                if (symbolCount >= _maxPositionsPerSymbol)
                {
                    // Distinguish a concurrent-path duplicate (pending reservation, no confirmed
                    // open position) from a genuine cap on confirmed holdings. Only the latter
                    // warrants operator attention.
                    var isRoutineDuplicate = openCount == 0 && pendingCount > 0;
                    positionBlock = isRoutineDuplicate
                        ? new TradeGuardBlock(
                            $"Entry in progress for {order.Symbol} on concurrent path — duplicate blocked",
                            IsRoutine: true)
                        : new TradeGuardBlock(
                            $"Max positions per symbol reached for {order.Symbol} ({symbolCount}/{_maxPositionsPerSymbol})");
                }
            }

            if (positionBlock is null)
            {
                var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);

                if (_openTrades.TryGetValue(matchKey, out var existing))
                {
                    if (!order.IsAverage)
                        positionBlock = new TradeGuardBlock(
                            $"Position already open for {order.Symbol} — use averaging");
                    else if (existing.HasAveraged)
                        positionBlock = new TradeGuardBlock(
                            $"Already averaged into {order.Symbol} — only one average allowed");
                }
                else if (order.IsAverage)
                {
                    positionBlock = new TradeGuardBlock(
                        $"No open position found for {order.Symbol} — cannot average");
                }
            }

            // Reserve the slot atomically while still under the lock.
            // Cleared by RegisterOpen on success or ReleaseReservation on any failure path.
            if (positionBlock is null && !order.IsAverage)
            {
                _pendingReservations.TryGetValue(order.Symbol, out var reservedCount);
                _pendingReservations[order.Symbol] = reservedCount + 1;
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
            {
                ReleaseReservation(order);
                return new TradeGuardBlock(
                    $"Daily exposure cap reached — need ${order.BudgetUsed:F2}, " +
                    $"deployable ${deployableRemaining:F2} " +
                    $"(cap ${maxDailyDeployment:F2}, today open ${todayOpenedValue:F2})");
            }

            if (order.TradeType == TradeType.Stock && _stockDailyAllocationPct > 0)
            {
                var todayStockValue    = GetTodayOpenedValueByType(TradeType.Stock);
                var maxStockDeployment = maxDailyDeployment * (decimal)(_stockDailyAllocationPct / 100.0);

                if (order.BudgetUsed + todayStockValue > maxStockDeployment)
                {
                    ReleaseReservation(order);
                    return new TradeGuardBlock(
                        $"Stock daily allocation cap reached — need ${order.BudgetUsed:F2}, " +
                        $"stock deployable ${Math.Max(0, maxStockDeployment - todayStockValue):F2} " +
                        $"(stock cap ${maxStockDeployment:F2}, stock open today ${todayStockValue:F2})");
                }
            }

            var available = balance - openValue;
            if (order.BudgetUsed > available)
            {
                ReleaseReservation(order);
                return new TradeGuardBlock(
                    $"Insufficient available balance — need ${order.BudgetUsed:F2}, " +
                    $"available ${available:F2} (balance ${balance:F2}, open ${openValue:F2})");
            }
        }

        var effectiveLimit = _regime?.IsChoppy == true && _chopDailyLossLimit < 0
            ? _chopDailyLossLimit
            : _dailyLossLimit;

        if (effectiveLimit < 0 && _csv is not null)
        {
            var todayPnl = await _csv.GetTodayRealizedPnLAsync(ct);
            if (todayPnl <= effectiveLimit)
            {
                var regimeLabel = _regime?.IsChoppy == true ? "choppy" : "normal";
                _logger.LogWarning(
                    "Daily loss limit reached ({Regime} regime) — " +
                    "today's realized P&L ${PnL:F2} at or below limit ${Limit:F2}. " +
                    "No new entries will be accepted for the rest of the session.",
                    regimeLabel, todayPnl, effectiveLimit);

                ReleaseReservation(order);
                return new TradeGuardBlock(
                    $"Daily loss limit reached ({regimeLabel} regime) — " +
                    $"today's realized P&L ${todayPnl:F2} (limit ${effectiveLimit:F2})");
            }
        }

        return null;
    }

    /// <summary>
    /// Releases the slot reserved by CheckAsync when execution fails before RegisterOpen.
    /// Must be called on every failure path between a successful CheckAsync and RegisterOpen.
    /// No-op for averaging orders, which do not reserve slots.
    /// </summary>
    public void ReleaseReservation(TradeOrder order)
    {
        if (order.IsAverage) return;

        lock (_lock)
        {
            if (!_pendingReservations.TryGetValue(order.Symbol, out var count)) return;

            if (count <= 1)
                _pendingReservations.Remove(order.Symbol);
            else
                _pendingReservations[order.Symbol] = count - 1;
        }
    }

    /// <summary>
    /// Called after a successful PlaceOrderAsync to register the position in memory.
    /// Clears the reservation made during CheckAsync.
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

            // Clear the slot reserved during CheckAsync now that the position is confirmed.
            if (!order.IsAverage && _pendingReservations.TryGetValue(order.Symbol, out var reservedCount))
            {
                if (reservedCount <= 1)
                    _pendingReservations.Remove(order.Symbol);
                else
                    _pendingReservations[order.Symbol] = reservedCount - 1;
            }

            var record = new TradeRecord
            {
                AlertId         = order.AlertId,
                OrderId         = result.OrderId,
                StopOrderId     = result.StopOrderId,
                TargetOrderId   = result.TargetOrderId,
                UserName        = order.UserName,
                XScore          = order.XScore,
                DiscordRank     = order.DiscordRank,
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
    /// Removes a position from TradeGuard by order ID.
    /// Used by StartupReconciliationService when a DB position has no matching IBKR position.
    /// </summary>
    public void RemovePosition(string orderId)
    {
        lock (_lock)
        {
            var key = _openTrades
                .FirstOrDefault(kv => kv.Value.OrderId == orderId).Key;

            if (key is null)
            {
                _logger.LogWarning(
                    "TradeGuard: RemovePosition — no position found with OrderId {OrderId}",
                    orderId);
                return;
            }

            _openTrades.Remove(key);
            _logger.LogInformation(
                "TradeGuard: removed position OrderId {OrderId} from memory.", orderId);
        }
    }

    /// <summary>
    /// Updates the quantity of a position in TradeGuard by order ID.
    /// Used by StartupReconciliationService when IBKR reports a different quantity than the DB.
    /// </summary>
    public void UpdatePositionQuantity(string orderId, int newQuantity)
    {
        lock (_lock)
        {
            var trade = _openTrades.Values
                .FirstOrDefault(t => t.OrderId == orderId);

            if (trade is null)
            {
                _logger.LogWarning(
                    "TradeGuard: UpdatePositionQuantity — no position found with OrderId {OrderId}",
                    orderId);
                return;
            }

            trade.Quantity = newQuantity;
            _logger.LogInformation(
                "TradeGuard: updated position {Symbol} OrderId {OrderId} quantity to {Qty}.",
                trade.Symbol, orderId, newQuantity);
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