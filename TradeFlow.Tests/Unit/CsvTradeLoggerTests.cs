using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeFlow.Worker.Models;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Unit;

/// <summary>
/// Tests for CsvTradeLogger. Uses a temp directory per test so each test
/// gets a clean slate and files don't leak between runs.
/// </summary>
public class CsvTradeLoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CsvTradeLogger _logger;

    public CsvTradeLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tradeflow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trades:Directory"] = _tempDir
            })
            .Build();

        _logger = new CsvTradeLogger(config, NullLogger<CsvTradeLogger>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- Header tests --

    [Fact]
    public void Constructor_CreatesOptionsFileWithHeader()
    {
        var path = Path.Combine(_tempDir, "options_trades.csv");
        File.Exists(path).Should().BeTrue();

        var header = File.ReadAllLines(path)[0];
        header.Should().Contain("Symbol");
        header.Should().Contain("Entry Price");
        header.Should().Contain("Status");
        header.Should().Contain("P&L");
    }

    [Fact]
    public void Constructor_CreatesStocksFileWithHeader()
    {
        var path = Path.Combine(_tempDir, "stocks_trades.csv");
        File.Exists(path).Should().BeTrue();

        var header = File.ReadAllLines(path)[0];
        header.Should().Contain("Symbol");
        header.Should().Contain("Shares");
        header.Should().Contain("Entry Price");
        header.Should().Contain("Status");
    }

    // -- OpenTrade tests --

    [Fact]
    public async Task OpenTradeAsync_WritesOptionsRowWithStatusOpen()
    {
        var trade = BuildOpenOptionsTrade();
        await _logger.OpenTradeAsync(trade);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        // Header + trade row + summary block
        lines.Should().HaveCountGreaterThan(1);

        var tradeRow = lines.First(l =>
            l.Contains("TSLA") && !l.StartsWith(",,") && l != lines[0]);

        tradeRow.Should().Contain("TSLA");
        tradeRow.Should().Contain("4.95");
        tradeRow.Should().Contain("Open");
    }

    [Fact]
    public async Task OpenTradeAsync_WritesStocksRowWithStatusOpen()
    {
        var trade = BuildOpenStockTrade();
        await _logger.OpenTradeAsync(trade);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_tempDir, "stocks_trades.csv"));

        var tradeRow = lines.First(l =>
            l.Contains("AAPL") && !l.StartsWith(",,") && l != lines[0]);

        tradeRow.Should().Contain("AAPL");
        tradeRow.Should().Contain("165.33");
        tradeRow.Should().Contain("Open");
    }

    [Fact]
    public async Task OpenTradeAsync_AppendsSummaryBlock()
    {
        var trade = BuildOpenOptionsTrade();
        await _logger.OpenTradeAsync(trade);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        content.Should().Contain("SUMMARY");
        content.Should().Contain("Total Trades");
        content.Should().Contain("Open");
    }

    // -- CloseTrade tests --

    [Fact]
    public async Task CloseTradeAsync_UpdatesRowWithExitData()
    {
        var openTrade = BuildOpenOptionsTrade();
        await _logger.OpenTradeAsync(openTrade);

        var closedTrade = openTrade with
        {
            Status = TradeStatus.Closed,
            ExitPrice = 9.90m,
            ExitAmount = 1_980m,
            PnL = 990m,
            PnLPercent = 100m,
            Result = TradeOutcome.XtradesExit,
            ClosedAt = DateTimeOffset.UtcNow
        };

        await _logger.CloseTradeAsync(closedTrade);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        var closedRow = lines.First(l =>
            l.Contains("TSLA") && l.Contains("Closed") && !l.StartsWith(",,"));

        closedRow.Should().Contain("9.90");
        closedRow.Should().Contain("Closed");
        closedRow.Should().Contain("XtradesExit");
    }

    [Fact]
    public async Task CloseTradeAsync_UpdatesSummaryWithPnL()
    {
        var openTrade = BuildOpenOptionsTrade();
        await _logger.OpenTradeAsync(openTrade);

        var closedTrade = openTrade with
        {
            Status = TradeStatus.Closed,
            ExitPrice = 9.90m,
            ExitAmount = 1_980m,
            PnL = 990m,
            PnLPercent = 100m,
            Result = TradeOutcome.XtradesExit,
            ClosedAt = DateTimeOffset.UtcNow
        };

        await _logger.CloseTradeAsync(closedTrade);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        content.Should().Contain("Wins");
        content.Should().Contain("Total P&L");
        content.Should().Contain("+990");
    }

    [Fact]
    public async Task OpenTradeAsync_TwoTrades_BothWrittenToFile()
    {
        await _logger.OpenTradeAsync(BuildOpenOptionsTrade("TSLA", "TSLA260620C00450000"));
        await _logger.OpenTradeAsync(BuildOpenOptionsTrade("AAPL", "AAPL260620C00200000"));

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        content.Should().Contain("TSLA");
        content.Should().Contain("AAPL");
    }

    // -- Helpers --

    private static TradeRecord BuildOpenOptionsTrade(
        string symbol = "TSLA",
        string contract = "TSLA260620C00450000") =>
        new()
        {
            AlertId = Guid.NewGuid().ToString(),
            OrderId = Guid.NewGuid().ToString(),
            StopOrderId = "STOP-001",
            TargetOrderId = "TGT-001",
            UserName = string.Empty,
            XScore = 0m,
            Symbol = symbol,
            TradeType = TradeType.Options,
            OptionsContract = contract,
            Direction = "call",
            Strike = 450m,
            Expiration = "2026-06-20T00:00:00",
            Quantity = 2,
            EntryPrice = 4.95m,
            EntryAmount = 990m,
            StopPrice = 2.48m,
            TargetPrice = 14.85m,
            Status = TradeStatus.Open,
            Result = TradeOutcome.Open,
            OpenedAt = DateTimeOffset.UtcNow,
        };

    private static TradeRecord BuildOpenStockTrade() =>
        new()
        {
            AlertId = Guid.NewGuid().ToString(),
            OrderId = Guid.NewGuid().ToString(),
            StopOrderId = null,
            TargetOrderId = null,
            UserName = string.Empty,
            XScore = 0m,
            Symbol = "AAPL",
            TradeType = TradeType.Stock,
            OptionsContract = null,
            Direction = null,
            Strike = null,
            Expiration = null,
            Quantity = 18,
            EntryPrice = 165.33m,
            EntryAmount = 2_975.94m,
            StopPrice = 140.53m,
            TargetPrice = 214.93m,
            Status = TradeStatus.Open,
            Result = TradeOutcome.Open,
            OpenedAt = DateTimeOffset.UtcNow,
        };
}
