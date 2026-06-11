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
By default the backtest connects to `localhost` with `tradeflow_user`. Override with:
```bash
export TRADEFLOW_CONNECTION_STRING="host=myhost dbname=tradeflow user=myuser password=mypass"
./run_backtest.sh --mode weekly --date 2026-06-09
```

---

## Output Locations

| Tool | Output |
|---|---|
| Daily backtest | `reports/backtest/week_YYYY-MM-DD/daily/YYYY-MM-DD/` |
| Weekly backtest | `reports/backtest/week_YYYY-MM-DD/` |
| HTML analytics | `reports/weekly_YYYY-MM-DD.html`, `reports/monthly_YYYY-MM-DD.html` |

All paths are relative to the repository root.