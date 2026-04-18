"""
export_features.py — Extract ML training data from PostgreSQL.

Joins ml_feature_snapshots with signal_outcomes to produce labeled training data.
When signal_id is null on feature rows (older data), falls back to a strict,
timeframe-aware nearest-signal match within a configurable short window.
Fallback candidates can be limited to the newest N days to reduce historical
linkage noise before any ratio-based guardrails apply.
Only a limited number of fallback rows are retained per unmatched signal to
avoid proximity-match label noise.

In accuracy-first mode, proximity fallback is auto-enabled when direct linked
rows are below --auto-fallback-threshold (default 200) so training export and
diagnostics use the same practical definition of "trainable labeled samples".

A machine-readable summary JSON is written alongside the data file so the
training pipeline and diagnostics comparator can consume the same counts.

Temporary training policy:
    Exclude 1m timeframe rows from ML auto-training and readiness calculations.
    This keeps the trainer focused on 5m+ data until 1m signal quality improves.

Usage:
    python export_features.py --output data/training_data.csv --min-date 2026-01-01
"""

import argparse
import json
import os
import re
import sys

import pandas as pd
from sqlalchemy import create_engine, text


def camel_to_snake(name: str) -> str:
    """Convert camelCase to snake_case. E.g. macdHist → macd_hist, plusDi → plus_di."""
    s1 = re.sub(r'(.)([A-Z][a-z]+)', r'\1_\2', name)
    return re.sub(r'([a-z0-9])([A-Z])', r'\1_\2', s1).lower()


def get_connection_string() -> str:
    conn = os.environ.get("PG_CONNECTION")
    if not conn:
        print("ERROR: PG_CONNECTION environment variable not set", file=sys.stderr)
        sys.exit(1)
    # Convert Npgsql-style conn string to SQLAlchemy format if needed
    if conn.startswith("Host="):
        parts = dict(p.split("=", 1) for p in conn.split(";") if "=" in p)
        host = parts.get("Host", "localhost")
        port = parts.get("Port", "5432")
        db = parts.get("Database", "postgres")
        user = parts.get("Username", "postgres")
        pwd = parts.get("Password", "")
        return f"postgresql://{user}:{pwd}@{host}:{port}/{db}"
    return conn


_LINKED_BASE = """
    SELECT
        f.evaluation_id,
        f.signal_id,
        f.features_json,
        f.feature_version,
        f.created_at_utc AS feature_time,
        f.timeframe AS feature_timeframe,
        COALESCE(p.calibrated_win_probability, p.predicted_win_probability) AS calibrated_win_probability,
        o.outcome_label,
        o.pnl_r,
        o.tp_hit,
        o.sl_hit,
        o.bars_observed,
        o.mfe_r,
        o.mae_r
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".signal_outcomes o ON f.signal_id = o.signal_id
    LEFT JOIN LATERAL (
        SELECT
            p1.calibrated_win_probability,
            p1.predicted_win_probability
        FROM "ETH".ml_predictions p1
        WHERE p1.evaluation_id = f.evaluation_id
        ORDER BY p1.created_at_utc DESC
        LIMIT 1
    ) p ON true
    WHERE f.feature_version = :version
      AND f.signal_id IS NOT NULL
      AND COALESCE(f.timeframe, '') <> '1m'
      AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
      AND o.outcome_label IN ('WIN', 'LOSS')
      {date_clause}
    ORDER BY f.created_at_utc ASC
"""

_BLOCKED_BASE = """
    SELECT
        f.evaluation_id,
        b.decision_id AS signal_id,
        f.features_json,
        f.feature_version,
        COALESCE(f.timestamp_utc, f.created_at_utc) AS feature_time,
        f.timeframe AS feature_timeframe,
        COALESCE(p.calibrated_win_probability, p.predicted_win_probability) AS calibrated_win_probability,
        b.outcome_label,
        b.pnl_r,
        b.tp_hit,
        b.sl_hit,
        b.bars_observed,
        b.mfe_r,
        b.mae_r
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".blocked_signal_outcomes b ON b.evaluation_id = f.evaluation_id
    LEFT JOIN LATERAL (
        SELECT
            p1.calibrated_win_probability,
            p1.predicted_win_probability
        FROM "ETH".ml_predictions p1
        WHERE p1.evaluation_id = f.evaluation_id
        ORDER BY p1.created_at_utc DESC
        LIMIT 1
    ) p ON true
    WHERE f.feature_version = :version
      AND COALESCE(f.timeframe, '') <> '1m'
      AND b.outcome_label IN ('WIN', 'LOSS')
      {date_clause}
    ORDER BY COALESCE(f.timestamp_utc, f.created_at_utc) ASC
"""

_GENERATED_BASE = """
    SELECT
        f.evaluation_id,
        g.decision_id AS signal_id,
        f.features_json,
        f.feature_version,
        COALESCE(f.timestamp_utc, f.created_at_utc) AS feature_time,
        f.timeframe AS feature_timeframe,
        COALESCE(p.calibrated_win_probability, p.predicted_win_probability) AS calibrated_win_probability,
        g.outcome_label,
        g.pnl_r,
        g.tp_hit,
        g.sl_hit,
        g.bars_observed,
        g.mfe_r,
        g.mae_r
    FROM "ETH".ml_feature_snapshots f
    JOIN "ETH".generated_signal_outcomes g ON g.evaluation_id = f.evaluation_id
    LEFT JOIN LATERAL (
        SELECT
            p1.calibrated_win_probability,
            p1.predicted_win_probability
        FROM "ETH".ml_predictions p1
        WHERE p1.evaluation_id = f.evaluation_id
        ORDER BY p1.created_at_utc DESC
        LIMIT 1
    ) p ON true
    WHERE f.feature_version = :version
      AND COALESCE(f.timeframe, '') <> '1m'
      AND g.outcome_label IN ('WIN', 'LOSS')
      {date_clause}
    ORDER BY COALESCE(f.timestamp_utc, f.created_at_utc) ASC
"""

_PROXIMITY_BASE = """
    SELECT
        f.evaluation_id,
        s.signal_id,
        f.features_json,
        f.feature_version,
        f.created_at_utc AS feature_time,
        f.timeframe AS feature_timeframe,
        COALESCE(p.calibrated_win_probability, p.predicted_win_probability) AS calibrated_win_probability,
        s.signal_time_utc,
        s.timeframe AS signal_timeframe,
        ABS(EXTRACT(EPOCH FROM (s.signal_time_utc - COALESCE(f.timestamp_utc, f.created_at_utc)))) AS match_delta_seconds,
        o.outcome_label,
        o.pnl_r,
        o.tp_hit,
        o.sl_hit,
        o.bars_observed,
        o.mfe_r,
        o.mae_r
    FROM "ETH".ml_feature_snapshots f
    JOIN LATERAL (
        SELECT s2.signal_id, s2.signal_time_utc, s2.timeframe
        FROM "ETH".signals s2
        WHERE s2.symbol = f.symbol
          AND s2.timeframe = f.timeframe
          AND ABS(EXTRACT(EPOCH FROM (s2.signal_time_utc - COALESCE(f.timestamp_utc, f.created_at_utc)))) <= :max_proximity_seconds
        ORDER BY ABS(EXTRACT(EPOCH FROM (s2.signal_time_utc - COALESCE(f.timestamp_utc, f.created_at_utc)))),
                 s2.signal_time_utc DESC
        LIMIT 1
    ) s ON true
    JOIN "ETH".signal_outcomes o ON s.signal_id = o.signal_id
    LEFT JOIN LATERAL (
        SELECT
            p1.calibrated_win_probability,
            p1.predicted_win_probability
        FROM "ETH".ml_predictions p1
        WHERE p1.evaluation_id = f.evaluation_id
        ORDER BY p1.created_at_utc DESC
        LIMIT 1
    ) p ON true
    WHERE f.feature_version = :version
      AND f.signal_id IS NULL
      AND COALESCE(f.timeframe, '') <> '1m'
      AND COALESCE(f.link_status, 'PENDING') NOT IN ('NO_SIGNAL_EXPECTED', 'ML_FILTERED', 'OPERATIONALLY_BLOCKED')
      AND o.outcome_label IN ('WIN', 'LOSS')
      {date_clause}
    ORDER BY f.created_at_utc ASC
"""


def _build_queries(min_date: str | None, max_proximity_seconds: int):
    """Build queries and params. Omit date filter entirely when min_date is None
    to avoid psycopg2 confusing ::timestamptz cast colons with named params."""
    if min_date:
        # Use CAST() instead of :: to avoid colon collision with :param syntax
        date_clause = "AND f.created_at_utc >= CAST(:min_date AS timestamptz)"
        params_extra = {"min_date": min_date}
    else:
        date_clause = ""
        params_extra = {}

    linked = text(_LINKED_BASE.format(date_clause=date_clause))
    blocked = text(_BLOCKED_BASE.format(date_clause=date_clause))
    generated = text(_GENERATED_BASE.format(date_clause=date_clause))
    proximity = text(_PROXIMITY_BASE.format(date_clause=date_clause))
    params_extra["max_proximity_seconds"] = max(30, int(max_proximity_seconds))
    return linked, blocked, generated, proximity, params_extra


def _parse_proximity_tiers(
    max_proximity_seconds: int,
    proximity_tiers: str,
    auto_tighten_proximity: bool,
) -> list[int]:
    base = max(30, int(max_proximity_seconds))
    if not auto_tighten_proximity:
        return [base]

    parsed: list[int] = []
    for token in (proximity_tiers or "").split(","):
        token = token.strip()
        if not token:
            continue
        try:
            parsed.append(max(30, int(token)))
        except ValueError:
            continue

    parsed.append(base)
    tiers = sorted({v for v in parsed if v <= base}, reverse=True)
    return tiers or [base]


def _filter_proximity_rows(
    df_linked: pd.DataFrame,
    df_proximity: pd.DataFrame,
    max_proximity_seconds: int,
    max_proximity_per_signal: int,
    fallback_recency_days: int,
) -> pd.DataFrame:
    print(f"  Proximity candidates at <= {max_proximity_seconds}s (raw): {len(df_proximity)}")

    # Quality gate 1: fallback rows should only fill missing signal links,
    # never duplicate signals already present in direct linked rows.
    if not df_proximity.empty:
        linked_signal_ids = set(df_linked["signal_id"].dropna().tolist())
        before = len(df_proximity)
        if linked_signal_ids:
            df_proximity = df_proximity[~df_proximity["signal_id"].isin(linked_signal_ids)]
        print(
            f"  Proximity rows after excluding already-linked signals: {len(df_proximity)} "
            f"(dropped {before - len(df_proximity)})"
        )

    # Quality gate 2: prioritize newest fallback candidates to reduce
    # historical linkage noise without forcing a hard cap.
    recency_days = max(0, int(fallback_recency_days))
    if recency_days > 0 and not df_proximity.empty and "feature_time" in df_proximity.columns:
        feature_ts = pd.to_datetime(df_proximity["feature_time"], utc=True, errors="coerce")
        latest_ts = feature_ts.max()
        if pd.notna(latest_ts):
            cutoff_ts = latest_ts - pd.Timedelta(days=recency_days)
            keep_mask = feature_ts >= cutoff_ts
            kept = int(keep_mask.sum())
            if kept > 0:
                before = len(df_proximity)
                df_proximity = df_proximity.loc[keep_mask].copy()
                print(
                    f"  Proximity recency window (last {recency_days}d): {len(df_proximity)} "
                    f"(dropped {before - len(df_proximity)})"
                )
            else:
                print(
                    f"  Proximity recency window (last {recency_days}d): no rows matched; "
                    "keeping full candidate set"
                )

    # Quality gate 3: keep only nearest N fallback features per unmatched signal,
    # preferring newest feature_time on equal match distance.
    max_per_signal = max(1, int(max_proximity_per_signal))
    if not df_proximity.empty:
        before = len(df_proximity)
        df_proximity = (
            df_proximity
            .sort_values(["signal_id", "match_delta_seconds", "feature_time"], ascending=[True, True, False])
            .groupby("signal_id", as_index=False)
            .head(max_per_signal)
            .reset_index(drop=True)
        )
        print(
            f"  Proximity rows retained (max {max_per_signal}/signal): "
            f"{len(df_proximity)} (dropped {before - len(df_proximity)})"
        )
        if "match_delta_seconds" in df_proximity.columns:
            print(
                "  Proximity match delta (sec): "
                f"median={df_proximity['match_delta_seconds'].median():.1f}, "
                f"p95={df_proximity['match_delta_seconds'].quantile(0.95):.1f}"
            )

    print(f"  Proximity-joined rows (final): {len(df_proximity)}")
    return df_proximity


def export_training_data(
    output_path: str,
    min_date: str | None,
    feature_version: str,
    include_proximity_fallback: bool = False,
    skip_no_trade_contexts: bool = True,
    max_proximity_seconds: int = 180,
    max_proximity_per_signal: int = 1,
    proximity_tiers: str = "180,120,90",
    max_fallback_ratio: float = 3.0,
    auto_tighten_proximity: bool = True,
    fallback_recency_days: int = 14,
    min_linked_for_hard_cap: int = 150,
    auto_fallback_threshold: int = 200,
) -> int:
    """Export labeled training data. Returns the number of exported rows.
    Also writes a JSON summary next to the output path for downstream tooling.
    """
    engine = create_engine(get_connection_string())
    linked_query, blocked_query, generated_query, _, extra = _build_queries(min_date, max_proximity_seconds)
    params = {"version": feature_version, **extra}

    summary: dict = {
        "output_path": output_path,
        "feature_version": feature_version,
        "min_date": min_date,
        "excluded_timeframes": ["1m"],
        "direct_linked_rows": 0,
        "blocked_rows": 0,
        "generated_rows": 0,
        "fallback_rows": 0,
        "dropped_no_trade_rows": 0,
        "wins": 0,
        "losses": 0,
        "total_rows": 0,
        "timeframe_distribution": {},
        "auto_fallback_triggered": False,
        "auto_fallback_threshold": int(auto_fallback_threshold),
    }

    with engine.connect() as conn:
        print("  Excluding timeframes from training export: ['1m']")
        df_linked = pd.read_sql(linked_query, conn, params=params)
        print(f"  Linked rows (signal_id IS NOT NULL): {len(df_linked)}")
        summary["direct_linked_rows"] = int(len(df_linked))

        df_blocked = pd.read_sql(blocked_query, conn, params=params)
        print(f"  Blocked rows (evaluation_id matched): {len(df_blocked)}")
        summary["blocked_rows"] = int(len(df_blocked))

        df_generated = pd.read_sql(generated_query, conn, params=params)
        print(f"  Generated rows (evaluation_id matched): {len(df_generated)}")
        summary["generated_rows"] = int(len(df_generated))

        # Auto-enable proximity fallback when direct linked rows are below the
        # trainable threshold. Keeps the guarded tier logic intact but removes
        # the silent drop where export emits fewer rows than diagnostics
        # reports as "trainable" labeled samples.
        effective_include_fallback = False
        if include_proximity_fallback:
            print(
                "  WARN: Proximity fallback is disabled for v3.0+ exports. "
                "Only direct signal-linked feature snapshots are exported."
            )

        df_proximity = pd.DataFrame()
        if effective_include_fallback:
            tiers = _parse_proximity_tiers(
                max_proximity_seconds=max_proximity_seconds,
                proximity_tiers=proximity_tiers,
                auto_tighten_proximity=auto_tighten_proximity,
            )
            if auto_tighten_proximity and len(tiers) > 1:
                print(f"  Auto-tighten tiers: {tiers} | max fallback/link ratio: {max_fallback_ratio:.2f}")

            selected_tier = tiers[-1]
            linked_count = len(df_linked)
            linked_denom = max(1, linked_count)

            for tier in tiers:
                _, _, _, proximity_query, extra = _build_queries(min_date, tier)
                proximity_params = {"version": feature_version, **extra}
                candidate = pd.read_sql(proximity_query, conn, params=proximity_params)
                filtered = _filter_proximity_rows(
                    df_linked=df_linked,
                    df_proximity=candidate,
                    max_proximity_seconds=tier,
                    max_proximity_per_signal=max_proximity_per_signal,
                    fallback_recency_days=fallback_recency_days,
                )

                ratio = len(filtered) / linked_denom
                print(f"  Fallback/link ratio at {tier}s: {len(filtered)}/{linked_count} = {ratio:.2f}")

                df_proximity = filtered
                selected_tier = tier

                if (not auto_tighten_proximity) or ratio <= max_fallback_ratio:
                    break

            if auto_tighten_proximity and (len(df_proximity) / linked_denom) > max_fallback_ratio:
                print(
                    f"  WARN: Even strictest tier ({selected_tier}s) fallback/link ratio "
                    f"is {(len(df_proximity) / linked_denom):.2f} > {max_fallback_ratio:.2f}"
                )

                # Final guardrail: cap fallback volume to keep label quality stable.
                # Keep best-quality rows first (smallest time delta, newest feature time).
                if linked_count >= max(1, int(min_linked_for_hard_cap)):
                    cap_rows = max(1, int(linked_count * max_fallback_ratio))
                    before = len(df_proximity)
                    df_proximity = (
                        df_proximity
                        .sort_values(["match_delta_seconds", "feature_time"], ascending=[True, False])
                        .head(cap_rows)
                        .reset_index(drop=True)
                    )
                    print(
                        f"  Applied hard cap: fallback rows {before} -> {len(df_proximity)} "
                        f"(cap={cap_rows}, ratio={len(df_proximity) / linked_denom:.2f})"
                    )
                else:
                    print(
                        f"  Hard cap skipped: linked rows ({linked_count}) < min_linked_for_hard_cap "
                        f"({min_linked_for_hard_cap})"
                    )
        else:
            print("  Proximity fallback disabled — exporting direct signal-linked rows only")

    df = pd.concat([df_linked, df_blocked, df_generated, df_proximity], ignore_index=True)
    # De-duplicate in case same evaluation appears from both paths
    df = df.drop_duplicates(subset=["evaluation_id"])
    summary["fallback_rows"] = int(len(df_proximity))

    if df.empty:
        print("WARNING: No training data found.")
        print("  Possible causes:")
        print("  1. No signal outcomes with WIN/LOSS labels yet (check signal_outcomes table)")
        print("  2. No blocked_signal_outcomes with WIN/LOSS labels yet")
        print("  3. No generated_signal_outcomes with WIN/LOSS labels yet")
        print("  4. ml_feature_snapshots is empty (ML mode may have been DISABLED)")
        print("  5. feature_version mismatch (using --feature-version v3.0?)")
        print("")
        print("  The app generates feature snapshots on every 5m bar when MlMode=SHADOW/ACTIVE.")
        print("  Signal outcomes are written ~4 bars after a signal is generated.")

        # Still emit a summary file so downstream tooling can react consistently.
        summary_path = os.path.splitext(output_path)[0] + "_summary.json"
        try:
            os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
            with open(summary_path, "w") as f:
                json.dump(summary, f, indent=2)
        except Exception as exc:
            print(f"  WARN: failed to write export summary to {summary_path}: {exc}")
        sys.exit(0)

    print(f"\nTotal training rows: {len(df)}")

    # Expand features_json into columns (new rows: snake_case keys; legacy rows: camelCase)
    features_df = pd.json_normalize(
        df["features_json"].apply(lambda x: x if isinstance(x, dict) else {})
    )
    features_df.index = df.index
    # Convert camelCase column names to snake_case to match FEATURE_NAMES in train script
    # e.g. macdHist → macd_hist, plusDi → plus_di, closeMid → close_mid
    features_df.columns = [camel_to_snake(c) for c in features_df.columns]

    # Rename legacy camelCase columns that camel_to_snake can't fix due to number boundaries.
    # Old C# serializer produced e.g. "ema20Slope3" → camel_to_snake → "ema20_slope3" (wrong).
    # New serializer writes exact snake_case keys, so these renames are no-ops for new data.
    _LEGACY_RENAME = {
        'ema20_slope3':         'ema20_slope_3',
        'ema20_slope5':         'ema20_slope_5',
        'rsi14_delta3':         'rsi14_delta_3',
        'macd_hist_delta3':     'macd_hist_delta_3',
        'recent_win_rate10':    'recent_win_rate_10',
        'recent_win_rate20':    'recent_win_rate_20',
        'recent_avg_pnl_r10':   'recent_avg_pnl_r_10',
        'recent_avg_pnl_r20':   'recent_avg_pnl_r_20',
        'avg_atr20_bars':       'avg_atr_20_bars',
        'avg_volume10_bars':    'avg_volume_10_bars',
        'price_range20_bars_pct': 'price_range_20_bars_pct',
        'regime_changes_last20':  'regime_changes_last_20',
    }
    features_df = features_df.rename(columns={k: v for k, v in _LEGACY_RENAME.items()
                                               if k in features_df.columns})

    timeframe_map = {
        "1m": 1,
        "5m": 2,
        "15m": 3,
        "30m": 4,
        "1h": 5,
        "4h": 6,
    }
    if "timeframe_encoded" not in features_df.columns:
        features_df["timeframe_encoded"] = 0
    if "feature_timeframe" in df.columns:
        features_df["timeframe_encoded"] = (
            features_df["timeframe_encoded"]
            .fillna(df["feature_timeframe"].map(timeframe_map).fillna(0))
            .astype(int)
        )
    # T3-5: Warn if too many rows have timeframe_encoded=0 (unknown/missing timeframe).
    # More than 10% defaulted rows indicates the timeframe column is not populating correctly.
    if len(features_df) > 0:
        zero_pct = (features_df["timeframe_encoded"] == 0).mean()
        if zero_pct > 0.10:
            print(f"  WARN: {zero_pct:.1%} of rows have timeframe_encoded=0 (unknown TF). "
                  f"Check that ml_feature_snapshots.timeframe is populated correctly.")

    result = pd.concat([
        df[["evaluation_id", "signal_id", "feature_time", "feature_timeframe", "calibrated_win_probability"]],
        features_df,
        df[["outcome_label", "pnl_r", "tp_hit", "sl_hit", "bars_observed", "mfe_r", "mae_r"]],
    ], axis=1)
    result = result.rename(columns={"feature_timeframe": "timeframe"})

    result["target"] = (result["outcome_label"] == "WIN").astype(int)

    dropped_no_trade = 0
    if skip_no_trade_contexts and "direction_encoded" in result.columns:
        before = len(result)
        result = result[result["direction_encoded"] != 0].reset_index(drop=True)
        dropped_no_trade = before - len(result)
        if dropped_no_trade > 0:
            print(f"Filtered {dropped_no_trade} NO_TRADE-context rows from export")
    summary["dropped_no_trade_rows"] = int(dropped_no_trade)

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)

    if output_path.endswith(".parquet"):
        result.to_parquet(output_path, index=False)
    else:
        result.to_csv(output_path, index=False)

    wins = int(result["target"].sum())
    losses = int(len(result) - wins)
    summary["wins"] = wins
    summary["losses"] = losses
    summary["total_rows"] = int(len(result))
    if "timeframe" in result.columns:
        tf_counts = result["timeframe"].fillna("unknown").astype(str).value_counts().to_dict()
        summary["timeframe_distribution"] = {str(k): int(v) for k, v in tf_counts.items()}

    summary_path = os.path.splitext(output_path)[0] + "_summary.json"
    try:
        with open(summary_path, "w") as f:
            json.dump(summary, f, indent=2)
    except Exception as exc:
        print(f"  WARN: failed to write export summary to {summary_path}: {exc}")

    print("")
    print("─── Export summary " + "─" * 44)
    print(f"  feature_version       : {feature_version}")
    print(f"  direct_linked_rows    : {summary['direct_linked_rows']}")
    print(f"  blocked_rows          : {summary['blocked_rows']}")
    print(f"  generated_rows        : {summary['generated_rows']}")
    print(f"  fallback_rows         : {summary['fallback_rows']}")
    print(f"  auto_fallback_triggered: {summary['auto_fallback_triggered']}")
    print(f"  dropped_no_trade_rows : {summary['dropped_no_trade_rows']}")
    print(f"  wins / losses         : {wins} / {losses}")
    print(f"  total_rows            : {summary['total_rows']}")
    if summary["timeframe_distribution"]:
        print(f"  timeframe_distribution: {summary['timeframe_distribution']}")
    print(f"  summary_file          : {summary_path}")
    print("─" * 62)

    print(f"Exported {len(result)} rows to {output_path}")
    print(f"  WIN: {wins}, LOSS: {losses}")
    if len(result) > 0:
        print(f"  Win rate: {wins / len(result):.3f}")

    return int(len(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export ML training features from DB")
    parser.add_argument("--output", default="data/training_data.csv", help="Output file path")
    parser.add_argument("--min-date", default=None, help="Minimum date filter (ISO format)")
    parser.add_argument("--feature-version", default="v3.0", help="Feature version to export")
    parser.add_argument(
        "--include-proximity-fallback",
        action="store_true",
        help="Include nearest-signal fallback rows for unmatched feature snapshots (disabled by default)",
    )
    parser.add_argument(
        "--keep-no-trade-contexts",
        dest="skip_no_trade_contexts",
        action="store_false",
        help="Retain rows where direction_encoded=0 instead of filtering them out",
    )
    parser.add_argument(
        "--max-proximity-seconds",
        type=int,
        default=180,
        help="Maximum |signal_time - feature_time| for fallback linkage (seconds)",
    )
    parser.add_argument(
        "--max-proximity-per-signal",
        type=int,
        default=1,
        help="Maximum fallback feature rows to keep for each unmatched signal",
    )
    parser.add_argument(
        "--proximity-tiers",
        default="180,120,90",
        help="Comma-separated proximity tiers (seconds) used when auto-tightening fallback matching",
    )
    parser.add_argument(
        "--max-fallback-ratio",
        type=float,
        default=3.0,
        help="Maximum allowed fallback-to-linked row ratio before tightening to next tier",
    )
    parser.add_argument(
        "--fallback-recency-days",
        type=int,
        default=14,
        help="Keep fallback matches from newest N days (0 disables recency filter)",
    )
    parser.add_argument(
        "--no-auto-tighten-proximity",
        dest="auto_tighten_proximity",
        action="store_false",
        help="Disable automatic tier fallback tightening",
    )
    parser.add_argument(
        "--min-linked-for-hard-cap",
        type=int,
        default=150,
        help="Minimum linked rows required before applying fallback hard-cap pruning",
    )
    parser.add_argument(
        "--auto-fallback-threshold",
        type=int,
        default=200,
        help="Auto-enable proximity fallback when direct linked rows are below this count",
    )
    parser.set_defaults(auto_tighten_proximity=True)
    args = parser.parse_args()

    export_training_data(
        args.output,
        args.min_date,
        args.feature_version,
        include_proximity_fallback=args.include_proximity_fallback,
        skip_no_trade_contexts=args.skip_no_trade_contexts,
        max_proximity_seconds=args.max_proximity_seconds,
        max_proximity_per_signal=args.max_proximity_per_signal,
        proximity_tiers=args.proximity_tiers,
        max_fallback_ratio=args.max_fallback_ratio,
        auto_tighten_proximity=args.auto_tighten_proximity,
        fallback_recency_days=args.fallback_recency_days,
        min_linked_for_hard_cap=args.min_linked_for_hard_cap,
        auto_fallback_threshold=args.auto_fallback_threshold,
    )
