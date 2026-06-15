#!/usr/bin/env python3
"""
Vela CSV Reconciliation Script
=====================================
Compares CSV trade logs against trade_metrics (source of truth) and reports discrepancies.
trade_metrics is always right; the CSV is what we check and optionally repair.

Discrepancy types:
  ORPHANED_OPEN  - CSV row is Open but DB has a pnl value (DB closed it, CSV missed the update)
  MISSING_CLOSE  - CSV row is Closed but DB has no pnl (CSV updated, DB not)
  PNL_MISMATCH   - Both closed, P&L differs by more than --pnl-threshold (default $1.00)
  CSV_ONLY       - CSV row has an OrderId not present in trade_metrics
  DB_ONLY        - trade_metrics record not matched to any CSV row
  UNMATCHED_ROW  - Pre-migration CSV row (no OrderId) that could not be fuzzy-matched

Modes:
  (default)        Report discrepancies, make no changes
  --backfill       Write missing OrderIds back to CSV rows with a unique fuzzy match
  --output FILE    Also write the full discrepancy report to a CSV file

Usage:
    cd Vela.Analytics
    source .venv/bin/activate
    python csv_reconcile.py \\
        --options ../trades/options_trades.csv \\
        --stocks  ../trades/stocks_trades.csv
    python csv_reconcile.py ... --backfill
    python csv_reconcile.py ... --output report.csv
"""

import os
import argparse
import csv as csv_module
from decimal import Decimal, InvalidOperation
from datetime import date
from pathlib import Path
from zoneinfo import ZoneInfo
import psycopg2         # type: ignore
import psycopg2.extras  # type: ignore

# -- Config --

DEFAULT_CONN  = "host=localhost dbname=vela user=vela_user password=vela_dev"
EASTERN_TIME  = ZoneInfo("America/New_York")
PNL_TOLERANCE = Decimal("1.00")

# -- CSV column indices (0-based), mirroring CsvTradeLogger constants --

OPTIONS_SYMBOL_COL   = 4
OPTIONS_PRICE_COL    = 10
OPTIONS_DATE_COL     = 0
OPTIONS_STATUS_COL   = 18
OPTIONS_PNL_COL      = 23
OPTIONS_ORDER_ID_COL = 25
OPTIONS_MIN_COLS     = 19

STOCKS_SYMBOL_COL    = 4
STOCKS_PRICE_COL     = 6
STOCKS_DATE_COL      = 0
STOCKS_STATUS_COL    = 14
STOCKS_PNL_COL       = 19
STOCKS_ORDER_ID_COL  = 21
STOCKS_MIN_COLS      = 15

# -- Discrepancy type labels --

ORPHANED_OPEN = "ORPHANED_OPEN"
MISSING_CLOSE = "MISSING_CLOSE"
PNL_MISMATCH  = "PNL_MISMATCH"
CSV_ONLY      = "CSV_ONLY"
DB_ONLY       = "DB_ONLY"
UNMATCHED_ROW = "UNMATCHED_ROW"


# -- Database --

def get_connection():
    conn_str = os.environ.get("VELA_CONNECTION_STRING", DEFAULT_CONN)
    return psycopg2.connect(conn_str)


def fetch_db_records(conn, since: date | None = None) -> dict[str, dict]:
    """
    Returns all non-average trade_metrics records keyed by id (order ID).
    Average entries are excluded — they update the CSV row in place rather than
    adding a new one, so matching them would produce spurious DB_ONLY noise.
    Pass since to limit results to records opened on or after that date (ET),
    which avoids false DB_ONLY hits for closed positions already archived from CSV.
    """
    sql = """
        SELECT
            tm.id,
            tm.symbol,
            tm.trade_type,
            tm.fill_price,
            tm.entry_amount,
            tm.quantity,
            tm.pnl,
            tm.pnl_pct,
            tm.outcome,
            tm.order_filled_at,
            tm.closed_at
        FROM trade_metrics tm
        WHERE (tm.is_average = false OR tm.is_average IS NULL)
          AND (
            tm.pnl IS NULL
            OR %s IS NULL
            OR DATE(tm.order_filled_at AT TIME ZONE 'America/New_York') >= %s
          )
        ORDER BY tm.order_filled_at ASC
    """
    with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
        cur.execute(sql, (since, since))
        rows = cur.fetchall()

    return {str(r["id"]): dict(r) for r in rows}


# -- CSV parsing --

def parse_csv(path: Path, is_options: bool) -> list[dict]:
    """
    Reads a trade CSV and returns a list of row dicts for data rows only.
    Skips the header, blank lines, and summary block (lines starting with ",,").
    Each dict includes: line_index, symbol, status, order_id, pnl,
    entry_price, date_opened, and the raw cols list for backfill writes.
    """
    order_id_col = OPTIONS_ORDER_ID_COL if is_options else STOCKS_ORDER_ID_COL
    status_col   = OPTIONS_STATUS_COL   if is_options else STOCKS_STATUS_COL
    pnl_col      = OPTIONS_PNL_COL      if is_options else STOCKS_PNL_COL
    symbol_col   = OPTIONS_SYMBOL_COL   if is_options else STOCKS_SYMBOL_COL
    price_col    = OPTIONS_PRICE_COL    if is_options else STOCKS_PRICE_COL
    date_col     = OPTIONS_DATE_COL     if is_options else STOCKS_DATE_COL
    min_cols     = OPTIONS_MIN_COLS     if is_options else STOCKS_MIN_COLS

    if not path.exists():
        return []

    with open(path, encoding="utf-8", newline="") as f:
        lines = f.readlines()

    rows = []
    for line_idx, raw in enumerate(lines):
        line = raw.rstrip("\r\n")
        if line_idx == 0 or not line.strip() or line.startswith(",,"):
            continue

        cols = line.split(",")
        if len(cols) < min_cols:
            continue

        order_id    = cols[order_id_col].strip() if len(cols) > order_id_col else ""
        date_opened = None
        entry_price = None
        pnl_val     = None

        try:
            date_opened = date.fromisoformat(cols[date_col].strip())
        except (ValueError, IndexError):
            pass

        try:
            entry_price = Decimal(cols[price_col].strip())
        except (InvalidOperation, IndexError):
            pass

        if len(cols) > pnl_col:
            try:
                pnl_val = Decimal(cols[pnl_col].strip().lstrip("+")) or None
            except (InvalidOperation, ValueError):
                pass

        rows.append({
            "line_index":  line_idx,
            "cols":        cols,
            "symbol":      cols[symbol_col].strip(),
            "status":      cols[status_col].strip() if len(cols) > status_col else "",
            "order_id":    order_id,
            "pnl":         pnl_val,
            "entry_price": entry_price,
            "date_opened": date_opened,
        })

    return rows


# -- Fuzzy matching --

def fuzzy_match(csv_row: dict, db_records: dict[str, dict]) -> str | None:
    """
    Attempts to match a pre-migration CSV row (no OrderId) to a DB record.
    Criteria: same symbol, entry price within 0.5% (floored at $0.20), same date (ET).
    Percentage-based tolerance scales with price level — $0.20 flat is too tight
    for high-priced stocks where fill vs alert can differ by more than that.
    Uniqueness is required; two candidates means ambiguous, returns None.
    """
    candidates = []

    for order_id, rec in db_records.items():
        if rec.get("symbol", "") != csv_row["symbol"]:
            continue

        db_price  = Decimal(str(rec["fill_price"] or 0))
        csv_price = csv_row["entry_price"] or Decimal("0")

        # Tolerance scales with price: 0.5% of the higher price, floored at $0.20.
        ref_price       = max(db_price, csv_price) or Decimal("1")
        price_tolerance = max(Decimal("0.20"), ref_price * Decimal("0.005"))

        if abs(db_price - csv_price) > price_tolerance:
            continue

        db_date = None
        if rec.get("order_filled_at"):
            dt = rec["order_filled_at"]
            if dt.tzinfo is None:
                from datetime import timezone as _tz
                dt = dt.replace(tzinfo=_tz.utc)
            db_date = dt.astimezone(EASTERN_TIME).date()
        if db_date != csv_row["date_opened"]:
            continue

        candidates.append(order_id)

    return candidates[0] if len(candidates) == 1 else None


# -- Reconciliation --

def reconcile(
    csv_rows: list[dict],
    db_records: dict[str, dict],
) -> tuple[list[dict], set[str]]:
    """
    Diffs CSV rows against DB records.
    Returns a list of discrepancy dicts and the set of DB order_ids matched to a CSV row.
    """
    discrepancies  = []
    matched_db_ids = set()

    for row in csv_rows:
        order_id = row["order_id"]
        csv_open = row["status"] == "Open"
        csv_pnl  = row["pnl"]

        if order_id:
            if order_id not in db_records:
                discrepancies.append({
                    "type":     CSV_ONLY,
                    "symbol":   row["symbol"],
                    "order_id": order_id,
                    "detail":   "OrderId found in CSV but not in trade_metrics",
                })
                continue

            rec = db_records[order_id]
            matched_db_ids.add(order_id)
            db_open = rec["pnl"] is None

            if csv_open and not db_open:
                discrepancies.append({
                    "type":     ORPHANED_OPEN,
                    "symbol":   row["symbol"],
                    "order_id": order_id,
                    "detail":   (
                        f"CSV: Open — DB: Closed"
                        f" ${float(rec['pnl']):+.2f}"
                        f" on {_fmt_date(rec['closed_at'])}"
                    ),
                    "db_rec":  rec,
                })

            elif not csv_open and db_open:
                discrepancies.append({
                    "type":     MISSING_CLOSE,
                    "symbol":   row["symbol"],
                    "order_id": order_id,
                    "detail":   "CSV: Closed — DB: still open (pnl is null)",
                })

            elif not csv_open and not db_open and csv_pnl is not None:
                db_pnl = Decimal(str(rec["pnl"]))
                diff   = abs(csv_pnl - db_pnl)
                if diff > PNL_TOLERANCE:
                    discrepancies.append({
                        "type":     PNL_MISMATCH,
                        "symbol":   row["symbol"],
                        "order_id": order_id,
                        "detail":   (
                            f"CSV: ${csv_pnl:+.2f}"
                            f" — DB: ${db_pnl:+.2f}"
                            f" (diff: ${diff:.2f})"
                        ),
                    })

        else:
            matched_id = fuzzy_match(row, db_records)
            if matched_id:
                matched_db_ids.add(matched_id)
                rec     = db_records[matched_id]
                db_open = rec["pnl"] is None
                if csv_open and not db_open:
                    discrepancies.append({
                        "type":            ORPHANED_OPEN,
                        "symbol":          row["symbol"],
                        "order_id":        f"(fuzzy {matched_id})",
                        "detail":          (
                            f"CSV: Open — DB: Closed"
                            f" ${float(rec['pnl']):+.2f}"
                            f" on {_fmt_date(rec['closed_at'])}"
                        ),
                        "db_rec":          rec,
                        "fuzzy_match_id":  matched_id,
                        "csv_row":         row,
                    })
            else:
                discrepancies.append({
                    "type":     UNMATCHED_ROW,
                    "symbol":   row["symbol"],
                    "order_id": "",
                    "detail":   (
                        f"No OrderId, no unique fuzzy match"
                        f" (date={row['date_opened']}, price={row['entry_price']})"
                    ),
                    "csv_row":  row,
                })

    return discrepancies, matched_db_ids


def find_db_only(db_records: dict[str, dict], matched_ids: set[str]) -> list[dict]:
    """Returns discrepancies for DB records that were not matched to any CSV row."""
    result = []
    for order_id, rec in db_records.items():
        if order_id not in matched_ids:
            status = "open" if rec["pnl"] is None else f"closed ${float(rec['pnl']):+.2f}"
            result.append({
                "type":     DB_ONLY,
                "symbol":   rec["symbol"],
                "order_id": order_id,
                "detail":   f"In trade_metrics ({status}) but not matched in CSV",
            })
    return result


# -- Backfill --

def backfill_order_ids(
    path: Path,
    csv_rows: list[dict],
    db_records: dict[str, dict],
    is_options: bool,
) -> int:
    """
    Writes missing OrderIds back to CSV rows that can be uniquely fuzzy-matched.
    Only rows without an existing OrderId are modified. Returns the count updated.
    """
    order_id_col = OPTIONS_ORDER_ID_COL if is_options else STOCKS_ORDER_ID_COL

    with open(path, encoding="utf-8", newline="") as f:
        lines = f.readlines()

    updated = 0

    for row in csv_rows:
        if row["order_id"]:
            continue

        matched_id = fuzzy_match(row, db_records)
        if not matched_id:
            continue

        line_idx = row["line_index"]
        cols     = lines[line_idx].rstrip("\r\n").split(",")

        while len(cols) <= order_id_col:
            cols.append("")

        cols[order_id_col] = matched_id
        lines[line_idx]    = ",".join(cols) + "\n"
        updated += 1

        print(f"  Backfilled {row['symbol']} line {line_idx + 1} → OrderId {matched_id}")

    if updated > 0:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.writelines(lines)

    return updated


# -- Output --

def _fmt_date(dt) -> str:
    if dt is None:
        return "?"
    try:
        return dt.astimezone(EASTERN_TIME).strftime("%Y-%m-%d")
    except Exception:
        return str(dt)


def print_report(
    options_rows: list[dict],
    stocks_rows: list[dict],
    db_records: dict[str, dict],
    discrepancies: list[dict],
) -> None:
    db_open   = sum(1 for r in db_records.values() if r["pnl"] is None)
    db_closed = len(db_records) - db_open
    opt_open  = sum(1 for r in options_rows if r["status"] == "Open")
    opt_close = sum(1 for r in options_rows if r["status"] == "Closed")
    opt_no_id = sum(1 for r in options_rows if not r["order_id"])
    stk_open  = sum(1 for r in stocks_rows  if r["status"] == "Open")
    stk_close = sum(1 for r in stocks_rows  if r["status"] == "Closed")
    stk_no_id = sum(1 for r in stocks_rows  if not r["order_id"])

    print()
    print("Vela CSV Reconciliation")
    print("=" * 52)
    print(f"DB records:  {len(db_records):3}  |  {db_closed:3} closed  |  {db_open:3} open")
    print(f"Options CSV: {len(options_rows):3}  |  {opt_close:3} closed  |  {opt_open:3} open"
          f"  |  {opt_no_id} without OrderId")
    print(f"Stocks CSV:  {len(stocks_rows):3}  |  {stk_close:3} closed  |  {stk_open:3} open"
          f"  |  {stk_no_id} without OrderId")
    print()

    if not discrepancies:
        print("No discrepancies found. ✓")
        return

    csv_closed_total = sum(1 for r in options_rows + stocks_rows if r["status"] == "Closed")
    db_only_closed   = sum(1 for d in discrepancies
                           if d["type"] == DB_ONLY and "open" not in d["detail"])

    if csv_closed_total == 0 and db_only_closed > 0:
        print("Note: Working CSV has no closed rows — weekly archive has likely run.")
        print(f"      {db_only_closed} DB_ONLY closed position(s) are expected after archive.")
        print("      To verify closed positions, check the archive file directly.")
        print()

    print(f"Discrepancies ({len(discrepancies)}):")
    print("-" * 52)

    by_type = {}
    for d in discrepancies:
        by_type.setdefault(d["type"], []).append(d)

    type_order = [ORPHANED_OPEN, MISSING_CLOSE, PNL_MISMATCH, CSV_ONLY, DB_ONLY, UNMATCHED_ROW]
    for t in type_order:
        for d in by_type.get(t, []):
            tag    = f"[{d['type']}]"
            oid    = f"  OrderId {d['order_id']}" if d["order_id"] else ""
            print(f"  {tag:<16}  {d['symbol']:<8}{oid}")
            print(f"    {d['detail']}")

    unmatched = len(by_type.get(UNMATCHED_ROW, []))
    if unmatched > 0:
        print()
        print(f"  Run with --backfill to write OrderIds to matchable pre-migration rows.")


def write_report_csv(output_path: Path, discrepancies: list[dict]) -> None:
    with open(output_path, "w", newline="", encoding="utf-8") as f:
        w = csv_module.writer(f)
        w.writerow(["Type", "Symbol", "OrderId", "Detail"])
        for d in discrepancies:
            w.writerow([d["type"], d["symbol"], d.get("order_id", ""), d["detail"]])


# -- Main --

def main():
    parser = argparse.ArgumentParser(
        description="Vela CSV vs trade_metrics reconciliation")
    parser.add_argument("--options",       required=True, help="Path to options_trades.csv")
    parser.add_argument("--stocks",        required=True, help="Path to stocks_trades.csv")
    parser.add_argument("--backfill",      action="store_true",
                        help="Write missing OrderIds to CSV rows with a unique fuzzy match")
    parser.add_argument("--output",        help="Also write the discrepancy report to this CSV file")
    parser.add_argument("--pnl-threshold", type=float, default=1.0,
                        help="P&L difference in dollars below which mismatches are ignored (default: 1.00)")
    parser.add_argument("--since", type=date.fromisoformat, default=None,
                        help="Only check DB records opened on or after this date (YYYY-MM-DD). "
                             "Use Monday of the current week to avoid DB_ONLY noise from archived closed positions.")
    args = parser.parse_args()

    global PNL_TOLERANCE
    PNL_TOLERANCE = Decimal(str(args.pnl_threshold))

    options_path = Path(args.options)
    stocks_path  = Path(args.stocks)

    conn = get_connection()
    try:
        db_records = fetch_db_records(conn, since=args.since)
    finally:
        conn.close()

    db_options = {k: v for k, v in db_records.items()
                  if (v.get("trade_type") or "").lower() == "options"}
    db_stocks  = {k: v for k, v in db_records.items()
                  if (v.get("trade_type") or "").lower() == "stock"}

    options_rows = parse_csv(options_path, is_options=True)
    stocks_rows  = parse_csv(stocks_path,  is_options=False)

    opt_issues, opt_matched = reconcile(options_rows, db_options)
    stk_issues, stk_matched = reconcile(stocks_rows,  db_stocks)

    all_discrepancies = (
        opt_issues + stk_issues +
        find_db_only(db_options, opt_matched) +
        find_db_only(db_stocks,  stk_matched)
    )

    print_report(options_rows, stocks_rows, db_records, all_discrepancies)

    if args.backfill:
        print()
        print("Backfilling missing OrderIds...")
        n = (backfill_order_ids(options_path, options_rows, db_options, is_options=True) +
             backfill_order_ids(stocks_path,  stocks_rows,  db_stocks,  is_options=False))
        print(f"Done — {n} row(s) updated.")

    if args.output:
        write_report_csv(Path(args.output), all_discrepancies)
        print(f"\nReport written → {args.output}")


if __name__ == "__main__":
    main()