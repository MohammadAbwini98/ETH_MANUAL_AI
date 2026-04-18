#!/usr/bin/env bash
# register_model.sh — Register a trained ML model in the DB via the API.
#
# Usage:
#   bash register_model.sh <meta_json_path>
#   bash register_model.sh models/outcome_predictor_v20260403_120000_meta.json
#
# The API endpoint only accepts localhost connections (security guard).
# Run this on the same machine as the EthSignal web server.

set -euo pipefail

META_FILE="${1:-}"

if [ -z "$META_FILE" ]; then
    # Auto-detect latest meta file
    SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
    META_FILE=$(ls -t "$SCRIPT_DIR/models"/outcome_predictor_*_meta.json 2>/dev/null | head -1 || true)
    if [ -z "$META_FILE" ]; then
        echo "ERROR: No meta file found. Pass path as argument or run train_pipeline.sh first."
        exit 1
    fi
    echo "Auto-detected: $META_FILE"
fi

if [ ! -f "$META_FILE" ]; then
    echo "ERROR: Meta file not found: $META_FILE"
    exit 1
fi

API_BASE="${API_BASE:-http://localhost:5234}"
REGISTER_URL="$API_BASE/api/admin/ml/models/register"

echo "Registering model from: $META_FILE"
echo "Endpoint: $REGISTER_URL"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
    -X POST "$REGISTER_URL" \
    -H "Content-Type: application/json" \
    -d @"$META_FILE")

HTTP_BODY=$(echo "$RESPONSE" | sed '$d')
HTTP_CODE=$(echo "$RESPONSE" | tail -n 1)

echo "Response ($HTTP_CODE): $HTTP_BODY"

if [ "$HTTP_CODE" -eq 200 ]; then
    echo ""
    # Extract model ID from response
    MODEL_ID=$(echo "$HTTP_BODY" | grep -o '"id":[0-9]*' | grep -o '[0-9]*' || echo "unknown")
    echo "Model registered successfully with id=$MODEL_ID"
    echo ""
    echo "Next steps:"
    echo "  1. Verify shadow predictions in dashboard ML panel"
    echo "  2. After 50+ bars, activate: curl -X POST $API_BASE/api/admin/ml/models/$MODEL_ID/activate"
else
    echo "ERROR: Registration failed (HTTP $HTTP_CODE)"
    exit 1
fi
