#!/bin/bash
# ML Training Pipeline — Full workflow
#
# Exports features from DB, trains all models, validates, and registers.
#
# Prerequisites:
#   python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
#   export PG_CONNECTION="Host=...;Port=5432;Database=...;Username=...;Password=..."
#
# Usage:
#   cd ml/
#   bash train_pipeline.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# ─── Load .env from project root if PG_CONNECTION not already set ─────────────
ROOT_ENV="$SCRIPT_DIR/../.env"
if [ -z "${PG_CONNECTION:-}" ] && [ -f "$ROOT_ENV" ]; then
    set -a
    # shellcheck disable=SC1090
    source "$ROOT_ENV"
    set +a
    echo "Loaded .env from $ROOT_ENV"
fi

if [ -z "${PG_CONNECTION:-}" ]; then
    echo "ERROR: PG_CONNECTION is not set. Add it to $ROOT_ENV or export it before running."
    exit 1
fi

DATA_DIR="data"
MODEL_DIR="models"
FEATURE_VERSION="${ML_FEATURE_VERSION:-v3.0}"
mkdir -p "$DATA_DIR" "$MODEL_DIR"

# ─── Resolve Python: prefer venv, fall back to system python3 ────────────────
if [ -f "$SCRIPT_DIR/.venv/bin/python" ]; then
    PYTHON="$SCRIPT_DIR/.venv/bin/python"
    echo "Using venv Python: $PYTHON"
elif command -v python3 &>/dev/null; then
    PYTHON="python3"
    echo "WARNING: .venv not found — using system python3. Run:"
    echo "  python3 -m venv .venv && .venv/bin/pip install -r requirements.txt"
else
    echo "ERROR: No Python found. Install Python 3.10+ and create a venv."
    exit 1
fi

echo "═══════════════════════════════════════════════════════"
echo " ETH/USD ML Training Pipeline"
echo " $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo " Python: $($PYTHON --version)"
echo "═══════════════════════════════════════════════════════"
echo ""

# Step 1: Export features
echo "▸ Step 1: Exporting training features..."
# Remove stale CSV so a failed/empty export does not leave old data on disk
rm -f "$DATA_DIR/training_data.csv"
"$PYTHON" export_features.py \
    --output "$DATA_DIR/training_data.csv" \
    --feature-version "$FEATURE_VERSION" \
    --max-proximity-seconds "${ML_MAX_PROXIMITY_SECONDS:-180}" \
    --max-proximity-per-signal "${ML_MAX_PROXIMITY_PER_SIGNAL:-1}" \
    --fallback-recency-days "${ML_FALLBACK_RECENCY_DAYS:-7}" \
    --proximity-tiers "${ML_PROXIMITY_TIERS:-180,120,90}" \
    --max-fallback-ratio "${ML_MAX_FALLBACK_TO_LINKED_RATIO:-3.0}" \
    --min-linked-for-hard-cap "${ML_MIN_LINKED_FOR_HARD_CAP:-150}" \
    --auto-fallback-threshold "${ML_AUTO_FALLBACK_THRESHOLD:-200}"

# Show the structured export summary so diagnostics and training are
# looking at the same "trainable labeled samples" definition.
SUMMARY_FILE="$DATA_DIR/training_data_summary.json"
if [ -f "$SUMMARY_FILE" ]; then
    echo ""
    echo "  Export summary ($SUMMARY_FILE):"
    if command -v python3 &>/dev/null; then
        python3 - <<PYEOF || true
import json
with open("$SUMMARY_FILE") as f:
    s = json.load(f)
for k in ("direct_linked_rows","generated_rows","fallback_rows","auto_fallback_triggered",
          "blocked_rows","dropped_no_trade_rows","wins","losses","total_rows","feature_version"):
    print(f"    {k:>22s}: {s.get(k)}")
tf = s.get("timeframe_distribution") or {}
if tf:
    print(f"    {'timeframe_distribution':>22s}: {tf}")
PYEOF
    fi
fi

if [ ! -f "$DATA_DIR/training_data.csv" ]; then
    echo "  INFO: No training data available yet — skipping training."
    echo "  The app will use its heuristic fallback until enough signals accumulate."
    echo "  Keep the app running in SHADOW/ACTIVE mode; re-run this pipeline periodically."
    exit 2
fi

SAMPLE_COUNT=$(wc -l < "$DATA_DIR/training_data.csv")
ACTUAL_SAMPLES="$((SAMPLE_COUNT - 1))"
echo "  → $ACTUAL_SAMPLES samples available"
if [ "$ACTUAL_SAMPLES" -lt 200 ]; then
    echo ""
    echo "  WARNING: $ACTUAL_SAMPLES samples found — acceptance criteria require 200+."
    echo "  Training will proceed but the model will FAIL validation and cannot be promoted."
    echo "  Action: keep the app running in SHADOW mode to accumulate more labeled signals."
    echo "  Each signal outcome is labeled ~4 bars (~20 min) after signal generation."
    echo ""
fi
echo ""

# Step 2: Train outcome predictor (M1)
# Exit codes: 0=success (new model written), 2=skip (not enough data), other=error
echo "▸ Step 2: Training outcome predictor (M1)..."
set +e
"$PYTHON" train_outcome_predictor.py \
    --data "$DATA_DIR/training_data.csv" \
    --output "$MODEL_DIR/" \
    --folds 5 \
    --model lightgbm
TRAIN_EXIT=$?
set -e
if [ "$TRAIN_EXIT" -eq 2 ]; then
    echo "  INFO: Training skipped — trainer reported insufficient or unusable data."
    echo "  Skipping remaining steps — will retry when more data accumulates."
    exit 2
elif [ "$TRAIN_EXIT" -ne 0 ]; then
    echo "  ERROR: Training failed (exit $TRAIN_EXIT)."
    exit 1
fi
echo ""

# Find the latest model artifacts
LATEST_META=$(ls -t "$MODEL_DIR"/outcome_predictor_*_meta.json 2>/dev/null | head -1 || true)
if [ -z "$LATEST_META" ]; then
    echo "  ERROR: No model metadata found — training produced no output."
    exit 1
fi

LATEST_MODEL=$(ls -t "$MODEL_DIR"/outcome_predictor_*.onnx 2>/dev/null | head -1 || true)
if [ -z "$LATEST_MODEL" ]; then
    LATEST_MODEL=$(ls -t "$MODEL_DIR"/outcome_predictor_*.pkl 2>/dev/null | head -1 || true)
fi

echo "  Latest meta:  $LATEST_META"
echo "  Latest model: ${LATEST_MODEL:-N/A}"
echo ""

# Step 3: Validate model (fatal — invalid artifacts must stop the pipeline)
echo "▸ Step 3: Validating model..."
if [ -f validate_model.py ]; then
    "$PYTHON" validate_model.py --meta "$LATEST_META"
else
    echo "  SKIPPED: validate_model.py not found"
fi
echo ""

# Step 4: Train recalibrator (M2)
echo "▸ Step 4: Training confidence recalibrator (M2)..."
if [ -f train_recalibrator.py ] && [ -n "$LATEST_MODEL" ]; then
    "$PYTHON" train_recalibrator.py \
        --data "$DATA_DIR/training_data.csv" \
        --model "$LATEST_MODEL" \
        --output "$MODEL_DIR/" || \
        echo "  WARNING: Recalibrator skipped (too few samples) — using uncalibrated model."
else
    echo "  SKIPPED: train_recalibrator.py not found or no model file"
fi
echo ""

# Step 5: Train threshold lookup (M3)
echo "▸ Step 5: Training dynamic threshold model (M3)..."
if [ -f train_threshold_model.py ]; then
    "$PYTHON" train_threshold_model.py \
        --data "$DATA_DIR/training_data.csv" \
        --output "$MODEL_DIR/" \
        --blend-weight "${ML_CONFIDENCE_BLEND_WEIGHT:-0.5}"
else
    echo "  SKIPPED: train_threshold_model.py not found"
fi
echo ""

# Step 6: Register model in DB
# Uses register_model.py (direct DB write) — does NOT require the app to be running.
echo "▸ Step 6: Registering model in DB..."
if [ -f register_model.py ]; then
    "$PYTHON" register_model.py --meta "$LATEST_META"
else
    echo "  SKIPPED: register_model.py not found"
fi
echo ""

# ─── Steps 7–9: Regime-specific sub-models (Recommendation 2) ─────────────────
# Train one specialist per regime (BEARISH / BULLISH / NEUTRAL).
# Each is independently validated and registered; failures are non-fatal so that
# insufficient data in one regime does not block the global model from running.
echo "▸ Steps 7–9: Training regime-specific specialists..."
for REGIME in BEARISH BULLISH NEUTRAL; do
    echo ""
    echo "  ── Regime: $REGIME ──"

    # Train
    set +e
    "$PYTHON" train_outcome_predictor.py \
        --data "$DATA_DIR/training_data.csv" \
        --output "$MODEL_DIR/" \
        --folds 5 \
        --model lightgbm \
        --regime "$REGIME"
    REGIME_TRAIN_EXIT=$?
    set -e

    if [ "$REGIME_TRAIN_EXIT" -eq 2 ]; then
        echo "  INFO: $REGIME specialist skipped — insufficient data (< 50 samples). Continuing."
        continue
    elif [ "$REGIME_TRAIN_EXIT" -ne 0 ]; then
        echo "  WARN: $REGIME specialist training failed (exit $REGIME_TRAIN_EXIT). Continuing with global model only."
        continue
    fi

    # Find the latest regime artifact
    REGIME_LOWER=$(echo "$REGIME" | tr '[:upper:]' '[:lower:]')
    REGIME_META=$(ls -t "$MODEL_DIR"/outcome_predictor_${REGIME}_*_meta.json 2>/dev/null | head -1 || true)
    if [ -z "$REGIME_META" ]; then
        echo "  WARN: No $REGIME meta file found after training — skipping registration."
        continue
    fi

    # Recalibrate regime specialist if train_recalibrator.py exists
    REGIME_MODEL=$(ls -t "$MODEL_DIR"/outcome_predictor_${REGIME}_*.onnx 2>/dev/null | head -1 || true)
    if [ -f train_recalibrator.py ] && [ -n "$REGIME_MODEL" ]; then
        "$PYTHON" train_recalibrator.py \
            --data "$DATA_DIR/training_data.csv" \
            --model "$REGIME_MODEL" \
            --output "$MODEL_DIR/" \
            --regime "$REGIME" || \
            echo "  INFO: $REGIME recalibrator skipped (too few samples)."
    fi

    # Register regime specialist in DB (non-fatal — quality-gate rejections skip the specialist only)
    if [ -f register_model.py ]; then
        set +e
        "$PYTHON" register_model.py --meta "$REGIME_META" --regime "$REGIME"
        REGIME_REG_EXIT=$?
        set -e
        if [ "$REGIME_REG_EXIT" -ne 0 ]; then
            echo "  INFO: $REGIME specialist not registered (quality gate or DB error, exit $REGIME_REG_EXIT) — continuing."
        fi
    fi
done
echo ""

echo "═══════════════════════════════════════════════════════"
echo " Pipeline complete!"
echo " Models saved to: $MODEL_DIR/"
echo ""
echo " Next steps:"
echo "   1. Global model + any regime specialists auto-registered in DB"
echo "   2. Verify in SHADOW mode via dashboard ML panel"
echo "   3. Monitor 50+ signals in shadow"
echo "   4. Activate: POST /api/admin/ml/models/{id}/activate"
echo "   5. Regime specialists load automatically — no extra activation needed."
echo "      Each regime uses its specialist if active; falls back to global model."
echo "═══════════════════════════════════════════════════════"
