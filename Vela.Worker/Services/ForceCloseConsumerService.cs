using Vela.Worker.Engine;
using Vela.Worker.Models;

namespace Vela.Worker.Services;

/// <summary>
/// Consumes dashboard force-close requests from the force_close_requests table.
/// Runs in the Worker process because the single IBKR session lives here, not in the Api.
/// Each cycle it claims Requested rows, resolves the live position from TradeGuard, closes it
/// through BrokerExecutionService, and writes the outcome back so the dashboard can tell a
/// clean close from one needing manual reconciliation.
/// </summary>
public class ForceCloseConsumerService : BackgroundService
{
    private const int PollIntervalSeconds = 2;
    private const int StartupDelayMs = 4000;
    private const int RequestStaleMinutes = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BrokerExecutionService _execution;
    private readonly TradeGuard _guard;
    private readonly DiscordNotificationService _discord;
    private readonly ILogger<ForceCloseConsumerService> _logger;

    public ForceCloseConsumerService(
        IServiceScopeFactory scopeFactory,
        BrokerExecutionService execution,
        TradeGuard guard,
        DiscordNotificationService discord,
        ILogger<ForceCloseConsumerService> logger)
    {
        _scopeFactory = scopeFactory;
        _execution = execution;
        _guard = guard;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the rest of Worker startup, Gateway connect, and reconciliation settle first.
        await Task.Delay(StartupDelayMs, ct);

        while (!ct.IsCancellationRequested)
        {
            await ProcessPendingRequestsAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);
        }
    }

    // -- Helpers --

    internal async Task ProcessPendingRequestsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VelaDbContext>();

            // AsTracking is required because the context default is NoTracking; without it the
            // status updates below would not be persisted on SaveChanges.
            var requests = await db.ForceCloseRequests
                .AsTracking()
                .Where(r => r.Status == "Requested")
                .OrderBy(r => r.RequestedAt)
                .ToListAsync(ct);

            if (requests.Count == 0) return;

            var staleCutoff = DateTimeOffset.UtcNow.AddMinutes(-RequestStaleMinutes);

            foreach (var request in requests)
            {
                request.ProcessedAt = DateTimeOffset.UtcNow;

                // A request that aged out is never sent to the broker. A stale command must not
                // close a position the operator may no longer want closed.
                if (request.RequestedAt < staleCutoff)
                {
                    request.Status = "Expired";
                    _logger.LogWarning(
                        "Force-close request {Id} for OrderId {OrderId} expired before processing — skipped.",
                        request.Id, request.OrderId);
                    continue;
                }

                var trade = _guard.GetOpenTrades()
                    .FirstOrDefault(t => t.OrderId == request.OrderId);

                if (trade is null)
                {
                    request.Status = ForceCloseOutcome.NotFound.ToString();
                    _logger.LogWarning(
                        "Force-close request {Id} — no tracked position for OrderId {OrderId}. " +
                        "It may be manual, already closed, or a ghost awaiting reconciliation.",
                        request.Id, request.OrderId);
                    await _discord.NotifyCriticalAsync(
                        "⚠️ Force Close — Position Not Found",
                        $"Dashboard requested a force close for order **{request.OrderId}** " +
                        "but no matching position is tracked by Vela. " +
                        "Check IBKR directly — it may be manual, already closed, or a ghost.",
                        ct);
                    continue;
                }

                var outcome = await _execution.ForceCloseAsync(trade, TradeOutcome.ForcedClose, ct);
                request.Status = outcome.ToString();

                _logger.LogInformation(
                    "Force-close request {Id} for {Symbol} (OrderId {OrderId}) — outcome {Outcome}.",
                    request.Id, trade.Symbol, request.OrderId, outcome);

                // Pending or Failed means the position may still be open at IBKR with its stop
                // already cancelled. PartialFill means it's confirmed still open (quantity
                // already corrected by ForceCloseAsync) and definitely unprotected. All three
                // need the critical channel for manual handling.
                if (outcome is ForceCloseOutcome.Pending or ForceCloseOutcome.Failed)
                {
                    await _discord.NotifyCriticalAsync(
                        $"⚠️ Force Close {outcome} — {trade.Symbol}",
                        $"Dashboard force close for **{trade.Symbol}** (order {request.OrderId}) " +
                        $"returned {outcome}. The position may still be open at IBKR. " +
                        "Manual reconciliation required.",
                        ct);
                }
                else if (outcome is ForceCloseOutcome.PartialFill)
                {
                    await _discord.NotifyCriticalAsync(
                        $"🚨 Force Close Partial Fill — {trade.Symbol} UNPROTECTED",
                        $"Dashboard force close for **{trade.Symbol}** (order {request.OrderId}) only " +
                        "partially filled within the fill-confirmation window. Position quantity has " +
                        "been corrected in Vela but the remainder is unprotected — the original stop " +
                        "was cancelled before this close attempt. Manual stop placement required immediately.",
                        ct);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force-close consumer cycle failed — will retry next interval.");
        }
    }
}