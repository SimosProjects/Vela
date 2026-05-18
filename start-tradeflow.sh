#!/bin/bash

# Get the directory where this script lives (solution root)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Load environment variables safely
set -a
source "$SCRIPT_DIR/.env"
set +a

# Get current time in ET
ET_HOUR=$(TZ="America/New_York" date +%-H)
ET_MIN=$(TZ="America/New_York" date +%-M)
ET_DOW=$(TZ="America/New_York" date +%u)  # 1=Monday, 7=Sunday
ET_TIME=$((ET_HOUR * 60 + ET_MIN))

MARKET_OPEN=$((9 * 60 + 25))   # 9:25am ET
MARKET_CLOSE=$((16 * 60 + 5))  # 4:05pm ET

echo "Current ET time: ${ET_HOUR}:${ET_MIN}"
echo "Day of week: ${ET_DOW} (1=Mon, 5=Fri, 6=Sat, 7=Sun)"

# Check if weekend
if [ "$ET_DOW" -ge 6 ]; then
    echo "Market is closed — weekend. Use --force to override."
    if [ "$1" != "--force" ]; then
        exit 0
    fi
fi

# Check if market hours
if [ "$ET_TIME" -lt "$MARKET_OPEN" ] || [ "$ET_TIME" -gt "$MARKET_CLOSE" ]; then
    echo "Market is closed. Hours are 9:30am-4:00pm ET Mon-Fri."
    echo "Use --force to override."
    if [ "$1" != "--force" ]; then
        exit 0
    fi
fi

echo "Starting TradeFlow..."

# Start PostgreSQL via Docker if not running
docker compose -f "$SCRIPT_DIR/docker-compose.yml" up -d postgres
echo "Waiting for PostgreSQL..."
sleep 5

# Schedule auto-stop at market close
if [ "$1" != "--force" ]; then
    SECONDS_UNTIL_CLOSE=$(( (MARKET_CLOSE - ET_TIME) * 60 ))
    echo "Will auto-stop in ${SECONDS_UNTIL_CLOSE} seconds (4:05pm ET)"
    (sleep $SECONDS_UNTIL_CLOSE && pkill -f "TradeFlow.Worker" && echo "Market closed — TradeFlow stopped.") &
fi

# Start Worker with caffeinate to prevent Mac sleep
cd "$SCRIPT_DIR/TradeFlow.Worker"
caffeinate -d dotnet run
