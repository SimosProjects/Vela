using IBApi;

namespace TradeFlow.Worker.Services;

/// <summary>
/// Receives all callbacks from IB Gateway. Only implements what TradeFlow needs,
/// all others are no-ops required by the EWrapper interface contract.
/// </summary>
public class IbkrEWrapper : EWrapper
{
    private readonly ILogger<IbkrEWrapper> _logger;

    // Order status callbacks keyed by orderId
    private readonly Dictionary<int, TaskCompletionSource<OrderState>> _orderCallbacks = new();

    // Account summary callbacks keyed by reqId
    private readonly Dictionary<int, TaskCompletionSource<string>> _accountCallbacks = new();

    // Position callbacks keyed by symbol match key
    private readonly Dictionary<string, TaskCompletionSource<decimal>> _positionCallbacks = new();

    private Action? _onConnectionClosed;

    private readonly Lock _lock = new();

    // Tracks the next valid order ID as reported by Gateway on connect and after each order.
    // Used by IbkrBrokerService to avoid ID collisions across Worker restarts.
    private int _nextValidOrderId = 1;
    public int NextValidOrderId => _nextValidOrderId;

    public IbkrEWrapper(ILogger<IbkrEWrapper> logger)
    {
        _logger = logger;
    }

    // Notifies awaiting PlaceOrderAsync calls when an order is confirmed filled.
    public void orderStatus(int orderId, string status, double filled, double remaining,
        double avgFillPrice, int permId, int parentId, double lastFillPrice,
        int clientId, string whyHeld, double mktCapPrice)
    {
        _logger.LogInformation(
            "IBKR OrderStatus — OrderId: {OrderId} Status: {Status} Filled: {Filled} AvgPrice: {Price}",
            orderId, status, filled, avgFillPrice);

        lock (_lock)
        {
            if (_orderCallbacks.TryGetValue(orderId, out var tcs))
            {
                // Only resolve on an actual fill, not PreSubmitted or Submitted.
                // This ensures OCA bracket orders are placed against a confirmed position.
                if (status is "Filled")
                {
                    tcs.TrySetResult(new OrderState { Status = status });
                    _orderCallbacks.Remove(orderId);
                }
            }
        }
    }

    // Handles both NetLiquidation and GrossPositionValue tags for account balance and exposure checks
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

    // Resolves position price callbacks used by PositionMonitorService
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
    /// Prevents orphaned TCS entries accumulating across PositionMonitorService poll cycles.
    /// </summary>
    public void UnregisterPositionCallback(string key)
    {
        lock (_lock) { _positionCallbacks.Remove(key); }
    }

    /// <summary>
    /// Registers a callback that resolves when IBKR confirms the order is filled.
    /// </summary>
    public TaskCompletionSource<OrderState> RegisterOrderCallback(int orderId)
    {
        var tcs = new TaskCompletionSource<OrderState>();
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
    /// Registers a callback that resolves when IBKR returns the account summary value for the given request ID.
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

    public void connectAck() =>
        _logger.LogInformation("IBKR connection acknowledged.");

    // Stores the next valid order ID from Gateway so IbkrBrokerService can seed
    // its counter on startup, preventing order ID collisions across Worker restarts.
    public void nextValidId(int orderId)
    {
        _nextValidOrderId = orderId;
        _logger.LogInformation("IBKR Next Valid OrderId: {OrderId}", orderId);
    }

    public void managedAccounts(string accountsList) =>
        _logger.LogInformation("IBKR Managed Accounts: {Accounts}", accountsList);

    public void connectionClosed()
    {
        _logger.LogWarning("IBKR connection closed.");
        _onConnectionClosed?.Invoke();
    }

    public void SetConnectionClosedCallback(Action onConnectionClosed) =>
        _onConnectionClosed = onConnectionClosed;

    public void error(Exception e) =>
        _logger.LogError(e, "IBKR Exception");

    public void error(string str) =>
        _logger.LogError("IBKR Error: {Message}", str);

    public void error(int id, int errorCode, string errorMsg)
    {
        // 2000-2999 are informational warnings not errors
        if (errorCode >= 2000 && errorCode < 3000)
            _logger.LogDebug("IBKR Info [{Code}]: {Message}", errorCode, errorMsg);
        else
            _logger.LogError("IBKR Error [{Code}] Id {Id}: {Message}", errorCode, id, errorMsg);
    }

    // Required interface stubs
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
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
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
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