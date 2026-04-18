#!/usr/bin/env bash
# Backup and truncate ETH schema data tables for a clean fresh start.
# Config tables (strategy_parameter_sets, ml_models) are PRESERVED by default.
# Usage: ./truncate-db.sh [--all]
#   --all  Also truncate strategy_parameter_sets and ml_models (full reset)

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
FULL_RESET=0
if [ "${1:-}" = "--all" ]; then
  FULL_RESET=1
fi

# Tables truncated in dependency order (children before parents)
DATA_TABLES=(
  #optimizer_candidate_folds
  #optimizer_candidates
  #optimizer_runs
  #replay_runs
  #parameter_activation_history
  #ml_predictions
  #ml_feature_snapshots
  #signal_features
  #signal_outcomes
  #signals
  #signal_decision_audit
  #regime_snapshots
  #indicator_snapshots
  #gap_events
  #ingestion_audit
  candles_4h
  candles_1h
  candles_30m
  candles_15m
  candles_5m
  candles_1m
)

CONFIG_TABLES=(
  strategy_parameter_sets
  ml_models
)

if [ "$FULL_RESET" -eq 1 ]; then
  ALL_TABLES=("${CONFIG_TABLES[@]}" "${DATA_TABLES[@]}")
  echo "WARNING: FULL RESET — config tables will also be truncated"
else
  ALL_TABLES=("${DATA_TABLES[@]}")
  echo "INFO: Config tables preserved (strategy_parameter_sets, ml_models)"
  echo "      Pass --all to also reset those."
fi

echo ""
echo "WARNING: This will DELETE data from ${#ALL_TABLES[@]} tables in ${DB_NAME}.\"${DB_SCHEMA}\""
echo ""
for table in "${ALL_TABLES[@]}"; do
  count=$("$PSQL" -d "$DB_NAME" -tAc "SELECT COUNT(*) FROM \"${DB_SCHEMA}\".${table};" 2>&1) || count="?"
  count=$(echo "$count" | tr -d '[:space:]')
  echo "  ${table}: ${count} rows"
done
echo ""

read -rp "Step 1 — Create backup before truncating? (y/n): " do_backup
if [ "$do_backup" = "y" ] || [ "$do_backup" = "Y" ]; then
  BACKUP_DIR="$(pwd)/db_backup_$(date +%Y%m%d_%H%M%S)"
  echo "Running backup to ${BACKUP_DIR}..."
  "$(dirname "$0")/export-db.sh" "$BACKUP_DIR" 0
  echo ""
fi

read -rp "Type YES to truncate: " confirm
if [ "$confirm" != "YES" ]; then
  echo "Aborted."
  exit 0
fi

echo ""
for table in "${ALL_TABLES[@]}"; do
  "$PSQL" -d "$DB_NAME" -c "TRUNCATE TABLE \"${DB_SCHEMA}\".${table} CASCADE;" && echo "  Truncated: ${table}" || echo "  Skipped:   ${table} (may not exist)"
done

echo ""
echo "Done. Data tables truncated. Ready for fresh start."
if [ "$FULL_RESET" -eq 0 ]; then
  echo "NOTE: strategy_parameter_sets and ml_models preserved."
  echo "      The app will auto-seed default parameters on next startup."
fi
