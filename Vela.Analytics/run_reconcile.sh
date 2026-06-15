#!/bin/bash
# Runs the CSV reconciliation tool against the working trade files.
# Any extra args (e.g. --backfill, --output report.csv) are passed through.
#
# Usage:
#   ./run_reconcile.sh
#   ./run_reconcile.sh --backfill
#   ./run_reconcile.sh --output report.csv

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TRADES_DIR="$SCRIPT_DIR/../trades"
cd "$SCRIPT_DIR"
source .venv/bin/activate

python3 csv_reconcile.py \
    --options "$TRADES_DIR/options_trades.csv" \
    --stocks  "$TRADES_DIR/stocks_trades.csv" \
    "$@"