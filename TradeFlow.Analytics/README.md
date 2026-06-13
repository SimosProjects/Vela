# TradeFlow Analytics

Two analytics systems run against the TradeFlow PostgreSQL database. Neither reads from the CSV files, both query `trade_metrics` directly, so they are independent of the CSV archive cycle.

---

## 1. Python Backtest Pipeline

**What it does:** Replays closed trades from `trade_metrics` against 21 scenario combinations to find the optimal risk configuration for any given day or week. Each scenario varies the minimum xScore floor and which risk tiers are allowed to trade.

**Scenarios — 21 total (7 × 3):**

| Score Floors | Risk Profiles |
|---|---|
| 60, 65, 70, 75, 80, 85, 90 | `S` (Standard only), `SH` (Standard + High), `SHL` (Standard + High + Lotto) |

Each scenario simulates a $75,000 account with a $2,000 daily loss limit and a $3,000 per-trade budget.

**Files:**

| File | Purpose |
|---|---|
| `backtest.py` | Core engine — queries DB, runs 21 simulations, writes CSV reports |
| `backtest_summary.py` | Reads 21 CSV files from a folder and produces a single ranked summary CSV |
| `run_backtest.sh` | Wrapper runs `backtest.py` then `backtest_summary.py` in one command |

**Output structure:**
```
reports/backtest/
  week_2026-06-09/
    daily/
      2026-06-09/
        backtest_60_S_2026-06-09.csv
        backtest_60_SH_2026-06-09.csv
        ...  (21 files)
        summary_2026-06-09.csv
      2026-06-10/
        ...  (21 files + summary)
    backtest_60_S_2026-06-13.csv    ← weekly scenarios, one per combination
    backtest_60_SH_2026-06-13.csv
    ...  (21 files)
    summary_2026-06-13.csv
```

---

## 2. .NET Analytics Engine

**What it does:** Generates a self-contained HTML performance report from `trade_metrics` and `alerts`. Covers executive summary, filter rates, win/loss breakdown, trader rankings, symbol performance, trade type split, latency, and exposure charts.

**Report types:**

| Flag | Period |
|---|---|
| `--report weekly` | Last 7 days (default) |
| `--report monthly` | Last 30 days |
| `--report custom --from 2026-06-01 --to 2026-06-30` | Any date range |

**Output:** A single self-contained HTML file in `reports/`, no external dependencies, opens directly in any browser.

**Automatic scheduling:** `MarketSchedulerService` triggers the weekly HTML report automatically at market close every Friday. No manual run needed for the weekly summary.

**Manual run:**
```bash
cd TradeFlow.Analytics
dotnet run -- --report weekly
dotnet run -- --report monthly
dotnet run -- --report custom --from 2026-06-01 --to 2026-06-30 --output ../reports
```

---

## 3. CSV Reconciliation Tool

**What it does:** Compares the working CSV trade logs (`options_trades.csv`, `stocks_trades.csv`) against `trade_metrics` and reports discrepancies. `trade_metrics` is always the source of truth; the CSV is what gets checked and optionally repaired.

**Discrepancy types detected:**

| Type | Description |
|---|---|
| `ORPHANED_OPEN` | CSV row is Open but DB has a P&L value — DB closed it, CSV missed the update |
| `MISSING_CLOSE` | CSV row is Closed but DB has no P&L — CSV updated, DB not |
| `PNL_MISMATCH` | Both closed, P&L differs by more than `--pnl-threshold` (default $1.00) |
| `CSV_ONLY` | CSV has an OrderId not present in `trade_metrics` |
| `DB_ONLY` | `trade_metrics` record not matched to any CSV row |
| `UNMATCHED_ROW` | Pre-migration CSV row (no OrderId) that could not be fuzzy-matched |

**Files:**

| File | Purpose |
|---|---|
| `csv_reconcile.py` | Core engine — queries DB, parses CSVs, reports discrepancies |
| `run_reconcile.sh` | Wrapper that auto-discovers the working CSV paths |

**Standard post-session run:**
```bash
cd TradeFlow.Analytics

# Use Monday of the current week as --since to filter archived closed positions
./run_reconcile.sh --since 2026-06-16
```

**Backfill missing OrderIds** (one-time migration for pre-deploy rows):
```bash
./run_reconcile.sh --since 2026-06-16 --backfill
```

Backfill fuzzy-matches pre-migration rows (no OrderId) to DB records by symbol, date, and price within 0.5% of fill price. Only rows with a unique match are updated — ambiguous matches are left for manual review.

**After the Friday weekly archive:**

The archive strips closed rows from the working CSV. Running the tool after archive will show `DB_ONLY` for every position closed that week — this is expected and the tool prints a note explaining it. The 22 archived closed positions are not real discrepancies.

```bash
# Post-archive run still works — DB_ONLY closed are explained automatically
./run_reconcile.sh --since 2026-06-16

# Write discrepancy report to a file for review
./run_reconcile.sh --since 2026-06-16 --output reconcile_2026-06-20.csv
```

**`--since` behaviour:**
- Closed DB records: only included if `order_filled_at` (ET) is on or after `--since`
- Open DB records: always included regardless of `--since` — ensures pre-deploy open positions are still checked even if opened before the filter date

---

## Running the Backtest

### Setup (first time only)
```bash
cd TradeFlow.Analytics
python3 -m venv .venv
source .venv/bin/activate
pip install psycopg2-binary
```

### Daily backtest
Run after market close for any individual trading day:
```bash
cd TradeFlow.Analytics
./run_backtest.sh --mode daily --date 2026-06-09
```

Writes 21 scenario CSVs + summary to `reports/backtest/week_2026-06-09/daily/2026-06-09/`.

### Weekly backtest
**Always run the daily for each trading day before running the weekly.** The weekly mode generates all five daily folders internally in one pass, then produces the week-level summary on top. Running daily separately during the week gives you incremental results as each session closes; running weekly at the end rolls everything up.

**Recommended end-of-week workflow (Friday after close):**
```bash
cd TradeFlow.Analytics

# Run daily for each day of the week that had trades
./run_backtest.sh --mode daily --date 2026-06-09   # Monday
./run_backtest.sh --mode daily --date 2026-06-10   # Tuesday
./run_backtest.sh --mode daily --date 2026-06-11   # Wednesday
./run_backtest.sh --mode daily --date 2026-06-12   # Thursday
./run_backtest.sh --mode daily --date 2026-06-13   # Friday

# Then run weekly, this regenerates all daily folders AND produces the week summary
./run_backtest.sh --mode weekly --date 2026-06-09
```

The `--date` for weekly can be any date within the target week, the script calculates Monday automatically.

**If you only want to run once on Friday:**
```bash
./run_backtest.sh --mode weekly --date 2026-06-09
```
This generates all five daily folders plus the week summary in a single pass. Use this if you skipped the daily runs during the week.

### Custom date
```bash
./run_backtest.sh --mode daily --date 2026-05-15
./run_backtest.sh --mode weekly --date 2026-05-12
```

### Environment (optional)
By default the scripts connect to `localhost` with `tradeflow_user`. Override with:
```bash
export TRADEFLOW_CONNECTION_STRING="host=myhost dbname=tradeflow user=myuser password=mypass"
./run_backtest.sh --mode weekly --date 2026-06-09
./run_reconcile.sh --since 2026-06-16
```

---

## Output Locations

| Tool | Output |
|---|---|
| Daily backtest | `reports/backtest/week_YYYY-MM-DD/daily/YYYY-MM-DD/` |
| Weekly backtest | `reports/backtest/week_YYYY-MM-DD/` |
| HTML analytics | `reports/weekly_YYYY-MM-DD.html`, `reports/monthly_YYYY-MM-DD.html` |
| CSV reconciliation | Console output + optional `--output FILE` |

All paths are relative to the repository root.