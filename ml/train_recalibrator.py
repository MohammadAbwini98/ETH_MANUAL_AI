"""
train_recalibrator.py — Train the confidence recalibrator (M2).

Fits isotonic regression on true out-of-fold predictor probabilities so the
calibration model never sees in-fold scores from the model-selection slice.

Usage:
    python train_recalibrator.py --data data/training_data.csv --model models/outcome_predictor_v*.onnx
"""

import argparse
import json
import os
import sys
from datetime import datetime, timezone

import numpy as np

from train_outcome_predictor import (
    EMBARGO_BARS,
    FEATURE_NAMES,
    load_data,
    train_lightgbm,
    train_xgboost,
    walk_forward_split,
)
from sklearn.isotonic import IsotonicRegression


MIN_CALIBRATION_SAMPLES = 40
GRID_POINTS = 1001
FULL_BLEND_SAMPLE_TARGET = 180
FULL_BLEND_UNIQUE_TARGET = 48


def load_model_metadata(model_path: str) -> dict:
    """Load predictor training metadata when available."""
    if model_path.endswith(".onnx"):
        meta_path = model_path.replace(".onnx", "_meta.json")
    elif model_path.endswith(".pkl"):
        meta_path = model_path.replace(".pkl", "_meta.json")
    else:
        meta_path = model_path + "_meta.json"

    if not os.path.exists(meta_path):
        return {}

    with open(meta_path) as f:
        return json.load(f)


def train_fold_model(model_library: str, X_train, y_train, X_val, y_val):
    """Train one temporary fold model for OOF calibration predictions."""
    try:
        if model_library == "xgboost":
            return train_xgboost(X_train, y_train, X_val, y_val)
        return train_lightgbm(X_train, y_train, X_val, y_val)
    except ImportError:
        alt = "lightgbm" if model_library == "xgboost" else "xgboost"
        if alt == "xgboost":
            return train_xgboost(X_train, y_train, X_val, y_val)
        return train_lightgbm(X_train, y_train, X_val, y_val)


def main(data_path: str, model_path: str, output_dir: str):
    df = load_data(data_path)

    # Need at least 4 samples to split into any folds at all.
    if len(df) < 4:
        print(f"SKIP: Only {len(df)} samples — need at least 4 for calibration.")
        sys.exit(2)

    metadata = load_model_metadata(model_path)
    model_library = str(metadata.get("model_library", "lightgbm")).lower()
    requested_folds = int(metadata.get("n_folds", 5))

    available_features = [f for f in FEATURE_NAMES if f in df.columns]
    X = df[available_features].astype(np.float32)
    y = df["target"].values.astype(int)

    effective_folds = max(2, min(requested_folds, len(df) // 8))
    effective_embargo = min(EMBARGO_BARS, max(1, len(df) // 15))

    fold_raw_probs = []
    fold_labels = []
    fold_sizes = []

    print(
        f"Generating OOF predictions for recalibration: "
        f"folds={effective_folds}, embargo={effective_embargo}, model={model_library}"
    )

    for fold_no, (train_idx, cal_idx) in enumerate(
        walk_forward_split(df, effective_folds, effective_embargo),
        start=1,
    ):
        if len(train_idx) == 0 or len(cal_idx) == 0:
            continue

        X_train = X.iloc[train_idx].reset_index(drop=True)
        X_cal = X.iloc[cal_idx].reset_index(drop=True)
        y_train = y[train_idx]
        y_cal = y[cal_idx]

        if len(np.unique(y_train)) < 2:
            print(f"Fold {fold_no}: skip — train set has only one class")
            continue

        model = train_fold_model(model_library, X_train, y_train, X_cal, y_cal)
        raw_probs = np.clip(model.predict_proba(X_cal)[:, 1], 1e-4, 1 - 1e-4)

        fold_raw_probs.append(raw_probs)
        fold_labels.append(y_cal)
        fold_sizes.append(len(cal_idx))

        print(
            f"Fold {fold_no}: calibration samples={len(cal_idx)} "
            f"raw_mean={raw_probs.mean():.4f} raw_std={raw_probs.std():.4f}"
        )

    if not fold_raw_probs:
        print("SKIP: No usable out-of-fold predictions for calibration.")
        sys.exit(2)

    raw_probs = np.concatenate(fold_raw_probs)
    y_cal = np.concatenate(fold_labels)
    unique_raw = np.unique(np.round(raw_probs, 6))

    print(f"Calibration set: {len(y_cal)} pooled OOF samples across {len(fold_sizes)} folds")
    print(f"Unique raw probabilities: {len(unique_raw)}")

    if len(y_cal) < MIN_CALIBRATION_SAMPLES or len(unique_raw) < 8:
        print(
            f"SKIP: Only {len(y_cal)} pooled calibration samples / {len(unique_raw)} unique scores "
            f"(need at least {MIN_CALIBRATION_SAMPLES} samples and 8 unique scores)."
        )
        sys.exit(2)

    print(f"Raw probs: mean={raw_probs.mean():.4f}, std={raw_probs.std():.4f}")

    iso_reg = IsotonicRegression(y_min=0.01, y_max=0.99, out_of_bounds="clip")
    iso_reg.fit(raw_probs, y_cal)

    sample_blend = np.clip(
        (len(y_cal) - MIN_CALIBRATION_SAMPLES) / max(1, FULL_BLEND_SAMPLE_TARGET - MIN_CALIBRATION_SAMPLES),
        0.15,
        1.0,
    )
    unique_blend = np.clip(
        (len(unique_raw) - 8) / max(1, FULL_BLEND_UNIQUE_TARGET - 8),
        0.15,
        1.0,
    )
    blend_weight = float(min(sample_blend, unique_blend))

    isotonic_calibrated = iso_reg.predict(raw_probs)
    calibrated = np.clip(
        blend_weight * isotonic_calibrated + (1.0 - blend_weight) * raw_probs,
        0.01,
        0.99,
    )

    from sklearn.metrics import brier_score_loss

    raw_brier = brier_score_loss(y_cal, raw_probs)
    cal_brier = brier_score_loss(y_cal, calibrated)
    print(f"Blend weight: {blend_weight:.3f}")
    print(f"Brier score: raw={raw_brier:.4f}, calibrated={cal_brier:.4f}")

    if cal_brier >= raw_brier:
        print(
            f"WARN: Calibration degraded Brier score ({cal_brier:.4f} >= {raw_brier:.4f}). "
            "Setting blend_weight=0 (passthrough)."
        )
        blend_weight = 0.0
        calibrated = raw_probs

    os.makedirs(output_dir, exist_ok=True)
    version = datetime.now(timezone.utc).strftime("v%Y%m%d_%H%M%S")

    lookup_raw = np.linspace(0, 1, GRID_POINTS)
    lookup_iso = iso_reg.predict(lookup_raw)
    lookup_cal = np.clip(
        blend_weight * lookup_iso + (1.0 - blend_weight) * lookup_raw,
        0.01,
        0.99,
    )
    calibration_table = {
        f"{r:.3f}": float(round(c, 4))
        for r, c in zip(lookup_raw, lookup_cal)
    }

    output = {
        "model_type": "confidence_recalibrator",
        "model_version": version,
        "method": "isotonic_regression",
        "prediction_source": "oof_time_series_split",
        "predictor_model_library": model_library,
        "calibration_table": calibration_table,
        "raw_brier": float(raw_brier),
        "calibrated_brier": float(cal_brier),
        "calibration_samples": int(len(y_cal)),
        "calibration_folds": len(fold_sizes),
        "calibration_fold_sizes": [int(size) for size in fold_sizes],
        "unique_raw_probabilities": int(len(unique_raw)),
        "blend_weight": float(round(blend_weight, 4)),
        "embargo_bars": int(effective_embargo),
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
    }

    out_path = os.path.join(output_dir, f"recalibrator_{version}.json")
    with open(out_path, "w") as f:
        json.dump(output, f, indent=2)

    import joblib

    pkl_path = os.path.join(output_dir, f"recalibrator_{version}.pkl")
    joblib.dump(iso_reg, pkl_path)

    print(f"\nCalibration table saved to {out_path}")
    print(f"Isotonic model saved to {pkl_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train confidence recalibrator")
    parser.add_argument("--data", required=True, help="Training data path")
    parser.add_argument("--model", required=True, help="Trained outcome predictor model path")
    parser.add_argument("--output", default="models/", help="Output directory")
    args = parser.parse_args()

    main(args.data, args.model, args.output)
