"""
train_threshold_model.py — Build dynamic threshold lookup table (M3).

Bins signal outcomes by (regime, ADX bucket, session) and computes optimal
confidence thresholds per bin. Exports as JSON for SignalFrequencyManager.

Usage:
    python train_threshold_model.py --data data/training_data.csv --output models/
"""

import argparse
import json
import os
from datetime import datetime, timezone

import numpy as np
import pandas as pd


def classify_adx(adx: float) -> str:
    if adx >= 30:
        return "high"
    elif adx >= 20:
        return "mid"
    return "low"


def classify_session(hour: int) -> str:
    if 7 <= hour < 13:
        return "london"
    elif 13 <= hour < 16:
        return "overlap"
    elif 16 <= hour < 21:
        return "ny"
    return "asia"


def compute_optimal_threshold(group_df: pd.DataFrame, score_column: str, min_samples: int = 10) -> int | None:
    """Find the confidence threshold that maximizes expected value."""
    if len(group_df) < min_samples:
        return None

    best_ev = -np.inf
    best_threshold = 65  # default

    for threshold in range(40, 91, 5):
        above = group_df[group_df[score_column] >= threshold]
        if len(above) < 5:
            continue

        win_rate = above["target"].mean()
        avg_win_r = above.loc[above["target"] == 1, "pnl_r"].mean() if (above["target"] == 1).any() else 1.5
        avg_loss_r = abs(above.loc[above["target"] == 0, "pnl_r"].mean()) if (above["target"] == 0).any() else 1.0

        ev = win_rate * avg_win_r - (1 - win_rate) * avg_loss_r
        signal_rate = len(above) / len(group_df)

        # Penalize very low signal rates
        adjusted_ev = ev * (0.5 + 0.5 * min(signal_rate / 0.3, 1.0))

        if adjusted_ev > best_ev:
            best_ev = adjusted_ev
            best_threshold = threshold

    return best_threshold


def build_gate_score(df: pd.DataFrame, blend_weight: float) -> pd.Series:
    rule_score = pd.to_numeric(df.get("rule_based_score", 0), errors="coerce").fillna(0.0)

    if "calibrated_win_probability" not in df.columns:
        return rule_score.clip(0, 100)

    calibrated_prob = pd.to_numeric(df["calibrated_win_probability"], errors="coerce")
    ml_score = calibrated_prob.clip(lower=0.0, upper=1.0) * 100.0
    blended = ((1.0 - blend_weight) * rule_score) + (blend_weight * ml_score.fillna(rule_score))
    return blended.clip(0, 100)


def main(data_path: str, output_dir: str, blend_weight: float):
    if data_path.endswith(".parquet"):
        df = pd.read_parquet(data_path)
    else:
        df = pd.read_csv(data_path)

    print(f"Loaded {len(df)} samples")
    blend_weight = min(max(float(blend_weight), 0.0), 1.0)

    # Map regime labels
    regime_map = {0: "NEUTRAL", 1: "BULLISH", 2: "BEARISH"}
    df["regime_name"] = df["regime_label"].map(regime_map).fillna("NEUTRAL")
    df["adx_bucket"] = df["adx14"].apply(classify_adx)
    df["session"] = df["hour_of_day"].apply(classify_session)
    df["gate_score"] = build_gate_score(df, blend_weight)

    timeframe_series = df["timeframe"] if "timeframe" in df.columns else pd.Series(["any"] * len(df))
    df["timeframe"] = timeframe_series.fillna("any").astype(str).str.lower()

    score_source = "blended_confidence_proxy" if "calibrated_win_probability" in df.columns else "rule_based_score"
    print(f"Using score column 'gate_score' (source={score_source}, blend_weight={blend_weight:.2f})")

    # Compute thresholds per (timeframe, regime, adx_bucket, session)
    lookup = {}
    summary_rows = []

    for (timeframe, regime, adx_bkt, session), group in df.groupby(["timeframe", "regime_name", "adx_bucket", "session"]):
        threshold = compute_optimal_threshold(group, "gate_score")
        if threshold is not None:
            key = f"{timeframe}_{regime}_{adx_bkt}_{session}"
            lookup[key] = threshold
            summary_rows.append({
                "key": key,
                "threshold": threshold,
                "samples": len(group),
                "win_rate": float(group["target"].mean()),
            })

    # Also create timeframe/session fallbacks per (timeframe, regime, adx_bucket)
    for (timeframe, regime, adx_bkt), group in df.groupby(["timeframe", "regime_name", "adx_bucket"]):
        threshold = compute_optimal_threshold(group, "gate_score")
        if threshold is not None:
            key = f"{timeframe}_{regime}_{adx_bkt}_any"
            if key not in lookup:
                lookup[key] = threshold
                summary_rows.append({
                    "key": key,
                    "threshold": threshold,
                    "samples": len(group),
                    "win_rate": float(group["target"].mean()),
                })

    # Global fallbacks across all timeframes.
    for (regime, adx_bkt), group in df.groupby(["regime_name", "adx_bucket"]):
        threshold = compute_optimal_threshold(group, "gate_score")
        if threshold is not None:
            key = f"any_{regime}_{adx_bkt}_any"
            if key not in lookup:
                lookup[key] = threshold
                summary_rows.append({
                    "key": key,
                    "threshold": threshold,
                    "samples": len(group),
                    "win_rate": float(group["target"].mean()),
                })

    # Ensure fallback defaults exist for all timeframe/regime/adx combinations
    # so the runtime always has a threshold to look up.
    DEFAULT_THRESHOLD = 65
    for timeframe in ("1m", "5m", "15m", "30m", "1h", "4h", "any"):
        for regime in ("BULLISH", "BEARISH", "NEUTRAL"):
            for adx_bkt in ("high", "mid", "low"):
                key = f"{timeframe}_{regime}_{adx_bkt}_any"
                if key not in lookup:
                    lookup[key] = DEFAULT_THRESHOLD
                    summary_rows.append({
                        "key": key,
                        "threshold": DEFAULT_THRESHOLD,
                        "samples": 0,
                        "win_rate": float("nan"),
                        "note": "default_fallback",
                    })

    print(f"\nGenerated {len(lookup)} threshold entries:")
    for row in sorted(summary_rows, key=lambda r: r["key"]):
        note = f" [{row.get('note', '')}]" if row.get("note") else ""
        print(f"  {row['key']}: threshold={row['threshold']}, "
              f"n={row['samples']}, wr={row.get('win_rate', float('nan')):.3f}{note}")

    # Export
    os.makedirs(output_dir, exist_ok=True)
    version = datetime.now(timezone.utc).strftime("v%Y%m%d_%H%M%S")

    # T3-21: Replace NaN win_rate with 0.0 before writing JSON.
    # float("nan") in summary_rows produces bare NaN in json.dump output,
    # which is not valid JSON (Python's json module writes it as "NaN" which
    # most parsers reject). C# MlInferenceService already sanitizes it via regex,
    # but it's cleaner to never produce invalid JSON in the first place.
    for row in summary_rows:
        if "win_rate" in row and (row["win_rate"] != row["win_rate"]):  # NaN check
            row["win_rate"] = 0.0

    output = {
        "model_type": "threshold_model",
        "model_version": version,
        "lookup_table": lookup,
        "summary": summary_rows,
        "score_source": score_source,
        "blend_weight": blend_weight,
        "total_samples": len(df),
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
    }

    out_path = os.path.join(output_dir, f"threshold_lookup_{version}.json")
    with open(out_path, "w") as f:
        json.dump(output, f, indent=2)

    print(f"\nThreshold lookup saved to {out_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train dynamic threshold lookup model")
    parser.add_argument("--data", required=True, help="Training data path")
    parser.add_argument("--output", default="models/", help="Output directory")
    parser.add_argument("--blend-weight", type=float, default=0.5, help="Blend weight used to proxy live blended confidence")
    args = parser.parse_args()

    main(args.data, args.output, args.blend_weight)
