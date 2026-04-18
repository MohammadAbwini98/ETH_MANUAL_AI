#!/usr/bin/env bash
# Export all ETH schema tables to CSV files.
# Usage: ./export-db.sh [output_dir] [row_limit]
# row_limit: 0 or omit for all rows, positive integer for cap (default: 0 = all)

set -euo pipefail

# Locate psql
if command -v psql &>/dev/null; then
  PSQL="psql"
else
  PSQL=$(find /usr/local/Cellar/postgresql*/*/bin /usr/local/bin /opt/homebrew/bin -name "psql" -type f 2>/dev/null | head -1 || true)
fi

if [ -z "${PSQL:-}" ]; then
  echo "ERROR: psql not found. Install PostgreSQL or add it to PATH."
  exit 1
fi

DB_NAME="ETH_BASE"
DB_SCHEMA="ETH"
OUTPUT_DIR="${1:-$(pwd)/db_export_$(date +%Y%m%d_%H%M%S)}"
ROW_LIMIT="${2:-0}"

TABLES=(
candles_1h
candles_1m
candles_4h
candles_5m
candles_15m
candles_30m
gap_events
indicator_snapshots
ingestion_audit
ml_drift_events
ml_feature_snapshots
ml_predictions
ml_training_runs
optimizer_candidate_folds
optimizer_candidates
optimizer_runs
parameter_activation_history
regime_snapshots
replay_runs
signal_decision_audit
signal_features
signal_outcomes
signals
strategy_parameter_sets
ui_tick_samples
)

mkdir -p "$OUTPUT_DIR"

echo "Using psql: ${PSQL}"
echo "Exporting ${#TABLES[@]} tables from ${DB_NAME}.\"${DB_SCHEMA}\" to ${OUTPUT_DIR}/"
echo ""

for table in "${TABLES[@]}"; do
  outfile="${OUTPUT_DIR}/${table}.csv"
  count=$("$PSQL" -d "$DB_NAME" -tAc "SELECT COUNT(*) FROM \"${DB_SCHEMA}\".${table};" 2>&1) || count="0"
  count=$(echo "$count" | tr -d '[:space:]')

  if [ "$ROW_LIMIT" -gt 0 ] 2>/dev/null; then
    LIMIT_CLAUSE="LIMIT ${ROW_LIMIT}"
  else
    LIMIT_CLAUSE=""
  fi

  if "$PSQL" -d "$DB_NAME" -c "\COPY (SELECT * FROM \"${DB_SCHEMA}\".${table} ORDER BY 1 DESC ${LIMIT_CLAUSE}) TO '${outfile}' WITH CSV HEADER;" 2>&1; then
    if [ "$ROW_LIMIT" -gt 0 ] 2>/dev/null; then
      exported=$(( count < ROW_LIMIT ? count : ROW_LIMIT ))
    else
      exported="$count"
    fi
    echo "  ${table}: ${exported}/${count} rows -> $(basename "$outfile")"
  else
    echo "  ${table}: FAILED (table may not exist yet)"
  fi
done

echo ""
echo "Done. Files saved to ${OUTPUT_DIR}/"
