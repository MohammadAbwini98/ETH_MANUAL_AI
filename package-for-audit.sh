#!/bin/bash
# Creates a clean ZIP of the full solution for audit review.
# Excludes secrets, build outputs, and local metadata.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$HOME/Desktop/ETH_MANUAL_AUDIT_$(date +%Y%m%d_%H%M%S).zip"

cd "$SCRIPT_DIR"

# Export latest DB records
if [ -f export-db.sh ]; then
    echo "Exporting latest DB records..."
    bash export-db.sh 2>/dev/null || echo "  (DB export skipped — psql not available or DB not running)"
fi

echo "Creating audit package..."
zip -r "$OUTPUT" . \
    -x "bin/*" "obj/*" "*/bin/*" "*/obj/*" \
    -x ".DS_Store" "*/.DS_Store" \
    -x ".env" "*.env" \
    -x ".git/*" \
    -x ".claude/*" \
    -x "node_modules/*" \
    -x "*.user" "*.suo" \
    -x "package-for-audit.sh" \
    2>/dev/null

SIZE=$(du -h "$OUTPUT" | cut -f1)
echo ""
echo "Audit package created: $OUTPUT ($SIZE)"
echo ""
echo "Contents include:"
echo "  - src/EthSignal.Domain/     (domain models incl. StrategyParameters, ReplayRun, OptimizerModels)"
echo "  - src/EthSignal.Infrastructure/  (engine: indicators, regime, signal, risk, outcome, replay, optimizer)"
echo "  - src/EthSignal.Web/        (ASP.NET API + dashboard)"
echo "  - tests/EthSignal.Tests/    (146 tests)"
echo "  - ETH_G/                    (legacy prototype — kept for reference)"
echo "  - logs/                     (runtime logs)"
echo "  - db_export_*/              (DB CSV exports if available)"
echo "  - run.sh                    (secrets removed — uses .env)"
