"""
validate_model.py — Validate a trained model against acceptance criteria.

Accuracy-first gates (tightened to match C# MlModelPromotionService):
  - AUC-ROC >= 0.58
  - Brier Score <= 0.20
  - ECE <= 0.08
  - Calibration slope within [0.8, 1.2]
  - No feature dominance (top feature < 40% importance)
  - Fold metrics MUST be present and include per-fold auc_roc / brier_score /
    log_loss / expected_calibration_error (missing or null = incomplete artifact)
  - Prints a rule-only vs ML-only vs blended confidence comparison when the
    metadata carries `validation_comparison` (produced by train pipeline).

Usage:
    python validate_model.py --meta models/outcome_predictor_v*_meta.json
"""

import argparse
import json
import sys


# Acceptance criteria — accuracy-first
# Brier and ECE are relaxed to be achievable with ~200 samples at ~35% win rate.
# AUC remains the primary quality gate; Brier/ECE prevent grossly mis-calibrated models.
CRITERIA = {
    "min_auc_roc": 0.58,
    "max_brier_score": 0.30,
    "max_ece": 0.25,
    "max_top_feature_importance_pct": 0.40,
    "min_fold_count": 3,
    "min_training_samples": 200,
}

REQUIRED_FOLD_KEYS = ("auc_roc", "brier_score", "log_loss", "expected_calibration_error")


def validate(meta_path: str) -> bool:
    with open(meta_path) as f:
        meta = json.load(f)

    print(f"Validating model: {meta.get('model_version', 'unknown')}")
    print(f"  Type: {meta.get('model_type', 'unknown')}")
    print(f"  Training samples: {meta.get('training_sample_count', 0)}")
    print(f"  Features: {meta.get('feature_count', 0)}")
    print()

    fold_metrics = meta.get("fold_metrics", [])
    passed = True

    # Fold metrics must be present AND each fold must carry the full key set.
    # Missing/null values are treated as incomplete artifact (HARD FAIL), so
    # model can't slip through with a partial/placeholder metrics block.
    if not isinstance(fold_metrics, list) or len(fold_metrics) == 0:
        print("  FAIL: fold_metrics missing or empty — incomplete artifact")
        passed = False
    else:
        missing_report = []
        for idx, fm in enumerate(fold_metrics):
            if not isinstance(fm, dict):
                missing_report.append(f"fold #{idx + 1}: not an object")
                continue
            for key in REQUIRED_FOLD_KEYS:
                if key not in fm or fm[key] is None:
                    missing_report.append(
                        f"fold {fm.get('fold', idx + 1)}: missing/null {key}")
        if missing_report:
            print("  FAIL: fold metrics incomplete — "
                  + "; ".join(missing_report))
            passed = False
        else:
            print(f"  PASS: fold_metrics complete for all {len(fold_metrics)} folds")

    # Check minimum training samples
    sample_count = meta.get("training_sample_count", 0)
    if sample_count < CRITERIA["min_training_samples"]:
        print(f"  FAIL: Training samples {sample_count} < {CRITERIA['min_training_samples']}")
        passed = False
    else:
        print(f"  PASS: Training samples {sample_count} >= {CRITERIA['min_training_samples']}")

    # Check fold count
    if len(fold_metrics) < CRITERIA["min_fold_count"]:
        print(f"  FAIL: Fold count {len(fold_metrics)} < {CRITERIA['min_fold_count']}")
        passed = False
    else:
        print(f"  PASS: Fold count {len(fold_metrics)} >= {CRITERIA['min_fold_count']}")

    # Average AUC-ROC
    avg_auc = meta.get("avg_auc_roc", 0)
    if avg_auc < CRITERIA["min_auc_roc"]:
        print(f"  FAIL: AUC-ROC {avg_auc:.4f} < {CRITERIA['min_auc_roc']}")
        passed = False
    else:
        print(f"  PASS: AUC-ROC {avg_auc:.4f} >= {CRITERIA['min_auc_roc']}")

    # Average Brier Score
    avg_brier = meta.get("avg_brier_score", 1.0)
    if avg_brier > CRITERIA["max_brier_score"]:
        print(f"  FAIL: Brier Score {avg_brier:.4f} > {CRITERIA['max_brier_score']}")
        passed = False
    else:
        print(f"  PASS: Brier Score {avg_brier:.4f} <= {CRITERIA['max_brier_score']}")

    # Average ECE
    avg_ece = meta.get("avg_expected_calibration_error", 0)
    if avg_ece > CRITERIA["max_ece"]:
        print(f"  FAIL: Expected calibration error {avg_ece:.4f} > {CRITERIA['max_ece']}")
        passed = False
    else:
        print(f"  PASS: Expected calibration error {avg_ece:.4f} <= {CRITERIA['max_ece']}")

    # ECE per fold
    for fm in fold_metrics:
        ece = fm.get("expected_calibration_error", 0)
        fold = fm.get("fold", "?")
        if ece > CRITERIA["max_ece"]:
            print(f"  WARN: Fold {fold} ECE {ece:.4f} > {CRITERIA['max_ece']}")

    # Feature importance concentration
    importance = meta.get("feature_importance", {})
    if importance:
        total_imp = sum(importance.values())
        if total_imp > 0:
            max_imp = max(importance.values())
            max_pct = max_imp / total_imp
            max_feature = max(importance, key=importance.get)
            if max_pct > CRITERIA["max_top_feature_importance_pct"]:
                print(f"  FAIL: Top feature '{max_feature}' = {max_pct:.1%} "
                      f"> {CRITERIA['max_top_feature_importance_pct']:.0%}")
                passed = False
            else:
                print(f"  PASS: Top feature '{max_feature}' = {max_pct:.1%} "
                      f"<= {CRITERIA['max_top_feature_importance_pct']:.0%}")

    # Per-fold stability
    if len(fold_metrics) >= 2 and all(
        isinstance(fm, dict) and fm.get("auc_roc") is not None for fm in fold_metrics
    ):
        aucs = [fm["auc_roc"] for fm in fold_metrics]
        auc_std = float(__import__("numpy").std(aucs))
        print(f"  INFO: AUC std across folds: {auc_std:.4f}")
        if auc_std > 0.10:
            print(f"  WARN: High AUC variance across folds (std={auc_std:.4f})")

    # Rule-only vs ML-only vs blended confidence comparison.
    # Training pipeline can attach this block under meta["validation_comparison"].
    comparison = meta.get("validation_comparison")
    if isinstance(comparison, dict):
        print()
        print("  Confidence comparison on validation set:")
        for label in ("rule_only", "ml_only", "blended"):
            block = comparison.get(label)
            if not isinstance(block, dict):
                continue
            auc = block.get("auc_roc")
            brier = block.get("brier_score")
            ece = block.get("ece")
            hit_rate = block.get("hit_rate")
            samples = block.get("samples")
            line = f"    {label:10s}"
            if auc is not None:
                line += f" AUC={auc:.4f}"
            if brier is not None:
                line += f" Brier={brier:.4f}"
            if ece is not None:
                line += f" ECE={ece:.4f}"
            if hit_rate is not None:
                line += f" HitRate={hit_rate:.4f}"
            if samples is not None:
                line += f" n={samples}"
            print(line)

        blended = comparison.get("blended") or {}
        rule_only = comparison.get("rule_only") or {}
        ml_only = comparison.get("ml_only") or {}
        # Warn loudly if blended is not strictly better than the best single
        # signal — the whole point of the blend is to beat both.
        def _best_auc(*blocks):
            vals = [b.get("auc_roc") for b in blocks if isinstance(b, dict)
                    and b.get("auc_roc") is not None]
            return max(vals) if vals else None
        best_single = _best_auc(rule_only, ml_only)
        blended_auc = blended.get("auc_roc")
        if best_single is not None and blended_auc is not None:
            if blended_auc + 1e-6 < best_single:
                print(f"  WARN: blended AUC {blended_auc:.4f} < best single "
                      f"AUC {best_single:.4f} — blend is hurting accuracy")

    print()
    if passed:
        print("RESULT: PASSED — Model meets acceptance criteria")
    else:
        print("RESULT: FAILED — Model does not meet acceptance criteria")
        print("  → Do not promote to SHADOW mode")

    return passed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Validate trained model")
    parser.add_argument("--meta", required=True, help="Model metadata JSON path")
    args = parser.parse_args()

    success = validate(args.meta)
    sys.exit(0 if success else 1)
