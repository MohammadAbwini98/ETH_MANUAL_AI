#!/bin/bash
# Starts the Signal Platform Control Portal on port 5233

set -euo pipefail

PORT=5233
PORTAL_DIR="$(cd "$(dirname "$0")/portal" && pwd)"
ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ -f "$ROOT_DIR/.env" ]; then
    set -a
    source "$ROOT_DIR/.env"
    set +a
fi

cleanup() {
    echo ""
    echo "Shutting down portal..."
    kill %1 2>/dev/null || true
    exit 0
}
trap cleanup INT TERM

# Guard: exit if already running (pass --restart to override)
if lsof -ti:5235 -sTCP:LISTEN &>/dev/null; then
    if [[ "${1:-}" == "--restart" ]]; then
        echo "Stopping existing Gold process on port 5235..."
        lsof -ti:5235 -sTCP:LISTEN | xargs kill -9 2>/dev/null
        sleep 1
    else
        echo "ERROR: Gold process is already running on port 5235."
        echo "       Use 'bash run_gold.sh --restart' to stop it and restart."
        exit 1
    fi
fi

if lsof -ti:5234 -sTCP:LISTEN &>/dev/null; then
    if [[ "${1:-}" == "--restart" ]]; then
        echo "Stopping existing process on port 5234..."
        lsof -ti:5234 -sTCP:LISTEN | xargs kill -9 2>/dev/null
        sleep 1
    else
        echo "ERROR: App is already running on port 5234."
        echo "       Use 'bash run.sh --restart' to stop it and restart."
        exit 1
    fi
fi

if lsof -ti:$PORT -sTCP:LISTEN &>/dev/null; then
    if [[ "${1:-}" == "--restart" ]]; then
        echo "Stopping existing portal on port $PORT..."
        lsof -ti:$PORT -sTCP:LISTEN | xargs kill -9 2>/dev/null
        sleep 1
    else
        echo "ERROR: Portal already running on port $PORT."
        echo "       Use './run_portal.sh --restart' to restart."
        exit 1
    fi
fi

cd "$PORTAL_DIR"

if [ ! -d "node_modules" ]; then
    echo "Installing portal dependencies..."
    npm install --silent
fi

echo "Signal Portal → http://localhost:$PORT"
(sleep 1.5 && open "http://localhost:$PORT") &
node server.js
