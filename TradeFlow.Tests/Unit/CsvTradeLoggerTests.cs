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

    [Fact]
    public void Constructor_HeadersContainOrderIdColumn()
    {
        var optionsHeader = File.ReadAllLines(Path.Combine(_tempDir, "options_trades.csv"))[0];
        var stocksHeader  = File.ReadAllLines(Path.Combine(_tempDir, "stocks_trades.csv"))[0];

        optionsHeader.Should().Contain("OrderId");
        stocksHeader.Should().Contain("OrderId");
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

    [Fact]
    public async Task OpenTradeAsync_WritesOrderIdInRow()
    {
        var trade = BuildOpenOptionsTrade();
        await _logger.OpenTradeAsync(trade);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        content.Should().Contain(trade.OrderId);
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

    [Fact]
    public async Task CloseTradeAsync_MatchesByOrderId_ClosesCorrectRowWhenTwoOpenSameSymbol()
    {
        // Root-cause scenario: two AMD entries on the same day — fuzzy match would pick
        // the wrong row because symbol and direction are identical. OrderId match fixes this.
        var trade1 = BuildOpenOptionsTrade("AMD", "AMD260620C00150000");
        var trade2 = BuildOpenOptionsTrade("AMD", "AMD260620C00150000");
        // trade2 opened at a slightly higher price so the rows are distinguishable in the file
        trade2 = trade2 with { EntryPrice = 5.50m, EntryAmount = 1_100m };

        await _logger.OpenTradeAsync(trade1);
        await _logger.OpenTradeAsync(trade2);

        // Close only trade2 by OrderId
        var closedTrade2 = trade2 with
        {
            Status    = TradeStatus.Closed,
            ExitPrice = 9.90m,
            ExitAmount = 1_980m,
            PnL = 880m,
            PnLPercent = 80m,
            Result = TradeOutcome.XtradesExit,
            ClosedAt = DateTimeOffset.UtcNow
        };
        await _logger.CloseTradeAsync(closedTrade2);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_tempDir, "options_trades.csv"));
        var dataLines = lines
            .Skip(1)
            .Where(l => !l.StartsWith(",,") && !string.IsNullOrWhiteSpace(l))
            .ToList();

        var openRows   = dataLines.Where(l => l.Contains(",Open,")).ToList();
        var closedRows = dataLines.Where(l => l.Contains(",Closed,")).ToList();

        openRows.Should().HaveCount(1, "trade1 should still be open");
        closedRows.Should().HaveCount(1, "only trade2 should be closed");

        openRows[0].Should().Contain(trade1.OrderId);
        closedRows[0].Should().Contain(trade2.OrderId);
    }

    [Fact]
    public async Task CloseTradeAsync_FallsBackToFuzzyMatch_ForPreMigrationRowWithNoOrderId()
    {
        // Simulate a row written before OrderId column was added (empty OrderId).
        var trade = BuildOpenOptionsTrade() with { OrderId = "" };
        await _logger.OpenTradeAsync(trade);

        var closedTrade = trade with
        {
            Status = TradeStatus.Closed,
            ExitPrice = 7.50m,
            ExitAmount = 1_500m,
            PnL = 510m,
            PnLPercent = 51.5m,
            Result = TradeOutcome.XtradesExit,
            ClosedAt = DateTimeOffset.UtcNow
        };
        await _logger.CloseTradeAsync(closedTrade);

        var content = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "options_trades.csv"));

        // Fuzzy match should have found and closed the row
        content.Should().Contain("Closed");
        content.Should().Contain("7.50");
 
        // Check data rows only — the summary block contains ",,Open,0,Closed,1" which would
        // be a false positive if we check the full file content for ",Open,"
        var dataLines = (await File.ReadAllLinesAsync(Path.Combine(_tempDir, "options_trades.csv")))
            .Skip(1)
            .Where(l => !l.StartsWith(",,") && !string.IsNullOrWhiteSpace(l))
            .ToList();
        dataLines.Should().AllSatisfy(l => l.Should().NotContain(",Open,"),
            "the open row should have been rewritten to Closed");
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
            DiscordRank = null,
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
            DiscordRank = null,
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