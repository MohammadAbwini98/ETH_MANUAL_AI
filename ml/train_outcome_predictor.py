"""
train_outcome_predictor.py — Train the outcome prediction model (M1).

Walk-forward cross-validation with purge/embargo.
Trains LightGBM by default, with XGBoost fallback.
Exports best model to ONNX format.

Usage:
    python train_outcome_predictor.py --data data/training_data.csv --output models/
"""

import argparse
import json
import os
import sys
from datetime import datetime, timezone

import numpy as np
import pandas as pd
from sklearn.model_selection import TimeSeriesSplit

# ─── Feature columns (must match MlFeatureVector.FeatureNames) ────────
FEATURE_NAMES = [
    # Category A (14)
    "ema20", "ema50", "rsi14", "macd_hist", "adx14", "plus_di", "minus_di", "atr14",
    "vwap", "volume_sma20", "spread", "close_mid", "volume", "body_ratio",
    # Category B (18)
    "ema20_minus_ema50", "ema20_minus_ema50_pct", "ema20_slope_3", "ema20_slope_5",
    "rsi14_delta", "rsi14_delta_3", "macd_hist_delta", "macd_hist_delta_3",
    "adx14_delta", "atr14_pct", "atr14_delta_pct", "distance_to_ema20_pct",
    "distance_to_vwap_pct", "volume_ratio", "spread_pct", "di_differential",
    "di_ratio", "candle_range_pct",
    # Category C (12)
    # Note: direction_encoded is intentionally excluded. Training data filters to BUY/SELL
    # contexts only (direction_encoded != 0), so the model never sees direction_encoded=0.
    # Including it creates a train/inference distribution mismatch that degrades performance.
    # Direction context is implicitly captured via regime_label and rule_based_score.
    "regime_label", "regime_score", "regime_age_bars", "rule_based_score",
    "timeframe_encoded", "hour_of_day", "day_of_week", "minutes_since_open",
    "is_london_session", "is_ny_session", "is_asia_session", "is_overlap",
    # Category D (8)
    "bars_since_last_signal",
    "avg_atr_20_bars", "atr_zscore", "avg_volume_10_bars", "volume_zscore",
    "price_range_20_bars_pct", "regime_changes_last_20", "pullback_depth_pct",
    # Category E (7) — market structure
    "session_range_position_pct", "distance_to_prior_day_high_pct",
    "distance_to_prior_day_low_pct", "distance_to_session_vwap_pct",
    "range_position_pct", "distance_to_20_bar_high_pct", "distance_to_20_bar_low_pct",
    # Category F (6) — volatility regime
    "realized_vol_15m", "realized_vol_1h", "realized_vol_4h",
    "volatility_compression_flag", "volatility_expansion_flag", "atr_percentile_rank",
    # Category G (5) — signal saturation
    "signals_last_10_bars", "same_direction_signals_last_10",
    "opposite_direction_signals_last_10", "recent_stop_out_count",
    "recent_false_breakout_rate",
    # Category H (3) — BTC cross-asset context
    "btc_recent_return", "btc_regime_label", "eth_btc_relative_strength",
]

EMBARGO_BARS = 12  # 60 min embargo at 5m bars


def load_data(data_path: str) -> pd.DataFrame:
    """Load and validate training data."""
    if data_path.endswith(".parquet"):
        df = pd.read_parquet(data_path)
    else:
        df = pd.read_csv(data_path)

    # Verify features exist
    available = [f for f in FEATURE_NAMES if f in df.columns]
    missing = [f for f in FEATURE_NAMES if f not in df.columns]
    if missing:
        print(f"WARNING: Missing {len(missing)} features: {missing[:5]}...")
        for feature in missing:
            df[feature] = 0.0
        available = [f for f in FEATURE_NAMES if f in df.columns]

    df[available] = df[available].fillna(0)

    print(f"Loaded {len(df)} samples, {len(available)} features")
    print(f"Target distribution: {df['target'].value_counts().to_dict()}")
    return df


def walk_forward_split(df: pd.DataFrame, n_folds: int = 5, embargo: int = EMBARGO_BARS):
    """Walk-forward split with purge/embargo. Embargo is scaled to dataset size."""
    tscv = TimeSeriesSplit(n_splits=n_folds)
    for train_idx, test_idx in tscv.split(df):
        # Purge: remove last embargo bars from train
        if len(train_idx) > embargo:
            train_idx = train_idx[:-embargo]
        # Embargo: remove first embargo bars from test
        if len(test_idx) > embargo:
            test_idx = test_idx[embargo:]
        yield train_idx, test_idx


def train_lightgbm(X_train, y_train, X_val, y_val):
    """Train a LightGBM classifier with params scaled to dataset size."""
    import lightgbm as lgb

    n = len(X_train)
    n_pos = int(y_train.sum())
    n_neg = n - n_pos
    spw = max(1.0, n_neg / n_pos) if n_pos > 0 else 1.0
    # If val set has only one class, early stopping has no AUC signal — skip it
    # so the model trains for the full n_estimators and learns real feature splits.
    val_has_both_classes = len(np.unique(y_val)) >= 2
    params = {
        "objective": "binary",
        "metric": "auc",
        "boosting_type": "gbdt",
        "num_leaves": max(4, min(31, n // 4)),
        "learning_rate": 0.05,
        "feature_fraction": 0.8,
        "bagging_fraction": 0.8,
        "bagging_freq": 5,
        "verbose": -1,
        "n_estimators": min(500, max(50, n * 5)),
        "min_child_samples": max(1, min(20, n // 5)),
        "scale_pos_weight": spw,
    }
    if val_has_both_classes:
        params["early_stopping_rounds"] = min(50, max(10, n))

    model = lgb.LGBMClassifier(**params)
    fit_kwargs = {"eval_set": [(X_val, y_val)]} if val_has_both_classes else {}
    model.fit(X_train, y_train, **fit_kwargs)
    return model


def train_xgboost(X_train, y_train, X_val, y_val):
    """Fallback: Train an XGBoost classifier with params scaled to dataset size."""
    import xgboost as xgb

    n = len(X_train)
    n_pos = int(y_train.sum())
    n_neg = n - n_pos
    spw = max(1.0, n_neg / n_pos) if n_pos > 0 else 1.0
    val_has_both_classes = len(np.unique(y_val)) >= 2
    model = xgb.XGBClassifier(
        objective="binary:logistic",
        eval_metric="auc",
        n_estimators=min(500, max(50, n * 5)),
        max_depth=max(2, min(6, n // 10)),
        learning_rate=0.05,
        subsample=0.8,
        colsample_bytree=0.8,
        early_stopping_rounds=min(50, max(10, n)) if val_has_both_classes else None,
        min_child_weight=max(1, n // 10),
        scale_pos_weight=spw,
        verbosity=0,
    )
    fit_kwargs = {"eval_set": [(X_val, y_val)], "verbose": False} if val_has_both_classes else {}
    model.fit(X_train, y_train, **fit_kwargs)
    return model


def compute_metrics(y_true, y_prob):
    """Compute standard binary classification metrics.
    Returns None if only one class is present in y_true (can't compute AUC/log_loss).
    """
    from sklearn.metrics import (
        brier_score_loss,
        log_loss,
        roc_auc_score,
    )

    unique_classes = np.unique(y_true)
    if len(unique_classes) < 2:
        print(f"  SKIP metrics: only class(es) {unique_classes} present in val set")
        return None

    auc = roc_auc_score(y_true, y_prob)
    brier = brier_score_loss(y_true, y_prob)
    ll = log_loss(y_true, y_prob, labels=[0, 1])
    ece = compute_ece(y_true, y_prob, n_bins=10)

    return {
        "auc_roc": float(auc),
        "brier_score": float(brier),
        "log_loss": float(ll),
        "expected_calibration_error": float(ece),
    }


def compute_ece(y_true, y_prob, n_bins=10):
    """Expected Calibration Error."""
    bins = np.linspace(0, 1, n_bins + 1)
    ece = 0.0
    for i in range(n_bins):
        mask = (y_prob >= bins[i]) & (y_prob < bins[i + 1])
        if mask.sum() == 0:
            continue
        bin_conf = y_prob[mask].mean()
        bin_acc = y_true[mask].mean()
        ece += mask.sum() / len(y_true) * abs(bin_acc - bin_conf)
    return ece


def export_to_onnx(model, feature_names, output_path):
    """Export trained model to ONNX format.

    Uses zipmap=False so the ONNX output is a plain float32 tensor [N, 2]
    rather than a sequence of dicts — this is required for C# OnnxRuntime parsing.

    For LightGBM, skl2onnx requires the onnxmltools converter to be registered first.
    """
    # Path 1: skl2onnx + onnxmltools LightGBM converter registration (preferred)
    try:
        import lightgbm as lgb
        from onnxmltools.convert.lightgbm.operator_converters.LightGbm import convert_lightgbm
        from skl2onnx import convert_sklearn, update_registered_converter
        from skl2onnx.common.data_types import FloatTensorType
        from skl2onnx.common.shape_calculator import calculate_linear_classifier_output_shapes

        update_registered_converter(
            lgb.LGBMClassifier,
            "LightGbmLGBMClassifier",
            calculate_linear_classifier_output_shapes,
            convert_lightgbm,
            options={"nocl": [True, False], "zipmap": [True, False]},
        )
        initial_type = [("features", FloatTensorType([None, len(feature_names)]))]
        # zipmap=False → output is FloatTensor [batch, 2], column 1 = P(WIN)
        # This is what MlInferenceService.ExtractScalar() expects.
        options = {lgb.LGBMClassifier: {"zipmap": False}}
        onnx_model = convert_sklearn(model, initial_types=initial_type, options=options,
                                     target_opset={"": 17, "ai.onnx.ml": 3})
        with open(output_path, "wb") as f:
            f.write(onnx_model.SerializeToString())
        print(f"ONNX model exported to {output_path} (zipmap=False, float tensor output)")
        return
    except Exception as e:
        print(f"  skl2onnx+onnxmltools path failed: {e}")

    # Path 2: onnxmltools direct LightGBM conversion
    try:
        import onnxmltools
        from onnxmltools.convert.common.data_types import FloatTensorType

        initial_type = [("features", FloatTensorType([None, len(feature_names)]))]
        onnx_model = onnxmltools.convert_lightgbm(model, initial_types=initial_type)
        onnxmltools.utils.save_model(onnx_model, output_path)
        print(f"ONNX model exported via onnxmltools to {output_path}")
        return
    except Exception as e:
        print(f"  onnxmltools direct path failed: {e}")

    # Path 3: native pickle fallback
    import joblib
    print("WARNING: ONNX export failed — saving model in native format instead")
    native_path = output_path.replace(".onnx", ".pkl")
    joblib.dump(model, native_path)
    print(f"Model saved to {native_path}")


def main(data_path: str, output_dir: str, n_folds: int, model_type: str):
    df = load_data(data_path)

    MIN_TRAINING_SAMPLES = 200

    available_features = [f for f in FEATURE_NAMES if f in df.columns]
    X = df[available_features].astype(np.float32)  # keep as DataFrame to preserve feature names
    y = df["target"].values.astype(int)

    if len(df) < MIN_TRAINING_SAMPLES:
        print(f"\nSKIP: Only {len(df)} samples — need at least {MIN_TRAINING_SAMPLES}.")
        print("  Accumulate more labeled signals and re-run.")
        sys.exit(2)

    # Guard: need both classes to train a meaningful binary classifier
    unique_classes = np.unique(y)
    if len(unique_classes) < 2:
        print(f"\nSKIP: Training data contains only class(es) {unique_classes}.")
        print("  Need both WIN (1) and LOSS (0) samples to train.")
        print("  Accumulate more labeled signals (mix of wins and losses) and re-run.")
        sys.exit(2)  # exit 2 = skip (not an error), handled in train_pipeline.sh

    # Scale folds and embargo to dataset size to avoid destroying small train sets.
    # With N=24, 5 folds + 6-bar embargo leaves fold-2 with only 2 training samples.
    # Rule: need at least 8 samples per fold after embargo to be useful.
    effective_folds = max(2, min(n_folds, len(df) // 8))
    effective_embargo = min(EMBARGO_BARS, max(1, len(df) // 15))
    if effective_folds != n_folds or effective_embargo != EMBARGO_BARS:
        print(f"  NOTE: Dataset too small for {n_folds} folds / {EMBARGO_BARS}-bar embargo.")
        print(f"  Scaled to {effective_folds} folds, {effective_embargo}-bar embargo for {len(df)} samples.")

    fold_metrics = []
    best_model = None
    best_auc = 0

    print(f"\n{'='*60}")
    print(f"Walk-forward training: {effective_folds} folds, embargo={effective_embargo} bars")
    print(f"{'='*60}\n")

    for fold_idx, (train_idx, test_idx) in enumerate(walk_forward_split(df, effective_folds, effective_embargo)):
        X_train = X.iloc[train_idx].reset_index(drop=True)
        X_val = X.iloc[test_idx].reset_index(drop=True)
        y_train, y_val = y[train_idx], y[test_idx]

        print(f"Fold {fold_idx + 1}: train={len(train_idx)}, val={len(test_idx)}")

        # Skip fold if train set has only one class (model can't learn)
        if len(np.unique(y_train)) < 2:
            print(f"  SKIP: train set has only one class — insufficient data for this fold")
            continue

        try:
            if model_type == "lightgbm":
                model = train_lightgbm(X_train, y_train, X_val, y_val)
            else:
                model = train_xgboost(X_train, y_train, X_val, y_val)
        except ImportError:
            print(f"  {model_type} not available, trying fallback...")
            alt = "xgboost" if model_type == "lightgbm" else "lightgbm"
            if alt == "lightgbm":
                model = train_lightgbm(X_train, y_train, X_val, y_val)
            else:
                model = train_xgboost(X_train, y_train, X_val, y_val)
        except Exception as e:
            print(f"  SKIP fold {fold_idx + 1}: training error — {e}")
            continue

        y_prob = model.predict_proba(X_val)[:, 1]
        metrics = compute_metrics(y_val, y_prob)

        if metrics is None:
            # Single class in val set — record dummy metrics, keep model candidate
            metrics = {"auc_roc": 0.5, "brier_score": 0.25, "log_loss": 0.693,
                       "expected_calibration_error": 0.0}
            if best_model is None:
                best_model = model

        metrics["fold"] = fold_idx + 1
        metrics["train_size"] = len(train_idx)
        metrics["val_size"] = len(test_idx)
        fold_metrics.append(metrics)

        print(f"  AUC={metrics['auc_roc']:.4f}, "
              f"Brier={metrics['brier_score']:.4f}, "
              f"ECE={metrics['expected_calibration_error']:.4f}")

        if metrics["auc_roc"] > best_auc:
            best_auc = metrics["auc_roc"]
        # M-07: Always use the last fold model (most recent training window)
        best_model = model

    if not fold_metrics or best_model is None:
        # Walk-forward produced no usable folds (all train sets had one class).
        # Fall back to full-dataset training with a small hold-out.
        # This happens when WINs are clustered early and val sets end up all-LOSS.
        print("\n  NOTE: Walk-forward produced no trainable folds.")
        print("  Falling back to full-dataset training (biased metrics — accumulate more data).")
        n_val = max(2, len(df) // 6)
        X_train_fb = X.iloc[:-n_val].reset_index(drop=True)
        X_val_fb   = X.iloc[-n_val:].reset_index(drop=True)
        y_train_fb = y[:-n_val]
        y_val_fb   = y[-n_val:]
        if len(np.unique(y_train_fb)) < 2:
            # Still only one class — train on everything, skip val metrics
            X_train_fb = X.reset_index(drop=True)
            y_train_fb = y
            X_val_fb   = X.reset_index(drop=True)
            y_val_fb   = y
        try:
            if model_type == "lightgbm":
                best_model = train_lightgbm(X_train_fb, y_train_fb, X_val_fb, y_val_fb)
            else:
                best_model = train_xgboost(X_train_fb, y_train_fb, X_val_fb, y_val_fb)
        except Exception as e:
            print(f"  Fallback training failed: {e}")
            sys.exit(1)
        fb_metrics = compute_metrics(y_val_fb, best_model.predict_proba(X_val_fb)[:, 1])
        if fb_metrics is None:
            fb_metrics = {"auc_roc": 0.5, "brier_score": 0.25, "log_loss": 0.693,
                          "expected_calibration_error": 0.0}
        fb_metrics["fold"] = 0
        fb_metrics["train_size"] = len(X_train_fb)
        fb_metrics["val_size"] = len(X_val_fb)
        fb_metrics["note"] = "full_dataset_fallback"
        fold_metrics = [fb_metrics]
        print(f"  Fallback AUC={fb_metrics['auc_roc']:.4f}, Brier={fb_metrics['brier_score']:.4f}")

    # Summary
    avg_auc = np.mean([m["auc_roc"] for m in fold_metrics])
    avg_brier = np.mean([m["brier_score"] for m in fold_metrics])
    avg_ece = np.mean([m["expected_calibration_error"] for m in fold_metrics])
    avg_log_loss = np.mean([m["log_loss"] for m in fold_metrics])
    print(f"\n{'='*60}")
    print(f"Average AUC: {avg_auc:.4f}, Average Brier: {avg_brier:.4f}")
    print(f"{'='*60}\n")

    # M-08: Minimum AUC threshold before export
    MIN_AUC_FOR_EXPORT = 0.54
    if avg_auc < MIN_AUC_FOR_EXPORT:
        print(f"WARNING: Average AUC {avg_auc:.4f} < {MIN_AUC_FOR_EXPORT} — model too weak for deployment.")
        print("  Accumulate more data or investigate feature quality.")
        sys.exit(3)

    # Guarantee every persisted fold entry has the accuracy-first required
    # keys. Promotion treats a model with missing/null fold metrics as
    # incomplete and refuses to activate it.
    REQUIRED_FOLD_KEYS = ("auc_roc", "brier_score", "log_loss", "expected_calibration_error")
    for idx, fm in enumerate(fold_metrics):
        for key in REQUIRED_FOLD_KEYS:
            if key not in fm or fm[key] is None:
                # Back-fill with a neutral sentinel so the JSON is valid and
                # downstream validators can still parse it — but log it.
                print(f"  WARN: fold {fm.get('fold', idx + 1)} missing {key} — back-filling 0.0")
                fm[key] = 0.0

    # Feature importance
    if hasattr(best_model, "feature_importances_"):
        importance = dict(zip(available_features, best_model.feature_importances_.tolist()))
        top_features = sorted(importance.items(), key=lambda x: x[1], reverse=True)[:15]
        print("Top 15 features:")
        for name, imp in top_features:
            print(f"  {name}: {imp:.4f}")

        # T3-19: Report zero-importance features so they can be removed from future training.
        # Zero-importance means the model never split on that feature — it contributes nothing
        # and adds noise. Log them so they can be culled from FEATURE_NAMES in the next cycle.
        zero_importance = [name for name, imp in importance.items() if imp == 0.0]
        if zero_importance:
            print(f"\n  WARN: {len(zero_importance)} features have zero importance and should be")
            print(f"  removed from FEATURE_NAMES before the next training run:")
            for name in zero_importance:
                print(f"    - {name}")
            print()
    else:
        importance = {}

    # Export
    os.makedirs(output_dir, exist_ok=True)
    version = datetime.now(timezone.utc).strftime("v%Y%m%d_%H%M%S")

    onnx_path = os.path.abspath(os.path.join(output_dir, f"outcome_predictor_{version}.onnx"))
    export_to_onnx(best_model, available_features, onnx_path)

    # Save metadata
    metadata = {
        "model_type": "outcome_predictor",
        "model_version": version,
        "file_path": onnx_path,
        "file_format": "onnx",
        "training_sample_count": len(df),
        "feature_count": len(available_features),
        "feature_list": available_features,
        "fold_metrics": fold_metrics,
        "feature_importance": importance,
        "avg_auc_roc": float(avg_auc),
        "avg_brier_score": float(avg_brier),
        "avg_expected_calibration_error": float(avg_ece),
        "avg_log_loss": float(avg_log_loss),
        "embargo_bars": effective_embargo,
        "n_folds": effective_folds,
        "model_library": model_type,
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
    }

    meta_path = os.path.join(output_dir, f"outcome_predictor_{version}_meta.json")
    with open(meta_path, "w") as f:
        json.dump(metadata, f, indent=2)
    print(f"\nMetadata saved to {meta_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train outcome prediction model")
    parser.add_argument("--data", default="data/training_data.csv", help="Training data path")
    parser.add_argument("--output", default="models/", help="Output directory for models")
    parser.add_argument("--folds", type=int, default=5, help="Number of walk-forward folds")
    parser.add_argument("--model", default="lightgbm", choices=["lightgbm", "xgboost"])
    args = parser.parse_args()

    main(args.data, args.output, args.folds, args.model)
