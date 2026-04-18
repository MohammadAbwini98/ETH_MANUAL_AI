-- SRS Remediation Phase 1 Migration
-- Adds columns for FR-1 (lifecycle), FR-4 (origin), FR-8 (runtime params), FR-16 (evaluation ID)

-- Add new columns to signal_decision_audit
ALTER TABLE "ETH".signal_decision_audit
    ADD COLUMN IF NOT EXISTS lifecycle_state TEXT DEFAULT 'EVALUATED',
    ADD COLUMN IF NOT EXISTS final_block_reason TEXT,
    ADD COLUMN IF NOT EXISTS origin TEXT DEFAULT 'CLOSED_BAR',
    ADD COLUMN IF NOT EXISTS evaluation_id UUID,
    ADD COLUMN IF NOT EXISTS effective_runtime_parameters_json JSONB;

-- Add evaluation_id to signals table for FR-16 correlation
ALTER TABLE "ETH".signals
    ADD COLUMN IF NOT EXISTS evaluation_id UUID;

-- Add inference_mode to ml_predictions for FR-9
ALTER TABLE "ETH".ml_predictions
    ADD COLUMN IF NOT EXISTS inference_mode TEXT DEFAULT 'HEURISTIC_FALLBACK';

-- Index for evaluation_id correlation queries (FR-16)
CREATE INDEX IF NOT EXISTS idx_decision_audit_evaluation_id
    ON "ETH".signal_decision_audit (evaluation_id)
    WHERE evaluation_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_signals_evaluation_id
    ON "ETH".signals (evaluation_id)
    WHERE evaluation_id IS NOT NULL;

-- Index for lifecycle_state filtering (FR-18 rejection funnel)
CREATE INDEX IF NOT EXISTS idx_decision_audit_lifecycle_state
    ON "ETH".signal_decision_audit (lifecycle_state);
