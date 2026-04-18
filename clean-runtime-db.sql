-- Clean runtime/training data while preserving system configuration tables.
-- Safe to use for a fresh data rebuild when you want to clear candles, signals,
-- ML artifacts, optimizer history, and sync state without deleting parameter sets.
--
-- Preserved on purpose:
--   - "ETH".strategy_parameter_sets
--   - "ETH".parameter_activation_history
--   - any *_archive tables
--
-- Also note:
--   - "ETH".candle_sync_status is truncated intentionally because it becomes
--     inconsistent if candle tables are wiped but sync state is left behind.
--   - "ETH".ml_models is truncated intentionally because this is a full ML reset.
--
-- Example:
--   psql -v ON_ERROR_STOP=1 -d ETH_BASE -f clean-runtime-db.sql
--
-- Run while the app is stopped.

BEGIN;

DO $$
DECLARE
    target_tables TEXT[] := ARRAY[
        'candles_1m',
        'candles_5m',
        'candles_15m',
        'candles_30m',
        'candles_1h',
        'candles_4h',
        'ui_tick_samples',
        'ingestion_audit',
        'gap_events',
        'indicator_snapshots',
        'regime_snapshots',
        'signals',
        'signal_outcomes',
        'signal_features',
        'signal_decision_audit',
        'ml_feature_snapshots',
        'ml_predictions',
        'ml_training_runs',
        'ml_drift_events',
        'replay_runs',
        'optimizer_candidate_folds',
        'optimizer_candidates',
        'optimizer_runs',
        'candle_sync_status'
    ];
    truncate_sql TEXT;
BEGIN
    SELECT string_agg(format('%I.%I', 'ETH', table_name), ', ')
    INTO truncate_sql
    FROM unnest(target_tables) AS table_name
    WHERE to_regclass(format('%I.%I', 'ETH', table_name)) IS NOT NULL;

    IF truncate_sql IS NULL THEN
        RAISE EXCEPTION 'No runtime tables were found in schema ETH.';
    END IF;

    EXECUTE 'TRUNCATE TABLE ' || truncate_sql || ' RESTART IDENTITY CASCADE';
END $$;

COMMIT;

-- Quick post-run sanity check.
SELECT 'strategy_parameter_sets_preserved' AS item, COUNT(*)::BIGINT AS row_count
FROM "ETH".strategy_parameter_sets
UNION ALL
SELECT 'parameter_activation_history_preserved', COUNT(*)::BIGINT
FROM "ETH".parameter_activation_history
UNION ALL
SELECT 'candles_1m_after_clean', COUNT(*)::BIGINT
FROM "ETH".candles_1m
UNION ALL
SELECT 'signals_after_clean', COUNT(*)::BIGINT
FROM "ETH".signals
UNION ALL
SELECT 'ml_predictions_after_clean', COUNT(*)::BIGINT
FROM "ETH".ml_predictions
UNION ALL
SELECT 'ml_models_after_clean', COUNT(*)::BIGINT
FROM "ETH".ml_models
UNION ALL
SELECT 'candle_sync_status_after_clean', COUNT(*)::BIGINT
FROM "ETH".candle_sync_status
ORDER BY item;
