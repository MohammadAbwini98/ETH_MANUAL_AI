#!/usr/bin/env bash
# Preview and clean disposable ETH runtime tables without touching configuration.
#
# Preserved:
#   - strategy_parameter_sets
#   - parameter_activation_history
#   - any *_archive tables
#
# Truncated:
#   - candle tables
#   - signal/runtime history
#   - ML feature/prediction/model/training tables
#   - optimizer/replay history
#   - candle_sync_status

set -euo pipefail

if command -v psql >/dev/null 2>&1; then
  PSQL="psql"
else
  PSQL=$(find /usr/local/Cellar/postgresql*/*/bin /usr/local/bin /opt/homebrew/bin -name "psql" -type f 2>/dev/null | head -1 || true)
fi

if [ -z "${PSQL:-}" ]; then
  echo "ERROR: psql not found. Install PostgreSQL or add it to PATH."
  exit 1
fi

DB_NAME="${DB_NAME:-ETH_BASE}"
DB_SCHEMA="${DB_SCHEMA:-ETH}"
AUTO_CONFIRM=0

if [ "${1:-}" = "--yes" ]; then
  AUTO_CONFIRM=1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SQL_FILE="${SCRIPT_DIR}/clean-runtime-db.sql"

if [ ! -f "$SQL_FILE" ]; then
  echo "ERROR: SQL file not found: ${SQL_FILE}"
  exit 1
fi

PRESERVED_TABLES=(
  strategy_parameter_sets
  parameter_activation_history
)

TARGET_TABLES=(
  candles_1m
  candles_5m
  candles_15m
  candles_30m
  candles_1h
  candles_4h
  ui_tick_samples
  ingestion_audit
  gap_events
  indicator_snapshots
  regime_snapshots
  signals
  signal_outcomes
  signal_features
  signal_decision_audit
  ml_feature_snapshots
  ml_predictions
  ml_training_runs
  ml_drift_events
  ml_models
  replay_runs
  optimizer_candidate_folds
  optimizer_candidates
  optimizer_runs
  candle_sync_status
)

count_rows() {
  local table="$1"
  "$PSQL" -d "$DB_NAME" -tAc "SELECT CASE WHEN to_regclass('\"${DB_SCHEMA}\".${table}') IS NULL THEN 'MISSING' ELSE (SELECT COUNT(*)::text FROM \"${DB_SCHEMA}\".${table}) END;" 2>/dev/null | tr -d '[:space:]'
}

echo "Using psql: ${PSQL}"
echo "Database: ${DB_NAME}"
echo "Schema:   ${DB_SCHEMA}"
echo ""
echo "Preserved configuration tables:"
for table in "${PRESERVED_TABLES[@]}"; do
  echo "  ${table}: $(count_rows "$table") rows"
done

echo ""
echo "Runtime tables to be truncated:"
for table in "${TARGET_TABLES[@]}"; do
  echo "  ${table}: $(count_rows "$table") rows"
done

echo ""
echo "Important:"
echo "  - strategy_parameter_sets and parameter_activation_history stay intact"
echo "  - *_archive tables are untouched"
echo "  - candle_sync_status is cleared intentionally so candle backfill can rebuild cleanly"
echo ""
echo "Tip: run ./export-db.sh first if you want a backup snapshot."
echo ""

if [ "$AUTO_CONFIRM" -ne 1 ]; then
  read -rp "Type CLEAN to truncate the runtime tables listed above: " confirm
  if [ "$confirm" != "CLEAN" ]; then
    echo "Aborted."
    exit 0
  fi
fi

echo ""
"$PSQL" -v ON_ERROR_STOP=1 -d "$DB_NAME" -f "$SQL_FILE"

echo ""
echo "Runtime clean completed."
