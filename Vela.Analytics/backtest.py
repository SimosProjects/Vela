#!/usr/bin/env python3
"""
Vela Backtest Comparison Script
=====================================
Reads closed trades from trade_metrics for a given date range and replays them
against 21 scenario combinations (7 score floors x 3 risk profiles) to find
the optimal configuration. Rank filter removed — xScore is the sole quality gate.

Usage:
    python backtest.py --mode daily --date 2026-06-09
    python backtest.py --mode weekly --date 2026-06-09

Environment:
    VELA_CONNECTION_STRING  PostgreSQL connection string (falls back to default)

Output:
    reports/backtest/week_YYYY-MM-DD/backtest_{score}_{risk}_{date}.csv
    reports/backtest/week_YYYY-MM-DD/daily/YYYY-MM-DD/backtest_{score}_{risk}_{date}.csv
"""

import os
import argparse
import csv
from datetime import date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo
import psycopg2 # type: ignore
import psycopg2.extras # type: ignore

# -- Configuration --

DEFAULT_CONN = "host=localhost dbname=vela user=vela_user password=vela_dev"

SIMULATED_ACCOUNT_BALANCE  = 75_000.0
SIMULATED_DAILY_LOSS_LIMIT = -2_000.0
SIMULATED_MAX_EXPOSURE_PCT = 100.0
SIMULATED_TRADE_BUDGET     = 3_000.0

ALWAYS_BLOCKED_TRADERS = {"Tim"}

SCORE_FLOORS = [60, 65, 70, 75, 80, 85, 90]

RISK_PROFILES = {
    "SHL": {"standard": True, "high": True,  "lotto": True },
    "SH":  {"standard": True, "high": True,  "lotto": False},
    "S":   {"standard": True, "high": False, "lotto": False},
}

# -- Database --

def get_connection():
    conn_str = os.environ.get("VELA_CONNECTION_STRING", DEFAULT_CONN)
    return psycopg2.connect(conn_str)


def fetch_trades(conn, from_dt: datetime, to_dt: datetime) -> list[dict]:
    sql = """
        SELECT
            tm.id, tm.trader_name, tm.x_score, tm.discord_rank,
            tm.symbol, tm.trade_type, tm.direction, tm.options_contract,
            tm.is_average, tm.fill_price, tm.entry_amount, tm.quantity,
            tm.pnl, tm.pnl_pct, tm.outcome, tm.order_filled_at, tm.closed_at,
            tm.slippage_pct, tm.latency_ms,
            a.risk AS alert_risk
        FROM trade_metrics tm
        LEFT JOIN alerts a ON a.id = tm.alert_id
        WHERE tm.closed_at >= %s AND tm.closed_at < %s AND tm.pnl IS NOT NULL
        ORDER BY tm.order_filled_at ASC
    """
    with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
        cur.execute(sql, (from_dt, to_dt))
        return [dict(row) for row in cur.fetchall()]


def fetch_open_trades(conn, from_dt: datetime, to_dt: datetime) -> list[dict]:
    sql = """
        SELECT
            tm.id, tm.trader_name, tm.x_score, tm.discord_rank,
            tm.symbol, tm.trade_type, tm.entry_amount, tm.order_filled_at,
            a.risk AS alert_risk
        FROM trade_metrics tm
        LEFT JOIN alerts a ON a.id = tm.alert_id
        WHERE tm.order_filled_at >= %s AND tm.order_filled_at < %s AND tm.pnl IS NULL
        ORDER BY tm.order_filled_at ASC
    """
    with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
        cur.execute(sql, (from_dt, to_dt))
        return [dict(row) for row in cur.fetchall()]


# -- Scenario filtering --

def passes_scenario(trade: dict, score_floor: int, risk_profile: dict) -> bool:
    """
    Returns True if this trade would have executed under the given scenario.
    Rank filter removed — xScore is the sole quality gate.
    discord_rank is still logged to trade_metrics for analytics purposes.
    """
    if (trade.get("trader_name") or "") in ALWAYS_BLOCKED_TRADERS:
        return False

    if float(trade.get("x_score") or 0) < score_floor:
        return False

    risk = (trade.get("alert_risk") or "standard").lower()
    if risk == "lotto" and not risk_profile["lotto"]:
        return False
    if risk == "high" and not risk_profile["high"]:
        return False

    return True


# -- $75k account simulation --

def simulate_account(trades: list[dict], score_floor: int, risk_profile: dict) -> dict:
    """
    Replays trades chronologically against a simulated $75k account.
    Simulates what would have happened on a real $75k account even though
    trades executed on the $250k paper account.
    """
    daily_pnl      = {}
    open_positions = {}
    results        = []
    cap_blocked    = []
    config_filtered = []

    for trade in trades:
        filled_at  = trade.get("order_filled_at")
        trade_date = filled_at.date() if hasattr(filled_at, "date") else date.fromisoformat(str(filled_at)[:10]) if filled_at else None
        if trade_date not in daily_pnl:
            daily_pnl[trade_date] = 0.0

        if not passes_scenario(trade, score_floor, risk_profile):
            config_filtered.append(trade)
            continue

        if daily_pnl.get(trade_date, 0.0) <= SIMULATED_DAILY_LOSS_LIMIT:
            cap_blocked.append({**trade, "block_reason": "daily_loss_limit"})
            continue

        trade_budget   = float(trade.get("entry_amount") or SIMULATED_TRADE_BUDGET)
        max_deployable = SIMULATED_ACCOUNT_BALANCE * (SIMULATED_MAX_EXPOSURE_PCT / 100.0)
        if sum(open_positions.values()) + trade_budget > max_deployable:
            cap_blocked.append({**trade, "block_reason": "exposure_cap"})
            continue

        pnl = float(trade.get("pnl") or 0)
        open_positions[trade["id"]] = trade_budget
        if trade.get("closed_at"):
            open_positions.pop(trade["id"], None)
            daily_pnl[trade_date] = daily_pnl.get(trade_date, 0.0) + pnl
        results.append(trade)

    executed_pnl     = sum(float(t.get("pnl") or 0) for t in results)
    wins             = [t for t in results if float(t.get("pnl") or 0) > 0]
    losses           = [t for t in results if float(t.get("pnl") or 0) < 0]
    win_rate         = len(wins) / len(results) * 100 if results else 0
    avg_win          = sum(float(t.get("pnl") or 0) for t in wins) / len(wins) if wins else 0
    avg_loss         = sum(float(t.get("pnl") or 0) for t in losses) / len(losses) if losses else 0
    cap_pnl_if_taken = sum(float(t.get("pnl") or 0) for t in cap_blocked)
    options_trades   = [t for t in results if (t.get("trade_type") or "").lower() == "options"]
    stock_trades     = [t for t in results if (t.get("trade_type") or "").lower() == "stock"]

    daily_breakdown = {}
    for t in results:
        fa = t.get("order_filled_at")
        if fa:
            d = fa.date() if hasattr(fa, "date") else date.fromisoformat(str(fa)[:10])
            if d not in daily_breakdown:
                daily_breakdown[d] = {"pnl": 0.0, "trades": 0, "wins": 0}
            daily_breakdown[d]["pnl"]    += float(t.get("pnl") or 0)
            daily_breakdown[d]["trades"] += 1
            if float(t.get("pnl") or 0) > 0:
                daily_breakdown[d]["wins"] += 1

    trader_breakdown = {}
    for t in results:
        name = t.get("trader_name") or "Unknown"
        if name not in trader_breakdown:
            trader_breakdown[name] = {
                "trades": 0, "wins": 0, "pnl": 0.0,
                "x_score":      float(t.get("x_score") or 0),
                "discord_rank": t.get("discord_rank") or "",
            }
        trader_breakdown[name]["trades"] += 1
        trader_breakdown[name]["pnl"]    += float(t.get("pnl") or 0)
        if float(t.get("pnl") or 0) > 0:
            trader_breakdown[name]["wins"] += 1

    return {
        "executed":        results,
        "cap_blocked":     cap_blocked,
        "config_filtered": config_filtered,
        "summary": {
            "executed_count":        len(results),
            "executed_pnl":          round(executed_pnl, 2),
            "win_count":             len(wins),
            "loss_count":            len(losses),
            "win_rate_pct":          round(win_rate, 1),
            "avg_win":               round(avg_win, 2),
            "avg_loss":              round(avg_loss, 2),
            "options_count":         len(options_trades),
            "options_pnl":           round(sum(float(t.get("pnl") or 0) for t in options_trades), 2),
            "stock_count":           len(stock_trades),
            "stock_pnl":             round(sum(float(t.get("pnl") or 0) for t in stock_trades), 2),
            "cap_blocked_count":     len(cap_blocked),
            "cap_pnl_if_taken":      round(cap_pnl_if_taken, 2),
            "config_filtered_count": len(config_filtered),
        },
        "daily_breakdown":  {str(k): v for k, v in sorted(daily_breakdown.items())},
        "trader_breakdown": dict(sorted(trader_breakdown.items(), key=lambda x: x[1]["pnl"], reverse=True)),
    }


# -- Report writing --

def write_report(result: dict, score_floor: int, risk_key: str,
                 period_label: str, from_dt: datetime, to_dt: datetime,
                 output_path: Path, open_trades: list[dict]):
    risk_profile = RISK_PROFILES[risk_key]
    summary      = result["summary"]
    risk_label   = {"SHL": "Standard + High + Lotto", "SH": "Standard + High", "S": "Standard only"}[risk_key]

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)

        w.writerow(["# Vela Backtest Report"])
        w.writerow([f"# Period: {period_label}"])
        w.writerow([f"# From: {from_dt.strftime('%Y-%m-%d')}"])
        w.writerow([f"# To: {(to_dt - timedelta(days=1)).strftime('%Y-%m-%d')}"])
        w.writerow([f"# Min xScore: {score_floor}"])
        w.writerow([f"# Risk allowed: {risk_label}"])
        w.writerow([f"# Always blocked traders: {', '.join(sorted(ALWAYS_BLOCKED_TRADERS))}"])
        w.writerow([f"# Account size simulated: ${SIMULATED_ACCOUNT_BALANCE:,.0f}"])
        w.writerow([f"# Daily loss limit simulated: ${SIMULATED_DAILY_LOSS_LIMIT:,.0f}"])
        w.writerow([f"# Max exposure simulated: {SIMULATED_MAX_EXPOSURE_PCT:.0f}%"])
        w.writerow([f"# Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S ET')}"])
        w.writerow([])

        w.writerow(["# SUMMARY"])
        w.writerow(["Metric", "Value"])
        w.writerow(["Executed trades",            summary["executed_count"]])
        w.writerow(["Total P&L",                  f"${summary['executed_pnl']:+,.2f}"])
        w.writerow(["Wins",                       summary["win_count"]])
        w.writerow(["Losses",                     summary["loss_count"]])
        w.writerow(["Win rate",                   f"{summary['win_rate_pct']:.1f}%"])
        w.writerow(["Avg win",                    f"${summary['avg_win']:+,.2f}"])
        w.writerow(["Avg loss",                   f"${summary['avg_loss']:+,.2f}"])
        w.writerow(["Options trades",             summary["options_count"]])
        w.writerow(["Options P&L",                f"${summary['options_pnl']:+,.2f}"])
        w.writerow(["Stock trades",               summary["stock_count"]])
        w.writerow(["Stock P&L",                  f"${summary['stock_pnl']:+,.2f}"])
        w.writerow(["Cap blocked trades",         summary["cap_blocked_count"]])
        w.writerow(["Cap blocked P&L (if taken)", f"${summary['cap_pnl_if_taken']:+,.2f}"])
        w.writerow(["Config filtered trades",     summary["config_filtered_count"]])
        w.writerow([])

        w.writerow(["# DAILY BREAKDOWN"])
        w.writerow(["Date", "Trades", "Wins", "Win Rate", "P&L"])
        for d, stats in result["daily_breakdown"].items():
            wr = stats["wins"] / stats["trades"] * 100 if stats["trades"] else 0
            w.writerow([d, stats["trades"], stats["wins"], f"{wr:.1f}%", f"${stats['pnl']:+,.2f}"])
        w.writerow([])

        w.writerow(["# TRADER BREAKDOWN"])
        w.writerow(["Trader", "xScore", "Rank", "Trades", "Wins", "Win Rate", "P&L"])
        for name, stats in result["trader_breakdown"].items():
            wr = stats["wins"] / stats["trades"] * 100 if stats["trades"] else 0
            w.writerow([name, stats["x_score"], stats["discord_rank"],
                        stats["trades"], stats["wins"], f"{wr:.1f}%", f"${stats['pnl']:+,.2f}"])
        w.writerow([])

        w.writerow(["# EXECUTED TRADES"])
        w.writerow(["Symbol", "Type", "Direction", "Trader", "xScore", "Rank",
                    "Risk", "Qty", "Entry $", "Entry Amount", "Exit $", "P&L", "P&L %",
                    "Outcome", "Opened", "Closed", "Slippage %", "Latency ms"])
        for t in result["executed"]:
            w.writerow([
                t.get("symbol") or "", t.get("trade_type") or "", t.get("direction") or "",
                t.get("trader_name") or "", t.get("x_score") or "", t.get("discord_rank") or "",
                t.get("alert_risk") or "", t.get("quantity") or "",
                f"${float(t.get('fill_price') or 0):.2f}",
                f"${float(t.get('entry_amount') or 0):.2f}",
                f"${float(t.get('exit_price') or 0):.2f}" if t.get("exit_price") else "",
                f"${float(t.get('pnl') or 0):+,.2f}",
                f"{float(t.get('pnl_pct') or 0):+.1f}%" if t.get("pnl_pct") else "",
                t.get("outcome") or "",
                str(t.get("order_filled_at") or "")[:16],
                str(t.get("closed_at") or "")[:16],
                f"{float(t.get('slippage_pct') or 0):.2f}%" if t.get("slippage_pct") else "",
                t.get("latency_ms") or "",
            ])
        w.writerow([])

        if result["cap_blocked"]:
            w.writerow(["# CAP BLOCKED TRADES (would have executed but $75k cap prevented it)"])
            w.writerow(["Symbol", "Trader", "xScore", "Risk", "Entry Amount",
                        "P&L (actual outcome)", "Block Reason", "Opened"])
            for t in result["cap_blocked"]:
                w.writerow([
                    t.get("symbol") or "", t.get("trader_name") or "", t.get("x_score") or "",
                    t.get("alert_risk") or "", f"${float(t.get('entry_amount') or 0):.2f}",
                    f"${float(t.get('pnl') or 0):+,.2f}", t.get("block_reason") or "",
                    str(t.get("order_filled_at") or "")[:16],
                ])
            w.writerow([])

        relevant_open = [t for t in open_trades if passes_scenario(t, score_floor, risk_profile)]
        if relevant_open:
            w.writerow(["# STILL OPEN (entered during period, not yet closed — P&L unknown)"])
            w.writerow(["Symbol", "Trader", "xScore", "Rank", "Risk", "Entry Amount", "Opened"])
            for t in relevant_open:
                w.writerow([
                    t.get("symbol") or "", t.get("trader_name") or "", t.get("x_score") or "",
                    t.get("discord_rank") or "", t.get("alert_risk") or "",
                    f"${float(t.get('entry_amount') or 0):.2f}",
                    str(t.get("order_filled_at") or "")[:16],
                ])


# -- Date helpers --

def get_week_monday(ref: date) -> date:
    return ref - timedelta(days=ref.weekday())

def date_range_for_day(d: date):
    et = ZoneInfo("America/New_York")
    from_dt = datetime(d.year, d.month, d.day, 0, 0, 0, tzinfo=et)
    return from_dt, from_dt + timedelta(days=1)

def date_range_for_week(monday: date):
    et = ZoneInfo("America/New_York")
    from_dt = datetime(monday.year, monday.month, monday.day, 0, 0, 0, tzinfo=et)
    return from_dt, from_dt + timedelta(days=5)


# -- Main --

def main():
    parser = argparse.ArgumentParser(description="Vela backtest comparison script")
    parser.add_argument("--mode", choices=["daily", "weekly"], required=True)
    parser.add_argument("--date", type=str, default=None)
    parser.add_argument("--output", type=str, default="reports/backtest")
    args = parser.parse_args()

    ref_date        = date.fromisoformat(args.date) if args.date else date.today()
    base_dir        = Path(args.output)
    monday          = get_week_monday(ref_date)
    week_dir        = base_dir / f"week_{monday.strftime('%Y-%m-%d')}"
    total_scenarios = len(SCORE_FLOORS) * len(RISK_PROFILES)
    days            = [ref_date] if args.mode == "daily" else [monday + timedelta(days=i) for i in range(5)]

    print(f"\nVela Backtest — mode={args.mode} week={monday}")
    print(f"Output directory: {week_dir}")
    print(f"Scenarios: {len(SCORE_FLOORS)} scores x {len(RISK_PROFILES)} risk = {total_scenarios} total\n")

    conn = get_connection()
    try:
        week_from, week_to = date_range_for_week(monday)
        print(f"Fetching trades {week_from.date()} -> {(week_to - timedelta(days=1)).date()}...")
        all_week_trades = fetch_trades(conn, week_from, week_to)
        all_week_open   = fetch_open_trades(conn, week_from, week_to)
        print(f"Found {len(all_week_trades)} closed trades, {len(all_week_open)} still open\n")

        scenario_count = 0

        for day in days:
            day_from, day_to = date_range_for_day(day)
            day_trades = [t for t in all_week_trades if t.get("closed_at") and day_from <= t["closed_at"] < day_to]
            day_open   = [t for t in all_week_open if t.get("order_filled_at") and day_from <= t["order_filled_at"] < day_to]

            if not day_trades and not day_open:
                print(f"  {day} — no trades, skipping")
                continue

            print(f"  {day} — {len(day_trades)} closed, {len(day_open)} open")
            day_dir      = week_dir / "daily" / day.strftime("%Y-%m-%d")
            period_label = f"Daily — {day.strftime('%Y-%m-%d')}"

            for score in SCORE_FLOORS:
                for risk_key, risk_profile in RISK_PROFILES.items():
                    result   = simulate_account(day_trades, score, risk_profile)
                    filename = f"backtest_{score}_{risk_key}_{day.strftime('%Y-%m-%d')}.csv"
                    write_report(result, score, risk_key, period_label,
                                 day_from, day_to, day_dir / filename, day_open)
                    scenario_count += 1

            print(f"    -> {total_scenarios} scenarios written to {day_dir}")

        if args.mode == "weekly":
            print(f"\n  Week {monday} — {len(all_week_trades)} closed, {len(all_week_open)} open")
            period_label = f"Weekly — {monday.strftime('%Y-%m-%d')} to {(monday + timedelta(days=4)).strftime('%Y-%m-%d')}"
            for score in SCORE_FLOORS:
                for risk_key, risk_profile in RISK_PROFILES.items():
                    result   = simulate_account(all_week_trades, score, risk_profile)
                    friday   = monday + timedelta(days=4)
                    filename = f"backtest_{score}_{risk_key}_{friday.strftime('%Y-%m-%d')}.csv"
                    write_report(result, score, risk_key, period_label,
                                 week_from, week_to, week_dir / filename, all_week_open)
            print(f"    -> {total_scenarios} weekly scenarios written to {week_dir}")

        print(f"\nDone. Total files written: {scenario_count + (total_scenarios if args.mode == 'weekly' else 0)}")

    finally:
        conn.close()

if __name__ == "__main__":
    main()