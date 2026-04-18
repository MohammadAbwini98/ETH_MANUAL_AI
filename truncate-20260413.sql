-- Full reset: candles + signals + ML tables.
-- Backup taken at: backups/ETH_BASE_backup_20260413_215557.dump
-- Preserves: strategy_parameter_sets, parameter_activation_history, candle_sync_status, *_archive tables.
-- Run while app is stopped.

BEGIN;

LOCK TABLE
    "ETH".candles_1m,
    "ETH".candles_5m,
    "ETH".candles_15m,
    "ETH".candles_30m,
    "ETH".candles_1h,
    "ETH".candles_4h,
    "ETH".signals,
    "ETH".signal_outcomes,
    "ETH".signal_features,
    "ETH".signal_decision_audit,
    "ETH".ml_feature_snapshots,
    "ETH".ml_predictions,
    "ETH".ml_training_runs,
    "ETH".ml_drift_events,
    "ETH".ml_models,
    "ETH".indicator_snapshots,
    "ETH".regime_snapshots,
    "ETH".gap_events,
    "ETH".ingestion_audit,
    "ETH".ui_tick_samples,
    "ETH".optimizer_runs,
    "ETH".optimizer_candidates,
    "ETH".optimizer_candidate_folds,
    "ETH".replay_runs
IN ACCESS EXCLUSIVE MODE;

-- ── All target tables in a single CASCADE truncate ─────────────
-- CASCADE handles foreign-key order automatically.
TRUNCATE TABLE
    "ETH".candles_1m,
    "ETH".candles_5m,
    "ETH".candles_15m,
    "ETH".candles_30m,
    "ETH".candles_1h,
    "ETH".candles_4h,
    "ETH".signals,
    "ETH".signal_outcomes,
    "ETH".signal_features,
    "ETH".signal_decision_audit,
    "ETH".ml_feature_snapshots,
    "ETH".ml_predictions,
    "ETH".ml_training_runs,
    "ETH".ml_drift_events,
    "ETH".ml_models,
    "ETH".indicator_snapshots,
    "ETH".regime_snapshots,
    "ETH".gap_events,
    "ETH".ingestion_audit,
    "ETH".ui_tick_samples,
    "ETH".optimizer_runs,
    "ETH".optimizer_candidates,
    "ETH".optimizer_candidate_folds,
    "ETH".replay_runs
RESTART IDENTITY CASCADE;

COMMIT;

-- ── Sanity check ────────────────────────────────────────────────
SELECT 'strategy_parameter_sets (kept)'   AS item, COUNT(*)::bigint AS rows FROM "ETH".strategy_parameter_sets
UNION ALL
SELECT 'parameter_activation_history (kept)',       COUNT(*) FROM "ETH".parameter_activation_history
UNION ALL
SELECT 'candle_sync_status (kept)',                 COUNT(*) FROM "ETH".candle_sync_status
UNION ALL
SELECT 'candles_1m (truncated)',                    COUNT(*) FROM "ETH".candles_1m
UNION ALL
SELECT 'candles_5m (truncated)',                    COUNT(*) FROM "ETH".candles_5m
UNION ALL
SELECT 'signals (truncated)',                       COUNT(*) FROM "ETH".signals
UNION ALL
SELECT 'signal_outcomes (truncated)',               COUNT(*) FROM "ETH".signal_outcomes
UNION ALL
SELECT 'ml_feature_snapshots (truncated)',          COUNT(*) FROM "ETH".ml_feature_snapshots
UNION ALL
SELECT 'ml_predictions (truncated)',                COUNT(*) FROM "ETH".ml_predictions
UNION ALL
SELECT 'indicator_snapshots (truncated)',           COUNT(*) FROM "ETH".indicator_snapshots
UNION ALL
SELECT 'regime_snapshots (truncated)',              COUNT(*) FROM "ETH".regime_snapshots
UNION ALL
SELECT 'ui_tick_samples (truncated)',               COUNT(*) FROM "ETH".ui_tick_samples
ORDER BY item;
