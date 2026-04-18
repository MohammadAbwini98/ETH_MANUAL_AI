#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROFILE_DIR="$ROOT/logs/capital-chrome-profile"
LOG_FILE="$ROOT/logs/bootstrap-chrome.log"
URL="https://capital.com/trading/platform/trade"
CHROME_BIN="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

mkdir -p "$PROFILE_DIR"
mkdir -p "$ROOT/logs"

if [[ ! -x "$CHROME_BIN" ]]; then
  echo "Google Chrome not found at: $CHROME_BIN"
  exit 1
fi

if pgrep -f "Google Chrome.*--user-data-dir=$PROFILE_DIR" >/dev/null 2>&1; then
  echo "Chrome is already running with this automation profile:"
  echo "  $PROFILE_DIR"
  echo "Close that Chrome window first, then run this script again."
  exit 0
fi

echo "Launching real Chrome with persistent profile: $PROFILE_DIR"
echo "1) Log in to Capital.com with Google"
echo "2) Confirm account is visible"
echo "3) Close this Chrome window"
echo "After that, run: node playwright-inspect.js"
echo "Chrome runtime logs are redirected to: $LOG_FILE"

: > "$LOG_FILE"

"$CHROME_BIN" \
  --user-data-dir="$PROFILE_DIR" \
  --profile-directory="Default" \
  --no-first-run \
  --no-default-browser-check \
  "$URL" \
  >"$LOG_FILE" 2>&1
