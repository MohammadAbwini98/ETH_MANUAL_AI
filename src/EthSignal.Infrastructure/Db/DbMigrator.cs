using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class DbMigrator : IDbMigrator
{
    private readonly string _connectionString;

    public DbMigrator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await EnsureDatabaseExistsAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await Exec(conn, @"CREATE SCHEMA IF NOT EXISTS ""ETH"";", ct);

        foreach (var tf in Timeframe.All)
        {
            // Detect old schema (column 'open' without 'bid_open') and rename to legacy
            if (await TableExistsAsync(conn, tf.Table, ct)
                && !await ColumnExistsAsync(conn, tf.Table, "bid_open", ct))
            {
                await Exec(conn, $@"ALTER TABLE ""ETH"".{tf.Table} RENAME TO {tf.Table}_legacy;", ct);
            }

            await Exec(conn, $@"
                CREATE TABLE IF NOT EXISTS ""ETH"".{tf.Table} (
                    symbol                 TEXT        NOT NULL,
                    datetime               TIMESTAMPTZ NOT NULL,

                    bid_open               NUMERIC     NOT NULL,
                    bid_high               NUMERIC     NOT NULL,
                    bid_low                NUMERIC     NOT NULL,
                    bid_close              NUMERIC     NOT NULL,

                    ask_open               NUMERIC     NOT NULL,
                    ask_high               NUMERIC     NOT NULL,
                    ask_low                NUMERIC     NOT NULL,
                    ask_close              NUMERIC     NOT NULL,

                    mid_open               NUMERIC     NOT NULL,
                    mid_high               NUMERIC     NOT NULL,
                    mid_low                NUMERIC     NOT NULL,
                    mid_close              NUMERIC     NOT NULL,

                    volume                 NUMERIC     NOT NULL DEFAULT 0,
                    buyer_pct              NUMERIC     NOT NULL DEFAULT 0,
                    seller_pct             NUMERIC     NOT NULL DEFAULT 0,

                    is_closed              BOOLEAN     NOT NULL DEFAULT TRUE,

                    source_timestamp_utc   TIMESTAMPTZ,
                    received_timestamp_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    created_at_utc         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at_utc         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

                    PRIMARY KEY (symbol, datetime)
                );", ct);

            await Exec(conn, $@"
                CREATE INDEX IF NOT EXISTS idx_{tf.Table}_open
                ON ""ETH"".{tf.Table} (symbol, is_closed) WHERE is_closed = false;", ct);
        }

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ui_tick_samples (
                id                  BIGSERIAL   PRIMARY KEY,
                symbol              TEXT        NOT NULL,
                epic                TEXT        NOT NULL,
                tick_time_utc       TIMESTAMPTZ NOT NULL,
                bid                 NUMERIC     NOT NULL,
                ask                 NUMERIC     NOT NULL,
                mid                 NUMERIC     NOT NULL,
                provider_kind       TEXT        NOT NULL,
                received_at_utc     TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ui_tick_samples_symbol_time
            ON ""ETH"".ui_tick_samples (symbol, tick_time_utc DESC);", ct);

        // Audit and gap tables
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ingestion_audit (
                id                  BIGSERIAL   PRIMARY KEY,
                operation           TEXT        NOT NULL,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                period_from         TIMESTAMPTZ,
                period_to           TIMESTAMPTZ,
                candles_fetched     INT         NOT NULL DEFAULT 0,
                candles_inserted    INT         NOT NULL DEFAULT 0,
                candles_updated     INT         NOT NULL DEFAULT 0,
                duplicates_skipped  INT         NOT NULL DEFAULT 0,
                validation_errors   INT         NOT NULL DEFAULT 0,
                duration_ms         BIGINT      NOT NULL DEFAULT 0,
                success             BOOLEAN     NOT NULL DEFAULT TRUE,
                error_message       TEXT,
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".gap_events (
                id                  BIGSERIAL   PRIMARY KEY,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                expected_time       TIMESTAMPTZ NOT NULL,
                actual_next_time    TIMESTAMPTZ,
                gap_duration_sec    INT         NOT NULL,
                gap_type            TEXT        NOT NULL,
                gap_source          TEXT        NOT NULL DEFAULT 'LIVE',
                detected_at_utc     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                resolved            BOOLEAN     NOT NULL DEFAULT FALSE,
                resolved_at_utc     TIMESTAMPTZ,
                UNIQUE (symbol, timeframe, expected_time)
            );", ct);

        // U-04: Add resolved_at_utc column to gap_events if missing
        if (await TableExistsAsync(conn, "gap_events", ct)
            && !await ColumnExistsAsync(conn, "gap_events", "resolved_at_utc", ct))
        {
            await Exec(conn, @"ALTER TABLE ""ETH"".gap_events ADD COLUMN resolved_at_utc TIMESTAMPTZ;", ct);
        }

        // REQ-NS-001: Add gap_source column to gap_events if missing
        if (await TableExistsAsync(conn, "gap_events", ct)
            && !await ColumnExistsAsync(conn, "gap_events", "gap_source", ct))
        {
            await Exec(conn, @"ALTER TABLE ""ETH"".gap_events ADD COLUMN gap_source TEXT NOT NULL DEFAULT 'LIVE';", ct);
        }

        // Phase 2: Indicator snapshots
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".indicator_snapshots (
                symbol                TEXT        NOT NULL,
                timeframe             TEXT        NOT NULL,
                candle_open_time_utc  TIMESTAMPTZ NOT NULL,

                ema20                 NUMERIC     NOT NULL DEFAULT 0,
                ema50                 NUMERIC     NOT NULL DEFAULT 0,
                rsi14                 NUMERIC     NOT NULL DEFAULT 0,

                macd                  NUMERIC     NOT NULL DEFAULT 0,
                macd_signal           NUMERIC     NOT NULL DEFAULT 0,
                macd_hist             NUMERIC     NOT NULL DEFAULT 0,

                atr14                 NUMERIC     NOT NULL DEFAULT 0,
                adx14                 NUMERIC     NOT NULL DEFAULT 0,
                plus_di               NUMERIC     NOT NULL DEFAULT 0,
                minus_di              NUMERIC     NOT NULL DEFAULT 0,

                volume_sma20          NUMERIC     NOT NULL DEFAULT 0,
                vwap                  NUMERIC     NOT NULL DEFAULT 0,
                spread                NUMERIC     NOT NULL DEFAULT 0,
                close_mid             NUMERIC     NOT NULL DEFAULT 0,

                mid_high              NUMERIC     NOT NULL DEFAULT 0,
                mid_low               NUMERIC     NOT NULL DEFAULT 0,

                is_provisional        BOOLEAN     NOT NULL DEFAULT FALSE,
                created_at_utc        TIMESTAMPTZ NOT NULL DEFAULT NOW(),

                PRIMARY KEY (symbol, timeframe, candle_open_time_utc)
            );", ct);

        // Schema migration: add mid_high / mid_low if missing from pre-existing tables
        if (await TableExistsAsync(conn, "indicator_snapshots", ct)
            && !await ColumnExistsAsync(conn, "indicator_snapshots", "mid_high", ct))
        {
            await Exec(conn, @"ALTER TABLE ""ETH"".indicator_snapshots ADD COLUMN IF NOT EXISTS mid_high NUMERIC NOT NULL DEFAULT 0;", ct);
            await Exec(conn, @"ALTER TABLE ""ETH"".indicator_snapshots ADD COLUMN IF NOT EXISTS mid_low  NUMERIC NOT NULL DEFAULT 0;", ct);
        }

        // Phase 3: Regime snapshots
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".regime_snapshots (
                symbol                  TEXT        NOT NULL,
                candle_open_time_utc    TIMESTAMPTZ NOT NULL,
                regime                  TEXT        NOT NULL,
                regime_score            INT         NOT NULL DEFAULT 0,
                triggered_conditions    TEXT        NOT NULL DEFAULT '',
                disqualifying_conditions TEXT       NOT NULL DEFAULT '',
                created_at_utc          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

                PRIMARY KEY (symbol, candle_open_time_utc)
            );", ct);

        // Phase 4-6: Signals and outcomes
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".signals (
                signal_id           UUID        PRIMARY KEY,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                signal_time_utc     TIMESTAMPTZ NOT NULL,
                direction           TEXT        NOT NULL,
                entry_price         NUMERIC     NOT NULL DEFAULT 0,
                tp_price            NUMERIC     NOT NULL DEFAULT 0,
                sl_price            NUMERIC     NOT NULL DEFAULT 0,
                risk_percent        NUMERIC     NOT NULL DEFAULT 0,
                risk_usd            NUMERIC     NOT NULL DEFAULT 0,
                confidence_score    INT         NOT NULL DEFAULT 0,
                regime              TEXT        NOT NULL DEFAULT 'NEUTRAL',
                strategy_version    TEXT        NOT NULL DEFAULT 'v1.0',
                reasons_json        JSONB       NOT NULL DEFAULT '[]'::jsonb,
                status              TEXT        NOT NULL DEFAULT 'OPEN',
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_signals_symbol_time
            ON ""ETH"".signals (symbol, signal_time_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".signal_outcomes (
                signal_id           UUID        PRIMARY KEY REFERENCES ""ETH"".signals(signal_id),
                evaluated_at_utc    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                bars_observed       INT         NOT NULL DEFAULT 0,
                tp_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                sl_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                partial_win         BOOLEAN     NOT NULL DEFAULT FALSE,
                outcome_label       TEXT        NOT NULL DEFAULT 'PENDING',
                pnl_r               NUMERIC     NOT NULL DEFAULT 0,
                mfe_price           NUMERIC     NOT NULL DEFAULT 0,
                mae_price           NUMERIC     NOT NULL DEFAULT 0,
                mfe_r               NUMERIC     NOT NULL DEFAULT 0,
                mae_r               NUMERIC     NOT NULL DEFAULT 0,
                closed_at_utc       TIMESTAMPTZ
            );", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".blocked_signal_outcomes (
                decision_id         UUID        PRIMARY KEY,
                evaluation_id       UUID        NOT NULL UNIQUE,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                signal_time_utc     TIMESTAMPTZ NOT NULL,
                evaluated_at_utc    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                bars_observed       INT         NOT NULL DEFAULT 0,
                tp_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                sl_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                partial_win         BOOLEAN     NOT NULL DEFAULT FALSE,
                outcome_label       TEXT        NOT NULL DEFAULT 'PENDING',
                pnl_r               NUMERIC     NOT NULL DEFAULT 0,
                mfe_price           NUMERIC     NOT NULL DEFAULT 0,
                mae_price           NUMERIC     NOT NULL DEFAULT 0,
                mfe_r               NUMERIC     NOT NULL DEFAULT 0,
                mae_r               NUMERIC     NOT NULL DEFAULT 0,
                closed_at_utc       TIMESTAMPTZ,
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_blocked_signal_outcomes_symbol_time
            ON ""ETH"".blocked_signal_outcomes (symbol, signal_time_utc DESC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_blocked_signal_outcomes_label
            ON ""ETH"".blocked_signal_outcomes (outcome_label, evaluated_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".generated_signal_outcomes (
                decision_id         UUID        PRIMARY KEY,
                evaluation_id       UUID        NOT NULL UNIQUE,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                signal_time_utc     TIMESTAMPTZ NOT NULL,
                evaluated_at_utc    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                bars_observed       INT         NOT NULL DEFAULT 0,
                tp_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                sl_hit              BOOLEAN     NOT NULL DEFAULT FALSE,
                partial_win         BOOLEAN     NOT NULL DEFAULT FALSE,
                outcome_label       TEXT        NOT NULL DEFAULT 'PENDING',
                pnl_r               NUMERIC     NOT NULL DEFAULT 0,
                mfe_price           NUMERIC     NOT NULL DEFAULT 0,
                mae_price           NUMERIC     NOT NULL DEFAULT 0,
                mfe_r               NUMERIC     NOT NULL DEFAULT 0,
                mae_r               NUMERIC     NOT NULL DEFAULT 0,
                closed_at_utc       TIMESTAMPTZ,
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_generated_signal_outcomes_symbol_time
            ON ""ETH"".generated_signal_outcomes (symbol, signal_time_utc DESC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_generated_signal_outcomes_label
            ON ""ETH"".generated_signal_outcomes (outcome_label, evaluated_at_utc DESC);", ct);

        await Exec(conn, @"ALTER TABLE ""ETH"".signal_outcomes ADD COLUMN IF NOT EXISTS partial_win BOOLEAN NOT NULL DEFAULT FALSE;", ct);
        await Exec(conn, @"ALTER TABLE ""ETH"".blocked_signal_outcomes ADD COLUMN IF NOT EXISTS partial_win BOOLEAN NOT NULL DEFAULT FALSE;", ct);
        await Exec(conn, @"ALTER TABLE ""ETH"".generated_signal_outcomes ADD COLUMN IF NOT EXISTS partial_win BOOLEAN NOT NULL DEFAULT FALSE;", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".executed_trades (
                executed_trade_id         BIGSERIAL   PRIMARY KEY,
                signal_id                 UUID        NOT NULL,
                evaluation_id             UUID,
                source_type               TEXT        NOT NULL,
                symbol                    TEXT        NOT NULL,
                instrument                TEXT        NOT NULL,
                timeframe                 TEXT        NOT NULL,
                direction                 TEXT        NOT NULL,
                recommended_entry_price   NUMERIC     NOT NULL DEFAULT 0,
                actual_entry_price        NUMERIC     NOT NULL DEFAULT 0,
                tp_price                  NUMERIC     NOT NULL DEFAULT 0,
                sl_price                  NUMERIC     NOT NULL DEFAULT 0,
                requested_size            NUMERIC     NOT NULL DEFAULT 0,
                executed_size             NUMERIC     NOT NULL DEFAULT 0,
                deal_reference            TEXT,
                deal_id                   TEXT,
                status                    TEXT        NOT NULL DEFAULT 'Pending',
                account_id                TEXT        NOT NULL DEFAULT '',
                account_name              TEXT        NOT NULL DEFAULT '',
                is_demo                   BOOLEAN     NOT NULL DEFAULT FALSE,
                account_currency          TEXT        NOT NULL DEFAULT '',
                opened_at_utc             TIMESTAMPTZ,
                closed_at_utc             TIMESTAMPTZ,
                pnl                       NUMERIC,
                failure_reason            TEXT,
                error_details             TEXT,
                force_closed              BOOLEAN     NOT NULL DEFAULT FALSE,
                close_source              TEXT,
                created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"ALTER TABLE ""ETH"".executed_trades ADD COLUMN IF NOT EXISTS account_id TEXT NOT NULL DEFAULT '';", ct);
        await Exec(conn, @"ALTER TABLE ""ETH"".executed_trades ADD COLUMN IF NOT EXISTS account_name TEXT NOT NULL DEFAULT '';", ct);
        await Exec(conn, @"ALTER TABLE ""ETH"".executed_trades ADD COLUMN IF NOT EXISTS is_demo BOOLEAN NOT NULL DEFAULT FALSE;", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_executed_trades_source_signal
            ON ""ETH"".executed_trades (signal_id, source_type, created_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_executed_trades_status
            ON ""ETH"".executed_trades (status, created_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".trade_execution_queue (
                queue_entry_id            BIGSERIAL   PRIMARY KEY,
                signal_id                 UUID        NOT NULL,
                evaluation_id             UUID,
                source_type               TEXT        NOT NULL,
                requested_by              TEXT        NOT NULL DEFAULT 'system',
                requested_size            NUMERIC,
                force_market_execution    BOOLEAN     NOT NULL DEFAULT FALSE,
                candidate_json            JSONB       NOT NULL DEFAULT '{}'::jsonb,
                status                    TEXT        NOT NULL DEFAULT 'Queued',
                executed_trade_id         BIGINT      REFERENCES ""ETH"".executed_trades(executed_trade_id) ON DELETE SET NULL,
                failure_reason            TEXT,
                error_details             TEXT,
                created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                processed_at_utc          TIMESTAMPTZ
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_trade_execution_queue_status
            ON ""ETH"".trade_execution_queue (status, created_at_utc ASC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_trade_execution_queue_signal
            ON ""ETH"".trade_execution_queue (signal_id, source_type, created_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".execution_attempts (
                attempt_id                BIGSERIAL   PRIMARY KEY,
                executed_trade_id         BIGINT      REFERENCES ""ETH"".executed_trades(executed_trade_id) ON DELETE SET NULL,
                signal_id                 UUID        NOT NULL,
                source_type               TEXT        NOT NULL,
                attempt_type              TEXT        NOT NULL,
                success                   BOOLEAN     NOT NULL DEFAULT FALSE,
                summary                   TEXT,
                error_details             TEXT,
                broker_payload            TEXT,
                created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_execution_attempts_trade
            ON ""ETH"".execution_attempts (executed_trade_id, created_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".execution_events (
                event_id                  BIGSERIAL   PRIMARY KEY,
                executed_trade_id         BIGINT      REFERENCES ""ETH"".executed_trades(executed_trade_id) ON DELETE SET NULL,
                signal_id                 UUID        NOT NULL,
                source_type               TEXT        NOT NULL,
                event_type                TEXT        NOT NULL,
                message                   TEXT        NOT NULL,
                details_json              JSONB       NOT NULL DEFAULT '{}'::jsonb,
                created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_execution_events_trade
            ON ""ETH"".execution_events (executed_trade_id, created_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".account_snapshots (
                snapshot_id               BIGSERIAL   PRIMARY KEY,
                account_id                TEXT        NOT NULL,
                account_name              TEXT        NOT NULL DEFAULT '',
                currency                  TEXT        NOT NULL,
                balance                   NUMERIC     NOT NULL DEFAULT 0,
                equity                    NUMERIC     NOT NULL DEFAULT 0,
                available                 NUMERIC     NOT NULL DEFAULT 0,
                margin                    NUMERIC     NOT NULL DEFAULT 0,
                funds                     NUMERIC     NOT NULL DEFAULT 0,
                open_positions            INT         NOT NULL DEFAULT 0,
                is_demo                   BOOLEAN     NOT NULL DEFAULT FALSE,
                hedging_mode              BOOLEAN     NOT NULL DEFAULT FALSE,
                captured_at_utc           TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"ALTER TABLE ""ETH"".account_snapshots ADD COLUMN IF NOT EXISTS account_name TEXT NOT NULL DEFAULT '';", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_account_snapshots_time
            ON ""ETH"".account_snapshots (captured_at_utc DESC);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".close_trade_actions (
                close_action_id           BIGSERIAL   PRIMARY KEY,
                executed_trade_id         BIGINT      NOT NULL REFERENCES ""ETH"".executed_trades(executed_trade_id) ON DELETE CASCADE,
                requested_by              TEXT        NOT NULL,
                reason                    TEXT,
                success                   BOOLEAN     NOT NULL DEFAULT FALSE,
                message                   TEXT        NOT NULL,
                deal_reference            TEXT,
                deal_id                   TEXT,
                close_level               NUMERIC,
                pnl                       NUMERIC,
                created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_close_trade_actions_trade
            ON ""ETH"".close_trade_actions (executed_trade_id, created_at_utc DESC);", ct);

        // B-14: Migrate reasons_json from TEXT to JSONB
        if (await ColumnExistsAsync(conn, "signals", "reasons_json", ct))
        {
            await Exec(conn, @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'ETH' AND table_name = 'signals'
                          AND column_name = 'reasons_json'
                          AND data_type = 'text'
                    ) THEN
                        -- Drop old default first
                        ALTER TABLE ""ETH"".signals ALTER COLUMN reasons_json DROP DEFAULT;
                        -- Convert existing pipe-delimited text to JSON arrays
                        UPDATE ""ETH"".signals
                        SET reasons_json = CASE
                            WHEN reasons_json = '' THEN '[]'
                            ELSE (SELECT jsonb_agg(elem)::text FROM unnest(string_to_array(reasons_json, '|')) AS elem)
                        END;
                        -- Change column type
                        ALTER TABLE ""ETH"".signals ALTER COLUMN reasons_json TYPE JSONB USING reasons_json::jsonb;
                        ALTER TABLE ""ETH"".signals ALTER COLUMN reasons_json SET DEFAULT '[]'::jsonb;
                    END IF;
                END $$;", ct);
        }

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".signal_features (
                signal_id           UUID        NOT NULL REFERENCES ""ETH"".signals(signal_id),
                feature_name        TEXT        NOT NULL,
                feature_value_numeric NUMERIC,
                feature_value_text  TEXT,
                PRIMARY KEY (signal_id, feature_name)
            );", ct);

        // B-07: Strategy parameter sets
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".strategy_parameter_sets (
                id                          BIGSERIAL   PRIMARY KEY,
                strategy_version            TEXT        NOT NULL,
                parameter_hash              TEXT        NOT NULL,
                parameters_json             JSONB       NOT NULL,
                status                      TEXT        NOT NULL DEFAULT 'Draft',
                created_utc                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                created_by                  TEXT,
                activated_utc               TIMESTAMPTZ,
                retired_utc                 TIMESTAMPTZ,
                notes                       TEXT,
                parent_parameter_set_id     BIGINT      REFERENCES ""ETH"".strategy_parameter_sets(id),
                objective_function_version  TEXT,
                code_version                TEXT,
                UNIQUE (parameter_hash)
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_strategy_params_active
            ON ""ETH"".strategy_parameter_sets (strategy_version, status)
            WHERE status = 'Active';", ct);

        // B-07: Parameter activation history
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".parameter_activation_history (
                id                  BIGSERIAL   PRIMARY KEY,
                parameter_set_id    BIGINT      NOT NULL REFERENCES ""ETH"".strategy_parameter_sets(id),
                previous_set_id     BIGINT      REFERENCES ""ETH"".strategy_parameter_sets(id),
                activated_utc       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                activated_by        TEXT,
                promotion_reason    TEXT,
                rollback            BOOLEAN     NOT NULL DEFAULT FALSE
            );", ct);

        // B-07: Replay runs
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".replay_runs (
                id                      BIGSERIAL   PRIMARY KEY,
                symbol                  TEXT        NOT NULL,
                timeframe_base          TEXT        NOT NULL DEFAULT '1m',
                timeframe_primary       TEXT        NOT NULL DEFAULT '5m',
                timeframe_bias          TEXT        NOT NULL DEFAULT '15m',
                start_utc               TIMESTAMPTZ NOT NULL,
                end_utc                 TIMESTAMPTZ NOT NULL,
                parameter_set_id        BIGINT      REFERENCES ""ETH"".strategy_parameter_sets(id),
                strategy_version        TEXT        NOT NULL,
                mode                    TEXT        NOT NULL DEFAULT 'historical_rebuild',
                status                  TEXT        NOT NULL DEFAULT 'queued',
                started_utc             TIMESTAMPTZ,
                finished_utc            TIMESTAMPTZ,
                candles_read_count      INT         DEFAULT 0,
                signals_generated_count INT         DEFAULT 0,
                outcomes_finalized_count INT        DEFAULT 0,
                gap_event_count         INT         DEFAULT 0,
                warnings_json           JSONB       DEFAULT '[]'::jsonb,
                error_text              TEXT,
                code_version            TEXT,
                trigger_source          TEXT        DEFAULT 'manual',
                checkpoint_time         TIMESTAMPTZ
            );", ct);

        // B-07: Optimizer runs
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".optimizer_runs (
                id                          BIGSERIAL   PRIMARY KEY,
                symbol                      TEXT        NOT NULL,
                strategy_version            TEXT        NOT NULL,
                baseline_parameter_set_id   BIGINT      REFERENCES ""ETH"".strategy_parameter_sets(id),
                search_space_json           JSONB,
                objective_function_version  TEXT,
                start_utc                   TIMESTAMPTZ NOT NULL,
                end_utc                     TIMESTAMPTZ NOT NULL,
                status                      TEXT        NOT NULL DEFAULT 'queued',
                run_mode                    TEXT        DEFAULT 'manual',
                fold_count                  INT         DEFAULT 3,
                candidate_count             INT         DEFAULT 0,
                best_candidate_id           BIGINT,
                best_score                  NUMERIC,
                started_utc                 TIMESTAMPTZ,
                finished_utc                TIMESTAMPTZ,
                summary_json                JSONB,
                error_text                  TEXT
            );", ct);

        // B-07: Optimizer candidates
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".optimizer_candidates (
                id                      BIGSERIAL   PRIMARY KEY,
                optimizer_run_id        BIGINT      NOT NULL REFERENCES ""ETH"".optimizer_runs(id),
                parameter_set_id        BIGINT      NOT NULL REFERENCES ""ETH"".strategy_parameter_sets(id),
                status                  TEXT        NOT NULL DEFAULT 'pending',
                train_score             NUMERIC,
                validation_score        NUMERIC,
                baseline_delta_pct      NUMERIC,
                trade_count             INT,
                win_rate                NUMERIC,
                expectancy_r            NUMERIC,
                total_pnl_r             NUMERIC,
                profit_factor           NUMERIC,
                max_drawdown_r          NUMERIC,
                timeout_rate            NUMERIC,
                overfit_penalty         NUMERIC,
                sparsity_penalty        NUMERIC,
                rank                    INT,
                summary_json            JSONB
            );", ct);

        // B-07: Optimizer candidate folds
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".optimizer_candidate_folds (
                id                      BIGSERIAL   PRIMARY KEY,
                optimizer_candidate_id  BIGINT      NOT NULL REFERENCES ""ETH"".optimizer_candidates(id),
                fold_index              INT         NOT NULL,
                train_start_utc         TIMESTAMPTZ NOT NULL,
                train_end_utc           TIMESTAMPTZ NOT NULL,
                val_start_utc           TIMESTAMPTZ NOT NULL,
                val_end_utc             TIMESTAMPTZ NOT NULL,
                train_metrics_json      JSONB,
                val_metrics_json        JSONB,
                warnings_json           JSONB
            );", ct);

        // TF-2 / DB-1: Signal decision audit — stores ALL evaluation outcomes including NO_TRADE
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".signal_decision_audit (
                id                  UUID        PRIMARY KEY,
                symbol              TEXT        NOT NULL,
                decision_time_utc   TIMESTAMPTZ NOT NULL,
                bar_time_utc        TIMESTAMPTZ NOT NULL,
                timeframe           TEXT        NOT NULL,
                decision_type       TEXT        NOT NULL,
                outcome_category    TEXT        NOT NULL,
                regime              TEXT,
                regime_bar_time_utc TIMESTAMPTZ,
                parameter_set_id    TEXT,
                confidence_score    INT         NOT NULL DEFAULT 0,
                reason_codes_json   JSONB       NOT NULL DEFAULT '[]'::jsonb,
                reason_details_json JSONB       NOT NULL DEFAULT '[]'::jsonb,
                indicators_json     JSONB       NOT NULL DEFAULT '{}'::jsonb,
                source_mode         TEXT        NOT NULL DEFAULT 'LIVE',
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(symbol, timeframe, bar_time_utc, source_mode)
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_signal_decision_audit_time
            ON ""ETH"".signal_decision_audit (symbol, decision_time_utc DESC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_signal_decision_audit_type
            ON ""ETH"".signal_decision_audit (symbol, decision_type);", ct);

        await Exec(conn, @"
            ALTER TABLE ""ETH"".signal_decision_audit
            ADD COLUMN IF NOT EXISTS candidate_direction TEXT;", ct);

        await Exec(conn, @"
            UPDATE ""ETH"".signal_decision_audit
            SET candidate_direction = decision_type
            WHERE candidate_direction IS NULL
              AND decision_type IN ('BUY', 'SELL');", ct);

        // ─── ML Enhancement Tables ──────────────────────────────────────

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ml_feature_snapshots (
                evaluation_id    UUID        PRIMARY KEY,
                signal_id        UUID        REFERENCES ""ETH"".signals(signal_id),
                symbol           TEXT        NOT NULL,
                timeframe        TEXT        NOT NULL,
                timestamp_utc    TIMESTAMPTZ NOT NULL,
                features_json    JSONB       NOT NULL,
                feature_version  TEXT        NOT NULL,
                link_status      TEXT        NOT NULL DEFAULT 'PENDING',
                created_at_utc   TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            ALTER TABLE ""ETH"".ml_feature_snapshots
            ADD COLUMN IF NOT EXISTS link_status TEXT NOT NULL DEFAULT 'PENDING';", ct);

        await Exec(conn, @"
            UPDATE ""ETH"".ml_feature_snapshots
            SET link_status = CASE
                WHEN signal_id IS NOT NULL THEN 'SIGNAL_LINKED'
                WHEN created_at_utc < NOW() - INTERVAL '24 hours' THEN 'NO_SIGNAL_EXPECTED'
                ELSE 'PENDING'
            END
            WHERE link_status IS NULL
               OR link_status = ''
               OR link_status = 'PENDING';", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_features_time
            ON ""ETH"".ml_feature_snapshots(timestamp_utc);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_features_signal
            ON ""ETH"".ml_feature_snapshots(signal_id);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_features_link_status
            ON ""ETH"".ml_feature_snapshots(link_status);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ml_predictions (
                prediction_id              UUID        PRIMARY KEY,
                evaluation_id              UUID        NOT NULL
                    REFERENCES ""ETH"".ml_feature_snapshots(evaluation_id),
                signal_id                  UUID        REFERENCES ""ETH"".signals(signal_id),
                model_version              TEXT        NOT NULL,
                model_type                 TEXT        NOT NULL,
                predicted_win_probability  NUMERIC(6,4) NOT NULL,
                calibrated_confidence      INT         NOT NULL,
                raw_win_probability        NUMERIC(6,4),
                calibrated_win_probability NUMERIC(6,4),
                prediction_confidence      INT,
                recommended_threshold      INT         NOT NULL,
                expected_value_r           NUMERIC(8,4) NOT NULL,
                inference_latency_us       INT         NOT NULL,
                is_active                  BOOLEAN     NOT NULL,
                mode                       TEXT        NOT NULL,
                created_at_utc             TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            ALTER TABLE ""ETH"".ml_predictions
            ADD COLUMN IF NOT EXISTS raw_win_probability NUMERIC(6,4);", ct);

        await Exec(conn, @"
            ALTER TABLE ""ETH"".ml_predictions
            ADD COLUMN IF NOT EXISTS calibrated_win_probability NUMERIC(6,4);", ct);

        await Exec(conn, @"
            ALTER TABLE ""ETH"".ml_predictions
            ADD COLUMN IF NOT EXISTS prediction_confidence INT;", ct);

        await Exec(conn, @"
            UPDATE ""ETH"".ml_predictions
            SET raw_win_probability = COALESCE(raw_win_probability, predicted_win_probability),
                calibrated_win_probability = COALESCE(calibrated_win_probability, predicted_win_probability),
                prediction_confidence = COALESCE(prediction_confidence, calibrated_confidence)
            WHERE raw_win_probability IS NULL
               OR calibrated_win_probability IS NULL
               OR prediction_confidence IS NULL;", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_pred_time
            ON ""ETH"".ml_predictions(created_at_utc);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_pred_signal
            ON ""ETH"".ml_predictions(signal_id);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_pred_model
            ON ""ETH"".ml_predictions(model_version);", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ml_models (
                id                      BIGSERIAL   PRIMARY KEY,
                model_type              TEXT        NOT NULL,
                model_version           TEXT        NOT NULL UNIQUE,
                file_path               TEXT        NOT NULL,
                file_format             TEXT        NOT NULL,
                train_start_utc         TIMESTAMPTZ NOT NULL,
                train_end_utc           TIMESTAMPTZ NOT NULL,
                training_sample_count   INT         NOT NULL,
                feature_count           INT         NOT NULL,
                feature_list_json       JSONB       NOT NULL,
                auc_roc                 NUMERIC(6,4),
                brier_score             NUMERIC(6,4),
                ece                     NUMERIC(6,4),
                log_loss                NUMERIC(8,4),
                fold_metrics_json       JSONB,
                feature_importance_json JSONB,
                status                  TEXT        NOT NULL DEFAULT 'candidate',
                created_at_utc          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                activated_at_utc        TIMESTAMPTZ,
                retired_at_utc          TIMESTAMPTZ,
                retired_reason          TEXT
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_models_status
            ON ""ETH"".ml_models(status);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_models_type
            ON ""ETH"".ml_models(model_type, status);", ct);

        // ENH-1: Regime-specific sub-models — add regime_scope column if missing
        await Exec(conn, @"
            ALTER TABLE ""ETH"".ml_models
            ADD COLUMN IF NOT EXISTS regime_scope VARCHAR(10) NOT NULL DEFAULT 'ALL';", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_ml_models_type_scope_status
            ON ""ETH"".ml_models (model_type, regime_scope, status)
            WHERE LOWER(status) = 'active';", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ml_training_runs (
                id                  BIGSERIAL   PRIMARY KEY,
                model_type          TEXT        NOT NULL,
                trigger             TEXT        NOT NULL,
                data_start_utc      TIMESTAMPTZ NOT NULL,
                data_end_utc        TIMESTAMPTZ NOT NULL,
                sample_count        INT,
                fold_count          INT         NOT NULL,
                embargo_bars        INT         NOT NULL,
                status              TEXT        NOT NULL DEFAULT 'running',
                result_model_id     BIGINT      REFERENCES ""ETH"".ml_models(id),
                metrics_json        JSONB,
                error_text          TEXT,
                started_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                finished_at_utc     TIMESTAMPTZ,
                duration_seconds    INT
            );", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".ml_drift_events (
                id                  BIGSERIAL   PRIMARY KEY,
                model_version       TEXT        NOT NULL,
                metric_name         TEXT        NOT NULL,
                metric_value        NUMERIC(8,4) NOT NULL,
                threshold           NUMERIC(8,4) NOT NULL,
                window_size         INT         NOT NULL,
                action_taken        TEXT        NOT NULL,
                detected_at_utc     TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        // Startup historical candle sync — per-timeframe state for empty bootstrap and offline gap recovery
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".candle_sync_status (
                symbol                      TEXT        NOT NULL,
                timeframe                   TEXT        NOT NULL,
                status                      TEXT        NOT NULL,
                sync_mode                   TEXT        NOT NULL,
                is_table_empty              BOOLEAN     NOT NULL DEFAULT FALSE,
                requested_from_utc          TIMESTAMPTZ,
                requested_to_utc            TIMESTAMPTZ,
                last_existing_candle_utc    TIMESTAMPTZ,
                last_synced_candle_utc      TIMESTAMPTZ,
                offline_duration_sec        BIGINT      NOT NULL DEFAULT 0,
                chunk_size_candles          INT         NOT NULL DEFAULT 0,
                chunks_total                INT         NOT NULL DEFAULT 0,
                chunks_completed            INT         NOT NULL DEFAULT 0,
                last_run_started_at_utc     TIMESTAMPTZ,
                last_run_finished_at_utc    TIMESTAMPTZ,
                last_success_at_utc         TIMESTAMPTZ,
                last_error                  TEXT,
                updated_at_utc              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (symbol, timeframe)
            );", ct);

        // ─── Adaptive Parameter System tables ───────────
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".adaptive_parameter_log (
                id                      BIGSERIAL       PRIMARY KEY,
                logged_utc              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
                bar_time_utc            TIMESTAMPTZ     NOT NULL,
                condition_class         TEXT            NOT NULL,
                volatility_tier         TEXT,
                trend_strength          TEXT,
                trading_session         TEXT,
                spread_quality          TEXT,
                volume_tier             TEXT,
                base_confidence_buy     INT,
                adapted_confidence_buy  INT,
                base_confidence_sell    INT,
                adapted_confidence_sell INT,
                overlay_deltas_json     JSONB,
                clamping_events_json    JSONB,
                atr14                   DECIMAL,
                atr_sma50               DECIMAL,
                adx14                   DECIMAL,
                regime_score            INT,
                spread_pct              DECIMAL
            );", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_adaptive_log_time
            ON ""ETH"".adaptive_parameter_log (bar_time_utc DESC);", ct);

        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_adaptive_log_condition
            ON ""ETH"".adaptive_parameter_log (condition_class, bar_time_utc DESC);", ct);

        // Add market_condition_class to signals table
        await Exec(conn, @"
            ALTER TABLE ""ETH"".signals
            ADD COLUMN IF NOT EXISTS market_condition_class TEXT;", ct);

        // Add market_condition_class and adapted_parameters_json to signal_decision_audit table
        await Exec(conn, @"
            ALTER TABLE ""ETH"".signal_decision_audit
            ADD COLUMN IF NOT EXISTS market_condition_class TEXT;", ct);
        await Exec(conn, @"
            ALTER TABLE ""ETH"".signal_decision_audit
            ADD COLUMN IF NOT EXISTS adapted_parameters_json JSONB;", ct);

        // ─── Issue #3: Restart-safe adaptive state ───────────
        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".adaptive_condition_outcomes (
                condition_key      TEXT        PRIMARY KEY,
                outcomes_json      JSONB       NOT NULL,
                outcome_count      INT         NOT NULL DEFAULT 0,
                last_updated_utc   TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        await Exec(conn, @"
            CREATE TABLE IF NOT EXISTS ""ETH"".adaptive_retrospective_overlays (
                condition_key  TEXT        PRIMARY KEY,
                overlay_json   JSONB       NOT NULL,
                updated_utc    TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);

        // ─── Issue #12: parameter_activation_history dedup ───
        // Prevent duplicate activations of the same parameter set within a 1-second
        // window (covers concurrent startups). We dedup at write-time in the
        // repository as well; this index defends against bursts of identical inserts.
        await Exec(conn, @"
            CREATE INDEX IF NOT EXISTS idx_param_activation_history_set
            ON ""ETH"".parameter_activation_history (parameter_set_id, activated_utc DESC);", ct);
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var dbName = builder.Database!;
        builder.Database = "postgres";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync(ct);

        await using var check = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @db", conn);
        check.Parameters.AddWithValue("db", dbName);

        if (await check.ExecuteScalarAsync(ct) == null)
        {
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{dbName}\"", conn);
            await create.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'ETH' AND table_name = @t", conn);
        cmd.Parameters.AddWithValue("t", table);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'ETH' AND table_name = @t AND column_name = @c", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
