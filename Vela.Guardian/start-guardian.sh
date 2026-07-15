#!/bin/bash

# Get the directory where this script lives (Vela.Guardian)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Load environment variables safely
set -a
source "$SCRIPT_DIR/../.env"
set +a

cd "$SCRIPT_DIR"
dotnet run --no-launch-profile
