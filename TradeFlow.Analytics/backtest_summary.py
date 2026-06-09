#!/usr/bin/env python3
"""
TradeFlow Backtest Summary Script
===================================
Reads all 21 backtest CSV files from a daily or weekly folder and produces
a single ranked performance summary CSV.

Usage:
    python backtest_summary.py --dir reports/backtest/week_2026-06-09/daily/2026-06-09
    python backtest_summary.py --dir reports/backtest/week_2026-06-09
"""

import csv
import argparse
from pathlib import Path


def parse_backtest_csv(filepath: Path) -> dict | None:
    """
    Parses a single backtest CSV. Uses csv.reader to handle quoted fields
    (e.g. "# Account size simulated: $75,000") correctly.
    """
    try:
        with open(filepath, encoding="utf-8", newline="") as f:
            reader = csv.reader(f)
            rows   = list(reader)

        meta            = {}
        sections        = {}
        current_section = None
        current_rows    = []

        for row in rows:
            if not row:
                continue
            cell = row[0].strip()

            if cell.startswith("#"):
                content = cell[1:].strip()
                if ":" in content:
                    key, _, val = content.partition(":")
                    meta[key.strip()] = val.strip()
                else:
                    if current_section is not None:
                        sections[current_section] = current_rows
                    current_section = content.strip()
                    current_rows    = []
                continue

            if current_section is not None:
                current_rows.append(row)

        if current_section is not None:
            sections[current_section] = current_rows

        summary = {}
        for row in sections.get("SUMMARY", []):
            if len(row) >= 2:
                summary[row[0].strip()] = row[1].strip()

        traders     = []
        trader_rows = sections.get("TRADER BREAKDOWN", [])
        if len(trader_rows) > 1:
            header = [h.strip() for h in trader_rows[0]]
            for row in trader_rows[1:]:
                if row and any(c.strip() for c in row):
                    padded = row + [""] * (len(header) - len(row))
                    traders.append(dict(zip(header, [c.strip() for c in padded])))

        return {
            "meta":     meta,
            "summary":  summary,
            "traders":  traders,
            "filename": filepath.name,
        }

    except Exception as e:
        print(f"  Warning: could not parse {filepath.name} — {e}")
        return None


def parse_pnl(value: str) -> float:
    try:
        return float(value.replace("$", "").replace("+", "").replace(",", "").strip())
    except (ValueError, AttributeError):
        return 0.0


def build_strategy_label(meta: dict) -> str:
    """Builds a compact human-readable strategy label from the metadata block."""
    score = meta.get("Min xScore", "?")

    risk_map   = {"Standard + High + Lotto": "SHL", "Standard + High": "SH", "Standard only": "S"}
    risk_raw   = meta.get("Risk allowed", "")
    risk_short = risk_map.get(risk_raw, risk_raw or "?")

    return f"Score {score} | {risk_short}"


def get_top_bottom_traders(traders: list[dict]) -> tuple:
    active = []
    for t in traders:
        try:
            if int(t.get("Trades", 0) or 0) > 0:
                active.append(t)
        except (ValueError, TypeError):
            pass

    if not active:
        return None, None

    sorted_traders = sorted(active, key=lambda t: parse_pnl(t.get("P&L", "0")), reverse=True)
    return sorted_traders[0], sorted_traders[-1]


def format_wl(trader: dict | None) -> str:
    if trader is None:
        return "—"
    try:
        wins   = int(trader.get("Wins", 0) or 0)
        trades = int(trader.get("Trades", 0) or 0)
        return f"{wins}/{trades - wins}"
    except (ValueError, TypeError):
        return "—"


def main():
    parser = argparse.ArgumentParser(
        description="TradeFlow backtest summary — ranks all 21 scenarios into one CSV")
    parser.add_argument(
        "--dir", required=True,
        help="Path to a daily or weekly backtest folder containing backtest_*.csv files")
    args = parser.parse_args()

    folder = Path(args.dir)
    if not folder.exists():
        print(f"Error: folder not found — {folder}")
        return

    csv_files = sorted([f for f in folder.glob("backtest_*.csv") if f.is_file()])
    if not csv_files:
        print(f"No backtest_*.csv files found in {folder}")
        return

    print(f"\nTradeFlow Backtest Summary")
    print(f"Folder: {folder}")
    print(f"Found {len(csv_files)} scenario files\n")

    results = []
    for f in csv_files:
        parsed = parse_backtest_csv(f)
        if parsed is not None:
            results.append(parsed)

    if not results:
        print("No files could be parsed.")
        return

    rows = []
    for r in results:
        summary = r["summary"]
        meta    = r["meta"]
        traders = r["traders"]

        executed_count = int(summary.get("Executed trades", 0) or 0)
        win_count      = int(summary.get("Wins", 0) or 0)
        loss_count     = int(summary.get("Losses", 0) or 0)
        total_pnl      = parse_pnl(summary.get("Total P&L", "0"))
        strategy_label = build_strategy_label(meta)
        top, bottom    = get_top_bottom_traders(traders)

        rows.append({
            "strategy":     strategy_label,
            "trades":       executed_count,
            "wins":         win_count,
            "losses":       loss_count,
            "pnl":          total_pnl,
            "top_trader":   top.get("Trader", "—") if top else "—",
            "top_wl":       format_wl(top),
            "top_pnl":      parse_pnl(top.get("P&L", "0")) if top else 0.0,
            "worst_trader": bottom.get("Trader", "—") if bottom else "—",
            "worst_wl":     format_wl(bottom),
            "worst_pnl":    parse_pnl(bottom.get("P&L", "0")) if bottom else 0.0,
            "filename":     r["filename"],
        })

    rows.sort(key=lambda x: x["pnl"], reverse=True)

    output_path = folder / "performance_summary.csv"
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow([
            "Rank", "Strategy", "Trades (W/L)", "Total P&L",
            "Top Trader", "Top Trader W/L", "Top Trader P&L",
            "Worst Trader", "Worst Trader W/L", "Worst Trader P&L",
            "Filename",
        ])
        for rank, row in enumerate(rows, start=1):
            wl = f"{row['wins']}/{row['losses']}" if row["trades"] > 0 else "0/0"
            w.writerow([
                rank, row["strategy"], wl, f"${row['pnl']:+,.2f}",
                row["top_trader"], row["top_wl"], f"${row['top_pnl']:+,.2f}",
                row["worst_trader"], row["worst_wl"], f"${row['worst_pnl']:+,.2f}",
                row["filename"],
            ])

    print(f"Summary written → {output_path}")
    print(f"Ranked {len(rows)} strategies\n")

    print("Top 5:")
    for i, row in enumerate(rows[:5], 1):
        wl = f"{row['wins']}/{row['losses']}"
        print(f"  {i:2}. {row['strategy']:<25} {wl:>8}  ${row['pnl']:>+10,.2f}")

    if len(rows) > 10:
        print("\nBottom 5:")
        for i, row in enumerate(rows[-5:], len(rows) - 4):
            wl = f"{row['wins']}/{row['losses']}"
            print(f"  {i:2}. {row['strategy']:<25} {wl:>8}  ${row['pnl']:>+10,.2f}")


if __name__ == "__main__":
    main()