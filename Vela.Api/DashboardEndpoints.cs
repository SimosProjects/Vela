using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Vela.Api.Models;

namespace Vela.Api;

/// <summary>
/// Registers all dashboard endpoints. Follows the same extension method
/// pattern as AlertEndpoints so Program.cs stays uniform.
/// </summary>
public static class DashboardEndpoints
{
    private static readonly TimeZoneInfo Et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly string[] ValidRegimeTiers = ["Bullish", "Choppy", "Bearish"];

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard");

        group.MapGet("/state", GetState).WithName("GetDashboardState")
             .WithSummary("Returns regime, account snapshot, and system health.");

        group.MapGet("/positions", GetPositions).WithName("GetOpenPositions")
             .WithSummary("Returns all currently open positions.");

        group.MapPost("/positions/{orderId}/close", ForceClosePosition).WithName("ForceClosePosition")
             .WithSummary("Queues a force-close for the given position. The Worker performs the broker close and writes the outcome back.");

        group.MapGet("/closed-today", GetClosedToday).WithName("GetClosedToday")
             .WithSummary("Returns trades closed today in Eastern Time.");

        group.MapPost("/pause", TogglePause).WithName("TogglePause")
             .WithSummary("Flips is_paused in system_state. Worker reads this at each poll cycle.");

        group.MapPost("/allow-override-blocks", ToggleAllowOverrideBlocks).WithName("ToggleAllowOverrideBlocks")
             .WithSummary("Toggles allow_override_blocks. When enabled, regime checkpoints do not reset block settings and values persist across restarts.");

        group.MapPost("/block-calls", ToggleBlockCalls).WithName("ToggleBlockCalls")
             .WithSummary("Flips block_calls_override in system_state. Applied by the Worker within 30 seconds.");

        group.MapPost("/block-high", ToggleBlockHigh).WithName("ToggleBlockHigh")
             .WithSummary("Flips block_high_override in system_state. Applied by the Worker within 30 seconds.");

        group.MapPost("/block-lotto", ToggleBlockLotto).WithName("ToggleBlockLotto")
             .WithSummary("Flips block_lotto_override in system_state. Applied by the Worker within 30 seconds.");

        group.MapPost("/regime", OverrideRegime).WithName("OverrideRegime")
             .WithSummary("Queues a manual regime override. Applied by the Worker within 30 seconds.");

        group.MapGet("/logs", GetLogs).WithName("GetWorkerLogs")
             .WithSummary("Returns today's Worker log entries, newest first, max 20.");

        group.MapGet("/traders", GetTraders).WithName("GetTraders")
             .WithSummary("Returns approved, restricted, and blocked traders from RiskEngineOptions.");
    }

    private static async Task<IResult> GetTraders(VelaDbContext db, CancellationToken ct)
    {
        var saved = await db.RiskConfigOverrides.FirstOrDefaultAsync(r => r.Id == 1, ct);
        if (saved is null) return Results.Ok(new TradersResponse([], [], []));

        try
        {
            using var doc = JsonDocument.Parse(saved.ConfigJson);
            var root      = doc.RootElement;

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var approved = root.TryGetProperty("approvedTraders", out var at)
                ? at.Deserialize<List<string>>(opts) ?? []
                : [];

            var allRestricted = root.TryGetProperty("restrictedTraders", out var rt)
                ? rt.Deserialize<List<RestrictedTraderDto>>(opts) ?? []
                : [];

            var restricted = allRestricted
                .Where(t => t.AllotmentPct > 0)
                .OrderBy(t => t.Name)
                .ToList();

            var blocked = allRestricted
                .Where(t => t.AllotmentPct <= 0)
                .OrderBy(t => t.Name)
                .Select(t => t.Name)
                .ToList();

            return Results.Ok(new TradersResponse(approved, restricted, blocked));
        }
        catch
        {
            return Results.Ok(new TradersResponse([], [], []));
        }
    }

    // -- Handlers --

    private static async Task<IResult> GetState(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);

        var todayStart  = TodayStartUtc();
        var dailyPnl    = await db.TradeMetrics
            .Where(t => t.ClosedAt >= todayStart)
            .SumAsync(t => t.PnL ?? 0m, ct);

        var lastAlertAt = await db.Alerts
            .MaxAsync(a => (DateTimeOffset?)a.IngestedAt, ct);

        var now        = DateTimeOffset.UtcNow;
        var marketOpen = IsMarketOpen();

        var workerRunning = state?.WorkerHeartbeat.HasValue == true
            && (now - state.WorkerHeartbeat!.Value).TotalSeconds < 60;

        var xtradesConnected = state?.SignalRConnected ?? false;

        var balance     = state?.AccountBalance ?? 0m;
        var openValue   = state?.OpenValue ?? 0m;
        var exposurePct = balance > 0 ? Math.Round(openValue / balance * 100, 1) : 0m;
        var sizingPct   = (int)Math.Round((state?.SizingMultiplier ?? 1.0m) * 100);

        var regime = new RegimeResponse(
            Tier:       state?.RegimeTier ?? "Unknown",
            SpyPrice:   state?.SpyPrice,
            Ma20:       state?.Ma20,
            Ma20pct:    CalcMaPct(state?.SpyPrice, state?.Ma20),
            Ma50:       state?.Ma50,
            Ma50pct:    CalcMaPct(state?.SpyPrice, state?.Ma50),
            Ma200:      state?.Ma200,
            Ma200pct:   CalcMaPct(state?.SpyPrice, state?.Ma200),
            Vix:        state?.Vix,
            VixDelta:   state?.VixDelta,
            SizingPct:  sizingPct,
            BlockCalls: state?.BlockCalls ?? false,
            ChopScore:  state?.ChopScore,
            Bias:       DetermineMarketBias(state?.RegimeTier, state?.Vix)
        );

        var account = new AccountResponse(
            Balance:     balance,
            OpenValue:   openValue,
            ExposurePct: exposurePct,
            DailyPnl:    dailyPnl,
            Deployable:  balance - openValue
        );

        var system = new SystemStatusResponse(
            IbkrConnected:       state?.IbkrConnected ?? false,
            XtradesConnected:    xtradesConnected,
            WorkerRunning:       workerRunning,
            MarketOpen:          marketOpen,
            IsPaused:            state?.IsPaused ?? false,
            AllowOverrideBlocks: state?.AllowOverrideBlocks ?? false,
            BlockCallsOverride:  state?.BlockCallsOverride ?? false,
            BlockHighOverride:   state?.BlockHighOverride ?? false,
            BlockLottoOverride:  state?.BlockLottoOverride ?? false,
            WorkerHeartbeat:     state?.WorkerHeartbeat,
            LastAlertAt:         lastAlertAt
        );

        return Results.Ok(new DashboardStateResponse(regime, account, system));
    }

    private static async Task<IResult> GetPositions(VelaDbContext db, CancellationToken ct)
    {
        // Materialise first, FormatContract is a C# method EF cannot translate to SQL
        var raw = await (
            from p in db.OpenPositions
            join a in db.Alerts on p.AlertId equals a.Id into alertJoin
            from a in alertJoin.DefaultIfEmpty()
            select new
            {
                p.OrderId,
                p.Symbol,
                p.OptionsContract,
                p.Direction,
                p.Quantity,
                p.EntryPrice,
                p.EntryAmount,
                p.StopPrice,
                p.TargetPrice,
                p.OpenedAt,
                p.UserName,
                XScore = a != null ? (double?)a.XScore : null,
            }
        ).ToListAsync(ct);

        var positions = raw.Select(x => new PositionResponse(
            Id:          x.OrderId,
            Contract:    FormatContract(x.OptionsContract, x.Symbol),
            Direction:   x.Direction,
            Quantity:    x.Quantity,
            EntryPrice:  x.EntryPrice,
            CostBasis:   x.EntryAmount,
            StopPrice:   x.StopPrice,
            TargetPrice: x.TargetPrice,
            OpenedAt:    x.OpenedAt,
            Trader:      x.UserName,
            XScore:      x.XScore,
            RiskTier:    DetermineRiskTier(x.OptionsContract)
        )).ToList();

        return Results.Ok(positions);
    }

    // Queues a dashboard-initiated force close. The Worker owns the single IBKR session, so the
    // close itself is performed there: ForceCloseConsumerService picks up the Requested row,
    // runs BrokerExecutionService.ForceCloseAsync, and writes the outcome back to the row.
    private static async Task<IResult> ForceClosePosition(
        string orderId,
        VelaDbContext db,
        CancellationToken ct)
    {
        var position = await db.OpenPositions
            .FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

        if (position is null)
            return Results.NotFound($"No open position found for order {orderId}.");

        // Manual positions are not tracked by TradeGuard and must be closed directly in IBKR.
        if (position.IsManual)
            return Results.BadRequest(
                "This position is manual and must be closed directly in IBKR.");

        var alreadyQueued = await db.ForceCloseRequests
            .AnyAsync(r => r.OrderId == orderId && r.Status == "Requested", ct);

        if (alreadyQueued)
            return Results.Conflict("A close is already queued for this position.");

        var request = new Vela.Worker.Data.ForceCloseRequest
        {
            OrderId = orderId,
            Status = "Requested",
            RequestedAt = DateTimeOffset.UtcNow,
        };

        db.ForceCloseRequests.Add(request);
        await db.SaveChangesAsync(ct);

        return Results.Accepted(
            $"/api/dashboard/positions/{orderId}/close",
            new { requestId = request.Id, status = request.Status });
    }

    private static async Task<IResult> GetClosedToday(VelaDbContext db, CancellationToken ct)
    {
        var todayStart = TodayStartUtc();

        // Materialise first, FormatContract is not SQL-translatable
        var raw = await db.TradeMetrics
            .Where(t => t.ClosedAt >= todayStart)
            .OrderByDescending(t => t.ClosedAt)
            .ToListAsync(ct);

        var trades = raw.Select(t => new ClosedTradeResponse(
            Id:          t.Id,
            Contract:    FormatContract(t.OptionsContract, t.Symbol),
            Direction:   t.Direction,
            Trader:      t.TraderName,
            XScore:      t.XScore.HasValue ? (double)t.XScore.Value : null,
            DiscordRank: t.DiscordRank,
            Quantity:    t.Quantity,
            EntryPrice:  t.FillPrice,
            ExitPrice:   t.ExitPrice,
            Pnl:         t.PnL,
            PnlPct:      t.PnLPct,
            Outcome:     t.Outcome,
            ClosedAt:    t.ClosedAt
        )).ToList();

        return Results.Ok(trades);
    }

    private static async Task<IResult> TogglePause(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        var newPaused = !state.IsPaused;

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPaused, newPaused), ct);

        return Results.Ok(new { isPaused = newPaused });
    }

    private static async Task<IResult> ToggleAllowOverrideBlocks(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        var newValue = !state.AllowOverrideBlocks;

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowOverrideBlocks, newValue), ct);

        return Results.Ok(new { allowOverrideBlocks = newValue });
    }

    private static async Task<IResult> ToggleBlockCalls(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        var newOverride = !state.BlockCallsOverride;

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BlockCallsOverride, newOverride), ct);

        return Results.Ok(new { blockCallsOverride = newOverride });
    }

    private static async Task<IResult> ToggleBlockHigh(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        var newOverride = !state.BlockHighOverride;

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BlockHighOverride, newOverride), ct);

        return Results.Ok(new { blockHighOverride = newOverride });
    }

    private static async Task<IResult> ToggleBlockLotto(VelaDbContext db, CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        var newOverride = !state.BlockLottoOverride;

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BlockLottoOverride, newOverride), ct);

        return Results.Ok(new { blockLottoOverride = newOverride });
    }

    private static async Task<IResult> OverrideRegime(
        RegimeOverrideRequest request,
        VelaDbContext db,
        CancellationToken ct)
    {
        if (!ValidRegimeTiers.Contains(request.Tier, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(
                $"Invalid regime tier '{request.Tier}'. Valid values: Bullish, Choppy, Bearish.");

        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
            return Results.NotFound("system_state row not yet initialised — is the Worker running?");

        // Normalise casing before writing so the Worker's Enum.TryParse succeeds
        var normalised = ValidRegimeTiers.First(t =>
            t.Equals(request.Tier, StringComparison.OrdinalIgnoreCase));

        await db.SystemState
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ForceRegime, normalised), ct);

        return Results.Ok(new
        {
            forceRegime = normalised,
            message     = $"Regime override to {normalised} queued — will apply within 30 seconds."
        });
    }

    private static async Task<IResult> GetLogs(VelaDbContext db, CancellationToken ct)
    {
        // TodayStartUtc() returns DateTimeOffset — use directly, not .UtcDateTime.
        // In Npgsql legacy timestamp mode, DateTime with Kind=Utc is rejected by timestamptz.
        var todayStart = TodayStartUtc();
        var logs       = new List<WorkerLogResponse>();

        try
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT logged_at, level, message FROM worker_logs " +
                "WHERE logged_at >= $1 ORDER BY logged_at DESC LIMIT 20",
                conn);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value        = todayStart,
                NpgsqlDbType = NpgsqlDbType.TimestampTz
            });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                logs.Add(new WorkerLogResponse(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    reader.GetString(1),
                    reader.GetString(2)));
        }
        catch
        {
            // worker_logs table absent until first Worker run — return empty list
        }

        return Results.Ok(logs);
    }

    // -- Helpers --

    private static DateTimeOffset TodayStartUtc()
    {
        var todayEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Et).Date;
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(todayEt, DateTimeKind.Unspecified), Et);
    }

    private static bool IsMarketOpen()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Et);
        return now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && now.TimeOfDay >= new TimeSpan(9, 30, 0)
            && now.TimeOfDay < new TimeSpan(16, 0, 0);
    }

    private static decimal CalcMaPct(decimal? price, decimal? ma) =>
        price > 0 && ma > 0
            ? Math.Round((price!.Value - ma!.Value) / ma.Value * 100, 2)
            : 0m;

    private static string DetermineMarketBias(string? tier, decimal? vix) => tier switch
    {
        "Bullish" => vix >= 20 ? "Cautiously Bullish" : "Bullish",
        "Choppy"  => "Choppy",
        "Bearish" => vix >= 25 ? "Bearish — Elevated Volatility" : "Bearish",
        _         => "Unknown"
    };

    // Parses an OCC-format options contract symbol into a human-readable display string.
    // Example: TSLA260620C00250000 → TSLA 250C 6/20
    // Falls back to the underlying symbol for stocks or unparseable contracts.
    private static string FormatContract(string? occ, string? symbol)
    {
        if (string.IsNullOrEmpty(occ) || occ.Length < 15)
            return symbol ?? string.Empty;

        try
        {
            // OCC format: ROOT(1-6) + YYMMDD(6) + C/P(1) + STRIKE(8 digits, x1000)
            var strike   = decimal.Parse(occ[^8..]) / 1000m;
            var type     = occ[^9..^8];
            var datePart = occ[^15..^9];

            var year  = 2000 + int.Parse(datePart[..2]);
            var month = int.Parse(datePart[2..4]);
            var day   = int.Parse(datePart[4..6]);
            var date  = new DateOnly(year, month, day);

            var strikeStr = strike % 1 == 0
                ? ((int)strike).ToString()
                : strike.ToString("0.##");

            return $"{symbol} {strikeStr}{type} {date:M/d}";
        }
        catch
        {
            return occ;
        }
    }

    // Derives risk tier from OCC contract expiration date using the same
    // logic as the risk engine. Falls back to Standard for stocks or parse failures.
    private static string DetermineRiskTier(string? optionsContract)
    {
        if (string.IsNullOrEmpty(optionsContract) || optionsContract.Length < 15)
            return "Standard";

        try
        {
            var datePart = optionsContract[^15..^9];
            var year     = 2000 + int.Parse(datePart[..2]);
            var month    = int.Parse(datePart[2..4]);
            var day      = int.Parse(datePart[4..6]);
            var expDate  = new DateOnly(year, month, day);

            var todayEt = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Et).DateTime);

            var daysToExpiry = expDate.DayNumber - todayEt.DayNumber;

            if (daysToExpiry <= 1) return "Lotto";

            var daysUntilFriday = (int)todayEt.DayOfWeek == 0
                ? 5
                : Math.Max(0, 6 - (int)todayEt.DayOfWeek);

            return expDate <= todayEt.AddDays(daysUntilFriday) ? "High" : "Standard";
        }
        catch
        {
            return "Standard";
        }
    }
}

/// <summary>Request body for POST /api/dashboard/regime.</summary>
public record RegimeOverrideRequest(string Tier);