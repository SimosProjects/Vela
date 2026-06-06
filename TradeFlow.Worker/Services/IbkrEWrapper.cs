using IBApi;
using TradeFlow.Worker.Models;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Receives all callbacks from IB Gateway. Only implements what TradeFlow needs,
/// all others are no-ops required by the EWrapper interface contract.
/// </summary>
public class IbkrEWrapper : EWrapper
{
    private readonly ILogger<IbkrEWrapper> _logger;

    private readonly Dictionary<int, TaskCompletionSource<OrderFill>> _orderCallbacks = new();
    private readonly Dictionary<int, TaskCompletionSource<string>> _accountCallbacks = new();
    private readonly Dictionary<string, TaskCompletionSource<decimal>> _positionCallbacks = new();
    private readonly Dictionary<int, string> _rejectionReasons = new();
    private readonly Dictionary<int, Action<decimal>> _execDetailsCallbacks = new();
    private readonly Dictionary<int, TaskCompletionSource<decimal>> _execDetailsTcsCallbacks = new();

    // Market data streaming callbacks keyed by reqId, resolves with midpoint or LAST price
    private readonly Dictionary<int, TaskCompletionSource<decimal>> _marketDataCallbacks = new();
    private readonly Dictionary<int, decimal> _marketDataBids = new();
    private readonly Dictionary<int, decimal> _marketDataAsks = new();
    // Historical data callbacks keyed by reqId, accumulates bars until historicalDataEnd fires
    private readonly Dictionary<int, (List<HistoricalBar> Bars, TaskCompletionSource<List<HistoricalBar>> Tcs)> _historicalDataCallbacks = new();

    private Action? _onConnectionClosed;
    private readonly Lock _lock = new();

    private readonly TaskCompletionSource<int> _nextValidIdReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextValidOrderId = 1;
    public int NextValidOrderId => _nextValidOrderId;

    public IbkrEWrapper(ILogger<IbkrEWrapper> logger)
    {
        _logger = logger;
    }

    public void orderStatus(int orderId, string status, double filled, double remaining,
        double avgFillPrice, int permId, int parentId, double lastFillPrice,
        int clientId, string whyHeld, double mktCapPrice)
    {
        if (status is "Filled" or "Cancelled" or "Inactive")
        {
            _logger.LogInformation(
                "IBKR OrderStatus — OrderId: {OrderId} Status: {Status} Filled: {Filled} AvgPrice: {Price}",
                orderId, status, filled, avgFillPrice);
        }
        else
        {
            _logger.LogDebug(
                "IBKR OrderStatus — OrderId: {OrderId} Status: {Status} Filled: {Filled} AvgPrice: {Price}",
                orderId, status, filled, avgFillPrice);
        }

        lock (_lock)
        {
            if (_orderCallbacks.TryGetValue(orderId, out var tcs))
            {
                if (status is "Filled")
                {
                    tcs.TrySetResult(new OrderFill(status, (decimal)avgFillPrice));
                    _orderCallbacks.Remove(orderId);
                }
            }
        }
    }

    public void execDetails(int reqId, Contract contract, Execution execution)
    {
        _logger.LogInformation(
            "IBKR ExecDetails — OrderId: {OrderId} Symbol: {Symbol} Side: {Side} AvgPrice: {Price} Qty: {Qty}",
            execution.OrderId, contract.Symbol, execution.Side,
            execution.AvgPrice, execution.CumQty);

        lock (_lock)
        {
            // Fire action-based callback (used by stop/target order detection)
            if (_execDetailsCallbacks.TryGetValue(execution.OrderId, out var handler))
            {
                handler((decimal)execution.AvgPrice);
                _execDetailsCallbacks.Remove(execution.OrderId);
            }

            // Resolve TCS-based callback (used by close order timeout recovery)
            if (_execDetailsTcsCallbacks.TryGetValue(execution.OrderId, out var tcs))
            {
                tcs.TrySetResult((decimal)execution.AvgPrice);
                _execDetailsTcsCallbacks.Remove(execution.OrderId);
            }
        }
    }

    public void accountSummary(int reqId, string account, string tag, string value, string currency)
    {
        _logger.LogDebug("IBKR AccountSummary {Tag}: {Value} {Currency}", tag, value, currency);
        lock (_lock)
        {
            if (tag is "NetLiquidation" or "GrossPositionValue" &&
                _accountCallbacks.TryGetValue(reqId, out var tcs))
            {
                tcs.TrySetResult(value);
                _accountCallbacks.Remove(reqId);
            }
        }
    }

    public void position(string account, Contract contract, double pos, double avgCost)
    {
        var key = contract.SecType == "OPT"
            ? $"{contract.Symbol}::{contract.LocalSymbol}"
            : $"{contract.Symbol}::STK";

        _logger.LogDebug(
            "IBKR Position {Symbol} qty: {Pos} avgCost: {Cost}", contract.Symbol, pos, avgCost);

        lock (_lock)
        {
            if (_positionCallbacks.TryGetValue(key, out var tcs))
            {
                tcs.TrySetResult((decimal)avgCost);
                _positionCallbacks.Remove(key);
            }
        }
    }

    // Resolves market data callbacks using the best available price.
    // LAST (4) resolves immediately as the most accurate price.
    // BID (1) and ASK (2) are collected and resolved as midpoint once both arrive,
    // covering premarket and after-hours when no recent trade exists.
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (price <= 0)
            return;

        lock (_lock)
        {
            if (!_marketDataCallbacks.TryGetValue(tickerId, out var tcs))
                return;

            if (tcs.Task.IsCompleted)
                return;

            var px = (decimal)price;

            switch (field)
            {
                case 4: // LAST — most accurate, resolve immediately
                    tcs.TrySetResult(px);
                    _marketDataCallbacks.Remove(tickerId);
                    _marketDataBids.Remove(tickerId);
                    _marketDataAsks.Remove(tickerId);
                    break;

                case 1: // BID
                    _marketDataBids[tickerId] = px;
                    TryResolveMidpoint(tickerId, tcs);
                    break;

                case 2: // ASK
                    _marketDataAsks[tickerId] = px;
                    TryResolveMidpoint(tickerId, tcs);
                    break;
            }
        }
    }

    // Resolves with bid/ask midpoint once both sides are available.
    // Called under _lock — do not acquire _lock inside this method.
    private void TryResolveMidpoint(int tickerId, TaskCompletionSource<decimal> tcs)
    {
        if (!_marketDataBids.TryGetValue(tickerId, out var bid)) return;
        if (!_marketDataAsks.TryGetValue(tickerId, out var ask)) return;

        var mid = (bid + ask) / 2m;
        tcs.TrySetResult(mid);
        _marketDataCallbacks.Remove(tickerId);
        _marketDataBids.Remove(tickerId);
        _marketDataAsks.Remove(tickerId);
    }

    /// <summary>
    /// Registers a callback that resolves when IBKR returns market data for the given request ID.
    /// Used by IbkrBrokerService.GetCurrentMarketPriceAsync for pre-trade slippage checks.
    /// </summary>
    public TaskCompletionSource<decimal> RegisterMarketDataCallback(int reqId)
    {
        var tcs = new TaskCompletionSource<decimal>();
        lock (_lock) { _marketDataCallbacks[reqId] = tcs; }
        return tcs;
    }

    /// <summary>
    /// Removes a market data callback and its associated bid/ask state on timeout.
    /// </summary>
    public void UnregisterMarketDataCallback(int reqId)
    {
        lock (_lock)
        {
            _marketDataCallbacks.Remove(reqId);
            _marketDataBids.Remove(reqId);
            _marketDataAsks.Remove(reqId);
        }
    }

    /// <summary>
    /// Registers a callback that resolves when Gateway finishes delivering historical bars.
    /// </summary>
    public TaskCompletionSource<List<HistoricalBar>> RegisterHistoricalDataCallback(int reqId)
    {
        var tcs = new TaskCompletionSource<List<HistoricalBar>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _historicalDataCallbacks[reqId] = (new List<HistoricalBar>(), tcs);
        }
        return tcs;
    }

    /// <summary>
    /// Removes a historical data callback on timeout before all bars arrive.
    /// </summary>
    public void UnregisterHistoricalDataCallback(int reqId)
    {
        lock (_lock) { _historicalDataCallbacks.Remove(reqId); }
    }

    /// <summary>
    /// Registers a callback that fires when IBKR reports an execution for the given order ID.
    /// </summary>
    public void RegisterExecDetailsCallback(int orderId, Action<decimal> handler)
    {
        lock (_lock) { _execDetailsCallbacks[orderId] = handler; }
    }

    /// <summary>
    /// Removes an exec details callback — called when a position is closed by other means.
    /// </summary>
    public void UnregisterExecDetailsCallback(int orderId)
    {
        lock (_lock) { _execDetailsCallbacks.Remove(orderId); }
    }

    /// <summary>
    /// Registers an awaitable callback that resolves when IBKR reports an execution for the given order ID.
    /// Used by ClosePositionAsync to recover the actual fill price after a close order timeout.
    /// </summary>
    public TaskCompletionSource<decimal> RegisterExecDetailsTcsCallback(int orderId)
    {
        var tcs = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) { _execDetailsTcsCallbacks[orderId] = tcs; }
        return tcs;
    }

    /// <summary>
    /// Removes an awaitable exec details callback, called when no longer needed.
    /// </summary>
    public void UnregisterExecDetailsTcsCallback(int orderId)
    {
        lock (_lock) { _execDetailsTcsCallbacks.Remove(orderId); }
    }

    /// <summary>
    /// Registers a callback that resolves when IBKR returns position data for the given symbol key.
    /// </summary>
    public TaskCompletionSource<decimal> RegisterPositionCallback(string key)
    {
        var tcs = new TaskCompletionSource<decimal>();
        lock (_lock) { _positionCallbacks[key] = tcs; }
        return tcs;
    }

    /// <summary>
    /// Removes a position callback that timed out before the Gateway responded.
    /// </summary>
    public void UnregisterPositionCallback(string key)
    {
        lock (_lock) { _positionCallbacks.Remove(key); }
    }

    /// <summary>
    /// Registers a callback that resolves when IBKR confirms the order is filled.
    /// </summary>
    public TaskCompletionSource<OrderFill> RegisterOrderCallback(int orderId)
    {
        var tcs = new TaskCompletionSource<OrderFill>();
        lock (_lock) { _orderCallbacks[orderId] = tcs; }
        return tcs;
    }

    /// <summary>
    /// Removes an order callback that timed out before the fill was confirmed.
    /// </summary>
    public void UnregisterOrderCallback(int orderId)
    {
        lock (_lock) { _orderCallbacks.Remove(orderId); }
    }

    /// <summary>
    /// Registers a callback that resolves when IBKR returns the account summary value.
    /// </summary>
    public TaskCompletionSource<string> RegisterAccountCallback(int reqId)
    {
        var tcs = new TaskCompletionSource<string>();
        lock (_lock) { _accountCallbacks[reqId] = tcs; }
        return tcs;
    }

    /// <summary>
    /// Removes an account summary callback that timed out before Gateway responded.
    /// </summary>
    public void UnregisterAccountCallback(int reqId)
    {
        lock (_lock) { _accountCallbacks.Remove(reqId); }
    }

    /// <summary>
    /// Returns a task that completes when Gateway sends the first nextValidId callback.
    /// </summary>
    public Task<int> WaitForNextValidIdAsync() => _nextValidIdReady.Task;

    public void connectAck() =>
        _logger.LogInformation("IBKR connection acknowledged.");

    public void nextValidId(int orderId)
    {
        _nextValidOrderId = orderId;
        _nextValidIdReady.TrySetResult(orderId);
        _logger.LogInformation("IBKR Next Valid OrderId: {OrderId}", orderId);
    }

    public void managedAccounts(string accountsList) =>
        _logger.LogInformation("IBKR Managed Accounts: {Accounts}", accountsList);

    public void connectionClosed()
    {
        _logger.LogWarning("IBKR connection closed.");
        _onConnectionClosed?.Invoke();
    }

    /// <summary>Sets the callback invoked when IB Gateway drops the connection.</summary>
    public void SetConnectionClosedCallback(Action onConnectionClosed) =>
        _onConnectionClosed = onConnectionClosed;

    public void error(Exception e) =>
        _logger.LogError(e, "IBKR Exception");

    public void error(string str) =>
        _logger.LogError("IBKR Error: {Message}", str);

    public void error(int id, int errorCode, string errorMsg)
    {
        // 2000-2999 are IBKR system informational codes; 10349 (market data farm notice)
        // are benign and expected during normal operation.
        if (errorCode >= 2000 && errorCode < 3000 || errorCode is 10349)
        {
            _logger.LogDebug("IBKR Info [{Code}] Id {Id}: {Message}", errorCode, id, errorMsg);
        }
        else if (errorCode == 201)
        {
            // 201 = order rejected (commonly margin insufficient for OCA target orders).
            // Store the reason so PlaceOrderAsync can surface it for the entry order.
            // For OCA stop/target orders this fires after the entry succeeds, meaning
            // the position is open but may be missing stop or target protection.
            lock (_lock) { _rejectionReasons[id] = errorMsg; }
            _logger.LogWarning(
                "IBKR Order rejected [201] Id {Id} — order will not execute. " +
                "If this is a target or stop order, the position may be unprotected. Reason: {Message}",
                id, errorMsg);
        }
        else if (errorCode == 202)
        {
            var idx    = errorMsg.IndexOf("reason:", StringComparison.OrdinalIgnoreCase);
            var reason = idx >= 0 ? errorMsg[(idx + 7)..].Trim() : "";

            if (!string.IsNullOrEmpty(reason))
                _logger.LogWarning("IBKR Order cancelled [202] Id {Id}: {Message}", id, errorMsg);
            else
                _logger.LogDebug("IBKR Order cancelled [202] Id {Id}", id);
        }
        else if (errorCode == 404)
        {
            // 404 = order held while IBKR locates shares (expected for stock OCA orders).
            // Not a terminal error, IBKR will resume the order once shares are located.
            _logger.LogWarning(
                "IBKR Order held while locating [404] Id {Id} — {Message}", id, errorMsg);
        }
        else
        {
            _logger.LogError("IBKR Error [{Code}] Id {Id}: {Message}", errorCode, id, errorMsg);
        }
    }

    /// <summary>
    /// Returns the rejection reason for the given order ID if one was received, then removes it.
    /// </summary>
    public string? TakeRejectionReason(int orderId)
    {
        lock (_lock)
        {
            if (_rejectionReasons.TryGetValue(orderId, out var reason))
            {
                _rejectionReasons.Remove(orderId);
                return reason;
            }

            return null;
        }
    }

    public void historicalData(int reqId, Bar bar)
    {
        if (bar.Close <= 0) return;

        var date = DateOnly.TryParseExact(bar.Time[..8], "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : DateOnly.MinValue;

        if (date == DateOnly.MinValue) return;

        var hBar = new HistoricalBar(
            Date:   date,
            Open:   (decimal)bar.Open,
            High:   (decimal)bar.High,
            Low:    (decimal)bar.Low,
            Close:  (decimal)bar.Close,
            Volume: (long)bar.Volume);

        lock (_lock)
        {
            if (_historicalDataCallbacks.TryGetValue(reqId, out var entry))
                entry.Bars.Add(hBar);
        }
    }

    public void historicalDataEnd(int reqId, string start, string end)
    {
        _logger.LogDebug("IBKR historical data complete — reqId {ReqId}", reqId);
        lock (_lock)
        {
            if (_historicalDataCallbacks.TryGetValue(reqId, out var entry))
            {
                entry.Tcs.TrySetResult(entry.Bars);
                _historicalDataCallbacks.Remove(reqId);
            }
        }
    }

    // Required interface stubs
    public void tickSize(int tickerId, int field, int size) { }
    public void tickString(int tickerId, int field, string value) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void currentTime(long time) { }
    public void accountSummaryEnd(int reqId) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void positionEnd() { }
    public void realtimeBar(int reqId, long date, double open, double high, double low, double close, long volume, double WAP, int count) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void positionMulti(int requestId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int requestId) { }
    public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int requestId) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
}