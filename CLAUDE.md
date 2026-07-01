# Vela — Claude Code Context

## Project Overview

Vela is a .NET 10 event-driven automated trading platform executing against an IBKR paper account (DUQ048946, port 4002). It consumes trade alerts from Xtrades (REST + SignalR), evaluates them through a risk engine, sizes positions, and places orders via the TWS API. Commercial SaaS intent; currently in controlled paper trading.

**Owner:** Chris (Simo) — Meridion Systems  
**Business entity:** Meridion Systems

---

## Solution Structure

```
Vela.sln
├── Vela.AlertPoC/        # Shared Alert DTO and API client models (net8.0)
├── Vela.Worker/          # Background services, risk engine, broker execution (net10.0)
├── Vela.Api/             # Minimal API for dashboard and Spyglass ingestion (net10.0)
├── Vela.Analytics/       # Python-backed analytics utilities (net10.0)
└── Vela.Tests/           # xUnit unit + integration tests (net10.0)
```

Key paths:
- Migrations: `Vela.Worker/Data/Migrations/`
- Worker logs: `./Vela.Worker/logs/vela-YYYYMMDD.log`
- API logs: `~/Local Documents/Vela/Vela.Api/logs/api-YYYYMMDD.log`
- appsettings: `Vela.Worker/appsettings.json`

---

## Commands

```bash
dotnet build                          # build all projects
dotnet test                           # run all tests (IBKR integration tests require Gateway)
SKIP_IBKR_TESTS=true dotnet test      # skip live IBKR tests (use in CI)

# EF Core migrations (always target Vela.Worker)
dotnet ef migrations add <Name> --project Vela.Worker --startup-project Vela.Worker
dotnet ef database update --project Vela.Worker --startup-project Vela.Worker

# Docker (Postgres only — Worker and API run locally via dotnet run)
docker compose up -d                  # start Postgres
docker exec -it vela-postgres-1 psql -U vela_user -d vela

# Log grep pattern
grep -E "INF|WRN|ERR" "./Vela.Worker/logs/vela-$(date +%Y%m%d).log"
```

---

## Coding Standards

1. XML doc on public members only
2. Inline `//` for non-trivial logic only — don't over-comment
3. No alignment spaces anywhere (fields, named args, assignments)
4. No em dashes in comments or docs
5. Private helpers use `//` only, not XML doc
6. No "Fix:"/"Previously…" comments
7. Section dividers: `// -- Helpers --`
8. No hyphen mid-comment
9. Never start code changes without confirmation
10. Briefly acknowledge standards before each implementation

---

## Architecture Principles

**DB is source of truth.** The API never reads `IOptions<RiskEngineOptions>` at runtime. The Worker seeds appsettings values to `risk_config_overrides` on startup. All runtime risk engine state reads from the DB.

**Event-based composition root wiring.** Cross-service state (pause, block overrides, regime) is wired via events in `Program.cs`, not direct service references.

**Spyglass abstraction boundary.** Spyglass is the producer, Vela is the consumer. Spyglass never sees Vela source. Deduplication and suppression belong entirely to Vela. Vela exposes `POST /api/v1/spyglass/alerts` with bearer auth.

**Exit dedup bypass.** Xtrades reuses the same alert ID for BTO entry and its STC exit. Both `AlertPollingService` and `SignalRListenerService` must bypass dedup for STC/BTC alerts — the actual double-close guard is `TradeGuard.TryMarkClosing`.

**OCA trail+target.** Spyglass stock entries with a computed `PriceTarget` (non-null, above entry) receive an OCA group with trail stop + limit target. All other trades (Xtrades options, Xtrades stocks) receive trail-only. The signal is `TradeOrder.HasComputedTarget`, not identity checks — any alert source that populates `Alert.PriceTarget` automatically qualifies.

---

## Stack

- .NET 10, PostgreSQL 16, EF Core (snake_case via `HasColumnName`)
- React/Vite PWA dashboard
- Docker (Postgres only containerized)
- SignalR, Serilog, Polly
- IBKR TWS API via `reqHistoricalData`, `reqPositions`, OCA order management
- Alert source: Xtrades (REST poller + SignalR dual ingestion)
- Spyglass: .NET 10 hexagonal screener, Alpaca Markets data, pushes to Vela via HTTP POST
- CI/CD: GitHub Actions on push to `main`, Docker builds to GHCR

---

## Active Configuration (Fibonaccizer Isolation Experiment)

- `MinXScore = 100`, `ApprovedTraders = ["Fibonaccizer"]`
- `AllowHigh = false`, `AllowLotto = false`
- All slippage thresholds set to 0 (market orders for all entries)
- `OptionsMaxBudget = $6,000`, `StockMaxBudget = $6,000`
- `OptionsStandardTrailPct = 40%`
- `AllowOverrideBlocks = true` (dashboard toggle persists block settings through regime checkpoints)
- Regime-aware sizing: Bullish=1.0x, Choppy=0.5x, Bearish=0.25x

---

## Key Patterns

**Spyglass alert ID composition:**
```csharp
Id = $"spyglass-{item.Symbol}-{envelope.EmittedAt:yyyyMMddHHmmssfff}-{item.Id}"
```
Encodes scan timestamp so persistent setups re-detected on later scans get fresh rows.

**Risk config flow:**
Worker reads `appsettings.json` → seeds `risk_config_overrides` → API reads from DB → dashboard reads/writes via `GET/POST /api/config/risk`.

**Alert ingestion dedup:**
- Entries: deduplicated by alert ID in `alerts` table
- Exits (STC/BTC): bypass dedup entirely — processed on every cycle, saved only if genuinely new row

**MaxBudget fallback in PositionSizer:**
When regime-adjusted budget can't afford any contracts but `OptionsMaxBudget`/`StockMaxBudget > 0` and single-contract cost ≤ max, executes exactly 1 contract bypassing regime scaling.

---

## Known Open Issues (Backlog)

**P1 — Exit path hardening:**
- Exit race: `FindOpenTrade → broker close → RegisterClose` spans await; dual ingestion can double-close = ghost short. Entry race solved via pending-reservation system; exit not yet hardened.
- `StartupReconciliationService` skips when IBKR returns 0 positions — can't distinguish flat account from Gateway silence.

**P1 — Xtrades token lifecycle:**
- Surface expired-token state via a `system_state` flag so the dashboard can reflect it.
- Send a Discord alert on 401/403 (distinct from transient failure alerts).
- Dashboard banner warning when the token has expired.
- UI flow for re-entering `XTRADES_TOKEN` without redeploying.

**P1 — Regime intraday refresh:**
- Dead branch: below-200MA returns Bearish but comment says cap at Choppy — resolve via backtest first.
- Confirm `SystemStateService.UpdateRegime` doesn't stomp manual overrides when `AllowOverrideBlocks` is active.

**Future:**
- Per-trader dynamic targets and partial exits
- Paper trading nuclear reset command
- xScore performance analysis by band using `trade_metrics`
- Pi/unattended ops: IBC, Tailscale, GitHub Actions deploy, watchdog cron

---

## CI Notes

- Integration tests require Docker (Postgres via Testcontainers)
- IBKR integration tests require live Gateway — always skipped in CI via `SKIP_IBKR_TESTS=true`
- GitHub Actions workflow: build → test → Docker build → push to GHCR

---

## Trader Roster (Current Session)

Approved: `Fibonaccizer` (xScore 61), `Krazy` (xScore 77)  
Restricted (0% allotment): `Tim`, `Sean@BearishBull`, `kareem`  
Spyglass alerts bypass xScore entirely (treated as `UserName = "SPYGLASS"`, `XScore = 100`)