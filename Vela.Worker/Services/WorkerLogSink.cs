using Npgsql;
using NpgsqlTypes;
using Serilog.Core;
using Serilog.Events;
using System.Threading.Channels;

namespace Vela.Worker.Services;

/// <summary>
/// Serilog sink that writes INFO and above to the worker_logs PostgreSQL table.
/// Events are buffered in a bounded channel and flushed every 2 seconds.
/// On startup: creates the table if absent and deletes entries older than 7 days.
/// Uses a direct NpgsqlConnection, no EF dependency.
/// </summary>
public sealed class WorkerLogSink : ILogEventSink, IDisposable
{
    private const int BatchIntervalMs  = 500;
    private const int ChannelCapacity  = 500;

    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS worker_logs (
            id          bigserial    PRIMARY KEY,
            logged_at   timestamptz  NOT NULL,
            level       varchar(5)   NOT NULL,
            message     text         NOT NULL,
            exception   text
        );
        CREATE INDEX IF NOT EXISTS idx_worker_logs_logged_at ON worker_logs (logged_at);
        """;

    private const string RetentionSql =
        "DELETE FROM worker_logs WHERE logged_at < NOW() - INTERVAL '7 days'";

    private const string InsertSql =
        "INSERT INTO worker_logs (logged_at, level, message, exception) VALUES ($1, $2, $3, $4)";

    private readonly string _connectionString;
    private readonly Channel<WorkerLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    public WorkerLogSink(string connectionString)
    {
        _connectionString = connectionString;
        _channel = Channel.CreateBounded<WorkerLogEntry>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
        _writerTask = RunWriterAsync(_cts.Token);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;

        _channel.Writer.TryWrite(new WorkerLogEntry(
            logEvent.Timestamp,
            LevelLabel(logEvent.Level),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString()
        ));
    }

    // -- Helpers --

    private async Task RunWriterAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync();

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(BatchIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            await FlushBatchAsync(ct);
        }

        await FlushBatchAsync(CancellationToken.None);
    }

    private async Task EnsureSchemaAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using (var cmd = new NpgsqlCommand(CreateTableSql, conn))
                await cmd.ExecuteNonQueryAsync();

            await using (var cmd = new NpgsqlCommand(RetentionSql, conn))
                await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("[WorkerLogSink] Schema ready — worker_logs active.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkerLogSink] Schema setup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task FlushBatchAsync(CancellationToken ct)
    {
        var batch = new List<WorkerLogEntry>();
        while (_channel.Reader.TryRead(out var entry))
            batch.Add(entry);

        if (batch.Count == 0) return;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            foreach (var e in batch)
            {
                await using var cmd = new NpgsqlCommand(InsertSql, conn);
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value        = e.LoggedAt,
                    NpgsqlDbType = NpgsqlDbType.TimestampTz
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value        = e.Level,
                    NpgsqlDbType = NpgsqlDbType.Varchar
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value        = e.Message,
                    NpgsqlDbType = NpgsqlDbType.Text
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value        = (object?)e.Exception ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Text
                });
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkerLogSink] Flush failed ({batch.Count} entries dropped): " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string LevelLabel(LogEventLevel level) => level switch
    {
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning     => "WRN",
        LogEventLevel.Error       => "ERR",
        LogEventLevel.Fatal       => "ERR",
        _                         => "INF"
    };

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        _writerTask.Wait(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}

internal record WorkerLogEntry(DateTimeOffset LoggedAt, string Level, string Message, string? Exception);