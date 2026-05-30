using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Engine;

// Enforces trading safety rules before any order is placed.
// Rules checked in order:
//   1. Daily exposure cap — today's open positions must not exceed MaxDailyExposurePct
//      of available balance (account balance minus carry-over positions from prior days)
//   2. Symbol already open (any instrument type) — prevents doubled exposure on same underlying
//   3. Total open exposure + new trade cost <= account balance
//   4. No duplicate open position for same trader + contract
//   5. Averaging rules (one average per position, at 50% budget)
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
    private readonly double _marginPct;

    // In-memory open positions, keyed by match key (userName + contractSymbol or symbol)
    private readonly Dictionary<string, TradeRecord> _openTrades = new();
    private readonly Lock _lock = new();

    // Cached IBKR account values — refreshed every 30s in the background
    private decimal _cachedBalance   = 0m;
    private decimal _cachedOpenValue = 0m;
    private readonly Lock _cacheLock = new();

    private CancellationTokenSource? _refreshCts;

    public TradeGuard(
        IBrokerService broker,
        IOptions<RiskEngineOptions> riskOptions,
        ILogger<TradeGuard> logger)
    {
        _broker              = broker;
        _logger              = logger;
        _maxDailyExposurePct = riskOptions.Value.MaxDailyExposurePct;
        _marginPct           = riskOptions.Value.MarginPct;
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
    /// Uses cached balance and open positions value — no IBKR round trips on the hot path.
    /// </summary>
    public Task<string?> CheckAsync(TradeOrder order, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // 1. Symbol-level check — block any new entry if the underlying symbol already
            // has an open position regardless of instrument type. Prevents holding TSLA stock
            // and a TSLA option simultaneously, which doubles exposure on one underlying.
            // Averaging is exempt since it is deliberately adding to an existing position.
            if (!order.IsAverage)
            {
                var symbolAlreadyOpen = _openTrades.Values
                    .Any(t => string.Equals(t.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase));

                if (symbolAlreadyOpen)
                    return Task.FromResult<string?>(
                        $"Position already open for {order.Symbol} — only one instrument per underlying allowed");
            }

            // 2. Contract-level duplicate and averaging rules
            var matchKey = BuildMatchKey(order.UserName, order.OptionsContractSymbol, order.Symbol);

            if (_openTrades.TryGetValue(matchKey, out var existing))
            {
                if (!order.IsAverage)
                    return Task.FromResult<string?>($"Position already open for {order.Symbol} — use averaging");

                if (existing.HasAveraged)
                    return Task.FromResult<string?>($"Already averaged into {order.Symbol} — only one average allowed");
            }
            else if (order.IsAverage)
            {
                return Task.FromResult<string?>($"No open position found for {order.Symbol} — cannot average");
            }
        }

        // 3. Exposure cap and balance check using cached values — no IBKR round trip
        decimal balance, openValue;
        lock (_cacheLock)
        {
            balance   = _cachedBalance;
            openValue = _cachedOpenValue;
        }

        if (balance > 0)
        {
            var todayOpenedValue   = GetTodayOpenedValue();
            var carryOverValue     = openValue - todayOpenedValue;
            var availableBalance   = balance - carryOverValue;
            var effectiveBalance   = availableBalance * (1m + (decimal)_marginPct);
            var maxDailyDeployment = effectiveBalance * (decimal)(_maxDailyExposurePct / 100.0);
            var deployableRemaining = maxDailyDeployment - todayOpenedValue;

            if (order.BudgetUsed > deployableRemaining)
            {
                return Task.FromResult<string?>(
                    $"Daily exposure cap reached — need ${order.BudgetUsed:F2}, " +
                    $"deployable ${deployableRemaining:F2} " +
                    $"(cap ${maxDailyDeployment:F2}, today open ${todayOpenedValue:F2})");
            }

            // Hard balance check — never deploy more than available cash
            var available = balance - openValue;
            if (order.BudgetUsed > available)
            {
                return Task.FromResult<string?>(
                    $"Insufficient available balance — need ${order.BudgetUsed:F2}, " +
                    $"available ${available:F2} (balance ${balance:F2}, open ${openValue:F2})");
            }
        }

        return Task.FromResult<string?>(null);
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
    /// Only today's opened positions count against the daily cap — carry-over positions
    /// from prior days reduce available balance but do not consume today's cap.
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
    // Used to separate today's deployment from carry-over positions when calculating the cap.
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

    // Refreshes cached balance and open positions value every 30 seconds.
    // Runs on a background thread so IBKR round trips never block trade execution.
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