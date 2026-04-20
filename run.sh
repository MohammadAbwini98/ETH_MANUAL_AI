#!/bin/bash

# Guard: exit if already running (pass --restart to override)
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
# Load secrets from .env file (not committed to source control)
if [ -f "$(dirname "$0")/.env" ]; then
    set -a
    source "$(dirname "$0")/.env"
    set +a
fi

# Validate required environment variables
for var in CAPITAL_API_KEY CAPITAL_IDENTIFIER CAPITAL_PASSWORD; do
    if [ -z "${!var}" ]; then
        echo "ERROR: $var is not set. Create a .env file or export it."
        echo "Required: CAPITAL_BASE_URL, CAPITAL_API_KEY, CAPITAL_IDENTIFIER, CAPITAL_PASSWORD, PG_CONNECTION"
        exit 1
    fi
done

export CAPITAL_BASE_URL="${CAPITAL_BASE_URL:-https://demo-api-capital.backend-capital.com}"
export PG_CONNECTION="${PG_CONNECTION:-Host=localhost;Port=5432;Database=ETH_BASE;Username=mohammadabwini}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"

# ─── ML Training Pipeline (runs before app start) ────────────────────────────
ML_DIR="$ROOT_DIR/ml"
if [ -f "$ML_DIR/.venv/bin/python" ]; then
    echo "▸ Running ML training pipeline..."
    (cd "$ML_DIR" && bash train_pipeline.sh) && echo "  ML pipeline complete." || echo "  ML pipeline failed or no data yet — app will use heuristic fallback."
else
    echo "▸ ML venv not found — skipping training. To set up:"
    echo "    cd ml && python3 -m venv .venv && .venv/bin/pip install -r requirements.txt"
fi
echo ""

dotnet run --project src/EthSignal.Web
