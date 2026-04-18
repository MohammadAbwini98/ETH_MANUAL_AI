# Dashboard Applied Queries

This note reflects the current query/filter logic used by the dashboard after the latest fixes:

- `Decision Summary` is all-time, not `24h`
- `Blocked Signals History` and `Generated Signals History` use the same counting filters as `Decision Summary`
- `ML Data Quality` uses `5m+` scope for the `ALL` view
- `Performance` is all-time and merges `Signal History + Blocked Signals History + Generated Signals History`

## 1. Decision Summary

Endpoint:

```text
GET /api/decisions/summary
```

Applied time range:

```sql
from = '1970-01-01 00:00:00+00'
to   = now()
```

Main query:

```sql
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (
        WHERE outcome_category = 'SIGNAL_GENERATED'
          AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
          AND COALESCE(candidate_direction, decision_type) = 'BUY'
    ) AS long_count,
    COUNT(*) FILTER (
        WHERE outcome_category = 'SIGNAL_GENERATED'
          AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
          AND COALESCE(candidate_direction, decision_type) = 'SELL'
    ) AS short_count,
    COUNT(*) FILTER (
        WHERE outcome_category = 'SIGNAL_GENERATED'
          AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
          AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
    ) AS generated_count,
    COUNT(*) FILTER (
        WHERE lifecycle_state IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
          AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
    ) AS blocked_count,
    COUNT(*) FILTER (WHERE outcome_category = 'STRATEGY_NO_TRADE') AS no_trade_count,
    COUNT(*) FILTER (WHERE outcome_category = 'STRATEGY_NO_TRADE') AS strategy_nt,
    COUNT(*) FILTER (WHERE outcome_category = 'OPERATIONAL_BLOCKED') AS op_blocked,
    COUNT(*) FILTER (WHERE outcome_category = 'CONTEXT_NOT_READY') AS ctx_nr,
    MAX(decision_time_utc) FILTER (
        WHERE outcome_category = 'SIGNAL_GENERATED'
          AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
          AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
    ) AS last_signal_time,
    MAX(decision_time_utc) AS last_eval_time
FROM "ETH".signal_decision_audit
WHERE symbol = @s
  AND decision_time_utc >= @from
  AND decision_time_utc < @to;
```

Top reject reasons query:

```sql
SELECT elem::text AS reason, COUNT(*) AS cnt
FROM "ETH".signal_decision_audit,
     jsonb_array_elements_text(reason_codes_json) AS elem
WHERE symbol = @s
  AND decision_time_utc >= @from
  AND decision_time_utc < @to
  AND jsonb_array_length(reason_codes_json) > 0
GROUP BY reason
ORDER BY cnt DESC
LIMIT 10;
```

## 2. ML Data Quality

Endpoint:

```text
GET /api/admin/ml/diagnostics
```

Scope rule:

- `ALL` means `5m, 15m, 30m, 1h, 4h`
- `1m` is excluded from the `ALL` ML diagnostics scope

### 2.1 Feature version stats

```sql
SELECT
    f.feature_version,
    COUNT(*)::INT AS total_snapshots,
    COUNT(*) FILTER (
        WHERE EXISTS (
            SELECT 1
            FROM "ETH".signal_outcomes o
            WHERE o.signal_id = f.signal_id
              AND o.outcome_label IN ('WIN', 'LOSS')
        )
    )::INT AS labeled_feature_snapshots,
    COUNT(*) FILTER (
        WHERE f.signal_id IS NOT NULL
          AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
          AND EXISTS (
              SELECT 1
              FROM "ETH".signal_outcomes o
              WHERE o.signal_id = f.signal_id
                AND o.outcome_label IN ('WIN', 'LOSS')
          )
    )::INT AS trainable_feature_snapshots,
    MAX(f.created_at_utc)
FROM "ETH".ml_feature_snapshots f
WHERE f.symbol = @sym
  AND f.timeframe = @tf
GROUP BY f.feature_version
ORDER BY MAX(f.created_at_utc) DESC;
```

### 2.2 Signal-history outcome quality

```sql
WITH outcome_stats AS (
    SELECT
        COUNT(o.signal_id)::INT AS total_outcomes,
        COUNT(*) FILTER (WHERE o.outcome_label = 'WIN')::INT AS wins,
        COUNT(*) FILTER (WHERE o.outcome_label = 'LOSS')::INT AS losses,
        COUNT(*) FILTER (WHERE o.outcome_label = 'PENDING')::INT AS pending,
        COUNT(*) FILTER (WHERE o.outcome_label = 'EXPIRED')::INT AS expired,
        COUNT(*) FILTER (WHERE o.outcome_label = 'AMBIGUOUS')::INT AS ambiguous,
        COUNT(*) FILTER (
            WHERE (o.outcome_label = 'WIN' AND o.pnl_r <= 0)
               OR (o.outcome_label = 'LOSS' AND o.pnl_r >= 0)
        )::INT AS inconsistent_pnl_labels,
        COUNT(*) FILTER (WHERE o.tp_hit AND o.sl_hit)::INT AS conflicting_tp_sl_hits,
        COUNT(*) FILTER (
            WHERE o.outcome_label IN ('WIN', 'LOSS', 'EXPIRED', 'AMBIGUOUS')
              AND o.closed_at_utc IS NULL
        )::INT AS closed_timestamp_missing
    FROM "ETH".signals s
    LEFT JOIN "ETH".signal_outcomes o ON o.signal_id = s.signal_id
    WHERE s.symbol = @sym
      AND s.timeframe = @tf
),
feature_stats AS (
    SELECT
        COUNT(*)::INT AS total_feature_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NOT NULL
               OR f.link_status = 'SIGNAL_LINKED'
        )::INT AS linked_feature_snapshots,
        COUNT(*) FILTER (
            WHERE (f.signal_id IS NOT NULL OR f.link_status = 'SIGNAL_LINKED')
              AND EXISTS (
                  SELECT 1
                  FROM "ETH".signal_outcomes o
                  WHERE o.signal_id = f.signal_id
                    AND o.outcome_label IN ('WIN', 'LOSS')
              )
        )::INT AS labeled_feature_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NOT NULL
              AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
              AND EXISTS (
                  SELECT 1
                  FROM "ETH".signal_outcomes o
                  WHERE o.signal_id = f.signal_id
                    AND o.outcome_label IN ('WIN', 'LOSS')
              )
        )::INT AS trainable_feature_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NULL
              AND COALESCE(f.link_status, 'PENDING') = 'PENDING'
        )::INT AS pending_link_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NULL
              AND COALESCE(f.link_status, 'PENDING') = 'PENDING'
              AND f.created_at_utc < NOW() - @stale_interval::interval
        )::INT AS stale_pending_link_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NULL
              AND COALESCE(f.link_status, 'PENDING') = 'NO_SIGNAL_EXPECTED'
        )::INT AS expected_no_signal_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NULL
              AND COALESCE(f.link_status, 'PENDING') = 'ML_FILTERED'
        )::INT AS ml_filtered_snapshots,
        COUNT(*) FILTER (
            WHERE f.signal_id IS NULL
              AND COALESCE(f.link_status, 'PENDING') = 'OPERATIONALLY_BLOCKED'
        )::INT AS operationally_blocked_snapshots
    FROM "ETH".ml_feature_snapshots f
    WHERE f.symbol = @sym
      AND f.timeframe = @tf
      AND f.feature_version = @feature_version
)
SELECT
    o.total_outcomes,
    o.wins,
    o.losses,
    o.pending,
    o.expired,
    o.ambiguous,
    o.inconsistent_pnl_labels,
    o.conflicting_tp_sl_hits,
    o.closed_timestamp_missing,
    f.total_feature_snapshots,
    f.linked_feature_snapshots,
    f.labeled_feature_snapshots,
    f.pending_link_snapshots,
    f.stale_pending_link_snapshots,
    f.expected_no_signal_snapshots,
    f.ml_filtered_snapshots,
    f.operationally_blocked_snapshots,
    f.trainable_feature_snapshots
FROM outcome_stats o
CROSS JOIN feature_stats f;
```

### 2.3 Calibration samples

```sql
SELECT
    p.created_at_utc,
    COALESCE(p.calibrated_win_probability, p.predicted_win_probability),
    p.recommended_threshold,
    p.model_version,
    o.outcome_label
FROM "ETH".ml_predictions p
JOIN "ETH".ml_feature_snapshots f ON f.evaluation_id = p.evaluation_id
JOIN "ETH".signal_outcomes o ON o.signal_id = p.signal_id
WHERE f.symbol = @sym
  AND f.timeframe = @tf
  AND o.outcome_label IN ('WIN', 'LOSS')
  AND p.created_at_utc >= NOW() - @window::interval
  [AND p.model_version = @model_version]
ORDER BY p.created_at_utc DESC
LIMIT @limit;
```

### 2.4 Labeled feature samples

```sql
SELECT
    f.evaluation_id,
    f.timestamp_utc,
    f.features_json
FROM "ETH".ml_feature_snapshots f
JOIN "ETH".signal_outcomes o ON o.signal_id = f.signal_id
WHERE f.symbol = @sym
  AND f.timeframe = @tf
  AND f.feature_version = @feature_version
  AND o.outcome_label IN ('WIN', 'LOSS')
ORDER BY f.created_at_utc DESC
LIMIT @limit;
```

### 2.5 Recent live feature samples

```sql
SELECT
    f.evaluation_id,
    f.timestamp_utc,
    f.features_json
FROM "ETH".ml_feature_snapshots f
WHERE f.symbol = @sym
  AND f.timeframe = @tf
  AND f.feature_version = @feature_version
  AND COALESCE(f.link_status, 'PENDING') IN ('PENDING', 'SIGNAL_LINKED', 'ML_FILTERED')
  AND f.created_at_utc >= NOW() - @window::interval
ORDER BY f.created_at_utc DESC
LIMIT @limit;
```

### 2.6 Added blocked/generated totals in diagnostics

No direct SQL aggregate is used here. The service loads:

- full `Blocked Signals History`
- full `Generated Signals History`

then filters them to `5m+` for `ALL`, and applies:

```text
OutcomeEvaluator.ComputeStats(...)
```

Those totals are added into:

- `TotalOutcomes`
- `Wins`
- `Losses`
- `Pending`
- `Expired`
- `Ambiguous`

## 3. ML Auto-Trainer > Sample Readiness

Endpoint:

```text
GET /api/admin/ml/training/status
```

Readiness count query:

```sql
WITH trainable AS (
    SELECT f.evaluation_id, o.outcome_label
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".signal_outcomes o ON o.signal_id = f.signal_id
    WHERE f.feature_version = @feature_version
      AND f.signal_id IS NOT NULL
      AND COALESCE(f.timeframe, '') <> '1m'
      AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
      AND o.outcome_label IN ('WIN','LOSS')
      AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
    UNION
    SELECT f.evaluation_id, b.outcome_label
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".blocked_signal_outcomes b ON b.evaluation_id = f.evaluation_id
    WHERE f.feature_version = @feature_version
      AND COALESCE(f.timeframe, '') <> '1m'
      AND b.outcome_label IN ('WIN','LOSS')
      AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
    UNION
    SELECT f.evaluation_id, g.outcome_label
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".generated_signal_outcomes g ON g.evaluation_id = f.evaluation_id
    WHERE f.feature_version = @feature_version
      AND COALESCE(f.timeframe, '') <> '1m'
      AND g.outcome_label IN ('WIN','LOSS')
      AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
)
SELECT COUNT(*)::INT
FROM trainable;
```

Wins / losses query:

```sql
WITH trainable AS (
    -- same CTE as above
)
SELECT
    COUNT(*) FILTER (WHERE outcome_label = 'WIN')::INT,
    COUNT(*) FILTER (WHERE outcome_label = 'LOSS')::INT
FROM trainable;
```

## 4. Signal History

Endpoint:

```text
GET /api/signals/history
```

Table records query:

```sql
SELECT s.signal_id, s.timeframe, s.signal_time_utc, s.direction,
       s.entry_price, s.tp_price, s.sl_price, s.risk_percent, s.risk_usd,
       s.confidence_score, s.regime, s.strategy_version, s.reasons_json, s.status,
       o.outcome_label, o.pnl_r, o.bars_observed, o.tp_hit, o.sl_hit,
       o.mfe_price, o.mae_price, o.mfe_r, o.mae_r, o.closed_at_utc
FROM "ETH".signals s
LEFT JOIN "ETH".signal_outcomes o ON s.signal_id = o.signal_id
WHERE s.symbol = @s
ORDER BY s.signal_time_utc DESC
LIMIT @n OFFSET @off;
```

Total count query:

```sql
SELECT COUNT(*)
FROM "ETH".signals
WHERE symbol = @s;
```

## 5. Blocked Signals History

Endpoint:

```text
GET /api/blocked-signals/history
```

Table records query:

```sql
SELECT
    id,
    evaluation_id,
    symbol,
    decision_time_utc,
    bar_time_utc,
    timeframe,
    decision_type,
    candidate_direction,
    regime,
    parameter_set_id,
    confidence_score,
    reason_details_json,
    indicators_json,
    source_mode,
    lifecycle_state,
    final_block_reason,
    origin,
    effective_runtime_parameters_json
FROM "ETH".signal_decision_audit
WHERE symbol = @symbol
  AND lifecycle_state IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
  AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
ORDER BY decision_time_utc DESC
LIMIT @limit OFFSET @offset;
```

Total count query:

```sql
SELECT COUNT(*)::INT
FROM "ETH".signal_decision_audit
WHERE symbol = @symbol
  AND lifecycle_state IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
  AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL');
```

Summary:

- no separate SQL summary query
- the service rebuilds the blocked recommendation rows
- then computes:

```text
OutcomeEvaluator.ComputeStats(...)
```

This summary now matches the same blocked filter used by `Decision Summary > Blocked`.

## 6. Generated Signals History

Endpoint:

```text
GET /api/generated-signals/history
```

Table records query:

```sql
SELECT
    id,
    evaluation_id,
    symbol,
    decision_time_utc,
    bar_time_utc,
    timeframe,
    decision_type,
    candidate_direction,
    regime,
    parameter_set_id,
    confidence_score,
    reason_details_json,
    indicators_json,
    source_mode,
    lifecycle_state,
    final_block_reason,
    origin,
    effective_runtime_parameters_json
FROM "ETH".signal_decision_audit
WHERE symbol = @symbol
  AND outcome_category = 'SIGNAL_GENERATED'
  AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
  AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
  AND (@fromUtc IS NULL OR decision_time_utc >= @fromUtc)
ORDER BY decision_time_utc DESC
LIMIT @limit OFFSET @offset;
```

Total count query:

```sql
SELECT COUNT(*)::INT
FROM "ETH".signal_decision_audit
WHERE symbol = @symbol
  AND outcome_category = 'SIGNAL_GENERATED'
  AND lifecycle_state NOT IN ('RISK_BLOCKED', 'SESSION_BLOCKED')
  AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
  AND (@fromUtc IS NULL OR decision_time_utc >= @fromUtc);
```

Summary:

- no separate SQL summary query
- the service rebuilds the generated recommendation rows
- then computes:

```text
OutcomeEvaluator.ComputeStats(...)
```

This summary now matches the same generated filter used by `Decision Summary > Generated`.

## 7. Performance

Endpoint:

```text
GET /api/performance/summary
```

This is now an all-time merged performance calculation across:

- `Signal History`
- `Blocked Signals History`
- `Generated Signals History`

Applied load steps:

1. Load full `Signal History` with outcomes:

```sql
SELECT s.signal_id, s.timeframe, s.signal_time_utc, s.direction,
       s.entry_price, s.tp_price, s.sl_price, s.risk_percent, s.risk_usd,
       s.confidence_score, s.regime, s.strategy_version, s.reasons_json, s.status,
       o.outcome_label, o.pnl_r, o.bars_observed, o.tp_hit, o.sl_hit,
       o.mfe_price, o.mae_price, o.mfe_r, o.mae_r, o.closed_at_utc
FROM "ETH".signals s
LEFT JOIN "ETH".signal_outcomes o ON s.signal_id = o.signal_id
WHERE s.symbol = @s
ORDER BY s.signal_time_utc DESC
LIMIT @n OFFSET @off;
```

2. Load full blocked history using the blocked-history query above.
3. Load full generated history using the generated-history query above.
4. Merge all outcomes in memory.
5. Compute final performance with:

```text
OutcomeEvaluator.ComputeStats(...)
```
