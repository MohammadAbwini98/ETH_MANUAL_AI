-- Recommended clean-baseline reset for the current April 11, 2026 situation.
-- Purpose:
--   1. Preserve signal ground truth (`signals`, `signal_outcomes`)
--   2. Repair stale OPEN signal statuses that already have outcomes
--   3. Archive noisy ML / decision history created before the recent fixes
--   4. Clear ML runtime history so the dashboard and retraining start fresh
--
-- Run this only while the app is stopped.
-- Example:
--   psql -v ON_ERROR_STOP=1 -d ETH_BASE -f reset-ml-baseline.sql

BEGIN;

LOCK TABLE
    "ETH".signals,
    "ETH".signal_outcomes,
    "ETH".signal_decision_audit,
    "ETH".ml_feature_snapshots,
    "ETH".ml_predictions,
    "ETH".ml_training_runs,
    "ETH".ml_drift_events,
    "ETH".ml_models
IN ACCESS EXCLUSIVE MODE;

-- Repair stale signal statuses caused by the prior closer bug.
UPDATE "ETH".signals s
SET status = CASE
    WHEN o.outcome_label = 'EXPIRED' THEN 'EXPIRED'
    WHEN o.outcome_label IN ('WIN', 'LOSS', 'AMBIGUOUS') THEN 'CLOSED'
    ELSE s.status
END
FROM "ETH".signal_outcomes o
WHERE o.signal_id = s.signal_id
  AND s.status = 'OPEN'
  AND o.outcome_label IN ('WIN', 'LOSS', 'AMBIGUOUS', 'EXPIRED');

-- Archive tables so nothing is lost.
CREATE TABLE IF NOT EXISTS "ETH".signal_decision_audit_archive
    (LIKE "ETH".signal_decision_audit INCLUDING ALL);
ALTER TABLE "ETH".signal_decision_audit_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".signal_decision_audit_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

CREATE TABLE IF NOT EXISTS "ETH".ml_feature_snapshots_archive
    (LIKE "ETH".ml_feature_snapshots INCLUDING ALL);
ALTER TABLE "ETH".ml_feature_snapshots_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".ml_feature_snapshots_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

CREATE TABLE IF NOT EXISTS "ETH".ml_predictions_archive
    (LIKE "ETH".ml_predictions INCLUDING ALL);
ALTER TABLE "ETH".ml_predictions_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".ml_predictions_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

CREATE TABLE IF NOT EXISTS "ETH".ml_training_runs_archive
    (LIKE "ETH".ml_training_runs INCLUDING ALL);
ALTER TABLE "ETH".ml_training_runs_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".ml_training_runs_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

CREATE TABLE IF NOT EXISTS "ETH".ml_drift_events_archive
    (LIKE "ETH".ml_drift_events INCLUDING ALL);
ALTER TABLE "ETH".ml_drift_events_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".ml_drift_events_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

CREATE TABLE IF NOT EXISTS "ETH".ml_models_archive
    (LIKE "ETH".ml_models INCLUDING ALL);
ALTER TABLE "ETH".ml_models_archive
    ADD COLUMN IF NOT EXISTS archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE "ETH".ml_models_archive
    ADD COLUMN IF NOT EXISTS archive_reason TEXT;

-- Snapshot current noisy ML / decision history before clearing it.
INSERT INTO "ETH".signal_decision_audit_archive
SELECT d.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".signal_decision_audit d
ON CONFLICT DO NOTHING;

INSERT INTO "ETH".ml_feature_snapshots_archive
SELECT f.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".ml_feature_snapshots f
ON CONFLICT DO NOTHING;

INSERT INTO "ETH".ml_predictions_archive
SELECT p.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".ml_predictions p
ON CONFLICT DO NOTHING;

INSERT INTO "ETH".ml_training_runs_archive
SELECT r.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".ml_training_runs r
ON CONFLICT DO NOTHING;

INSERT INTO "ETH".ml_drift_events_archive
SELECT e.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".ml_drift_events e
ON CONFLICT DO NOTHING;

INSERT INTO "ETH".ml_models_archive
SELECT m.*, NOW(), 'Manual archive on 2026-04-11 before clean post-fix baseline reset'
FROM "ETH".ml_models m
ON CONFLICT DO NOTHING;

-- Clear ML + decision-history tables only.
-- Ground-truth signal history remains intact.
TRUNCATE TABLE
    "ETH".ml_predictions,
    "ETH".ml_feature_snapshots,
    "ETH".ml_training_runs,
    "ETH".ml_drift_events,
    "ETH".ml_models,
    "ETH".signal_decision_audit
RESTART IDENTITY;

COMMIT;

-- Quick sanity check after reset.
SELECT 'signals_preserved' AS item, COUNT(*)::BIGINT AS row_count FROM "ETH".signals
UNION ALL
SELECT 'signal_outcomes_preserved', COUNT(*)::BIGINT FROM "ETH".signal_outcomes
UNION ALL
SELECT 'signal_decision_audit_after_reset', COUNT(*)::BIGINT FROM "ETH".signal_decision_audit
UNION ALL
SELECT 'ml_feature_snapshots_after_reset', COUNT(*)::BIGINT FROM "ETH".ml_feature_snapshots
UNION ALL
SELECT 'ml_predictions_after_reset', COUNT(*)::BIGINT FROM "ETH".ml_predictions
UNION ALL
SELECT 'ml_training_runs_after_reset', COUNT(*)::BIGINT FROM "ETH".ml_training_runs
UNION ALL
SELECT 'ml_drift_events_after_reset', COUNT(*)::BIGINT FROM "ETH".ml_drift_events
UNION ALL
SELECT 'ml_models_after_reset', COUNT(*)::BIGINT FROM "ETH".ml_models
ORDER BY item;
