#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPORTS_DIR="$SCRIPT_DIR/../reports/backtest"
cd "$SCRIPT_DIR"
source .venv/bin/activate
python3 backtest.py "$@" --output "$REPORTS_DIR"

# Parse mode and date from args to build the correct summary path
MODE=""
DATE_ARG=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode) MODE="$2"; shift 2 ;;
        --date) DATE_ARG="$2"; shift 2 ;;
        *) shift ;;
    esac
done

# Default date to today if not provided
if [ -z "$DATE_ARG" ]; then
    DATE_ARG=$(date +%Y-%m-%d)
fi

# Calculate Monday of the week for the week folder name
MONDAY=$(python3 -c "
from datetime import date, timedelta
d = date.fromisoformat('$DATE_ARG')
print(d - timedelta(days=d.weekday()))
")

if [ "$MODE" = "daily" ]; then
    SUMMARY_DIR="$REPORTS_DIR/week_$MONDAY/daily/$DATE_ARG"
else
    SUMMARY_DIR="$REPORTS_DIR/week_$MONDAY"
fi

python3 backtest_summary.py --dir "$SUMMARY_DIR"