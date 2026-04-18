"""
register_model.py — Register a trained ML model directly in the DB.

Bypasses the API (which may not be running yet) and writes the model
record directly to the ETH.ml_models table via SQLAlchemy.

Usage:
    python register_model.py --meta models/outcome_predictor_v*_meta.json
"""

import argparse
import json
import os
import sys
from datetime import datetime, timezone

from sqlalchemy import create_engine, text


def get_connection_string() -> str:
    conn = os.environ.get("PG_CONNECTION")
    if not conn:
        print("ERROR: PG_CONNECTION environment variable not set", file=sys.stderr)
        sys.exit(1)
    if conn.startswith("Host="):
        parts = dict(p.split("=", 1) for p in conn.split(";") if "=" in p)
        host = parts.get("Host", "localhost")
        port = parts.get("Port", "5432")
        db = parts.get("Database", "postgres")
        user = parts.get("Username", "postgres")
        pwd = parts.get("Password", "")
        return f"postgresql://{user}:{pwd}@{host}:{port}/{db}"
    return conn


def register(meta_path: str) -> None:
    with open(meta_path) as f:
        meta = json.load(f)

    model_version = meta.get("model_version", "")
    file_path = meta.get("file_path", "")
    file_format = meta.get("file_format", "onnx")
    model_type = meta.get("model_type", "outcome_predictor")

    if not model_version or not file_path:
        print("ERROR: meta JSON missing model_version or file_path")
        sys.exit(1)

    if not os.path.exists(file_path):
        print(f"ERROR: Model file not found: {file_path}")
        sys.exit(1)

    # Accuracy-first quality gates — aligned with C# MlModelPromotionService.
    # Any gate failure is a hard REJECT; we never want incomplete/weak artifacts
    # landing in ml_models where auto-promotion could pick them up.
    sample_count = meta.get("training_sample_count", 0)
    avg_auc = meta.get("avg_auc_roc", 0.0)
    avg_brier = meta.get("avg_brier_score", 1.0)
    avg_ece = meta.get("avg_expected_calibration_error")
    fold_metrics = meta.get("fold_metrics", [])

    if sample_count < 200:
        print(f"REJECT: Training samples {sample_count} < 200 — not registering.")
        sys.exit(1)
    if avg_auc < 0.58:
        print(f"REJECT: AUC-ROC {avg_auc:.4f} < 0.58 — not registering.")
        sys.exit(1)
    if avg_brier > 0.30:
        print(f"REJECT: Brier score {avg_brier:.4f} > 0.30 — not registering.")
        sys.exit(1)
    if avg_ece is not None and avg_ece > 0.25:
        print(f"REJECT: Expected calibration error {avg_ece:.4f} > 0.25 — not registering.")
        sys.exit(1)

    # Fold metrics must be present and complete. C# promotion gate treats
    # null/empty fold metrics as an incomplete artifact, so reject here too.
    if not isinstance(fold_metrics, list) or len(fold_metrics) == 0:
        print("REJECT: fold_metrics missing or empty — incomplete artifact.")
        sys.exit(1)
    required_keys = ("auc_roc", "brier_score", "log_loss", "expected_calibration_error")
    missing = []
    for idx, fm in enumerate(fold_metrics):
        if not isinstance(fm, dict):
            missing.append(f"fold #{idx+1}: not an object")
            continue
        for k in required_keys:
            if k not in fm or fm[k] is None:
                missing.append(f"fold {fm.get('fold', idx+1)}: {k}")
    if missing:
        print("REJECT: incomplete fold_metrics — " + "; ".join(missing))
        sys.exit(1)

    engine = create_engine(get_connection_string())
    now = datetime.now(timezone.utc)

    insert_sql = text("""
        INSERT INTO "ETH".ml_models (
            model_type, model_version, file_path, file_format,
            train_start_utc, train_end_utc, training_sample_count, feature_count,
            feature_list_json, fold_metrics_json, feature_importance_json,
            auc_roc, brier_score, ece, log_loss, status, created_at_utc
        ) VALUES (
            :model_type, :model_version, :file_path, :file_format,
            :train_start_utc, :train_end_utc, :training_sample_count, :feature_count,
            CAST(:feature_list_json AS jsonb), CAST(:fold_metrics_json AS jsonb),
            CAST(:feature_importance_json AS jsonb),
            :auc_roc, :brier_score, :ece, :log_loss, 'candidate', :created_at_utc
        )
        ON CONFLICT (model_version) DO UPDATE SET
            file_path = EXCLUDED.file_path,
            auc_roc = EXCLUDED.auc_roc,
            brier_score = EXCLUDED.brier_score,
            ece = EXCLUDED.ece,
            log_loss = EXCLUDED.log_loss,
            fold_metrics_json = EXCLUDED.fold_metrics_json,
            feature_importance_json = EXCLUDED.feature_importance_json
        RETURNING id
    """)

    params = {
        "model_type": model_type,
        "model_version": model_version,
        "file_path": file_path,
        "file_format": file_format,
        "train_start_utc": now,
        "train_end_utc": now,
        "training_sample_count": meta.get("training_sample_count", 0),
        "feature_count": meta.get("feature_count", 0),
        "feature_list_json": json.dumps(meta.get("feature_list", [])),
        "fold_metrics_json": json.dumps(meta.get("fold_metrics", [])),
        "feature_importance_json": json.dumps(meta.get("feature_importance", {})),
        "auc_roc": meta.get("avg_auc_roc", 0.0),
        "brier_score": meta.get("avg_brier_score", 1.0),
        "ece": meta.get("avg_expected_calibration_error", None),
        "log_loss": meta.get("avg_log_loss", None),
        "created_at_utc": now,
    }

    with engine.begin() as conn:
        row = conn.execute(insert_sql, params).fetchone()
        model_id = row[0]

    print(f"Model {model_version} registered in DB as candidate (id={model_id})")
    print(f"  AUC={meta.get('avg_auc_roc', 0):.4f}, "
          f"Brier={meta.get('avg_brier_score', 1):.4f}, "
          f"ECE={meta.get('avg_expected_calibration_error', 0):.4f}, "
          f"Samples={meta.get('training_sample_count', 0)}, "
          f"Folds={meta.get('n_folds', 0)}")
    print(f"  To activate after shadow validation:")
    print(f"    curl -X POST http://localhost:5234/api/admin/ml/models/{model_id}/activate")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Register ML model in DB")
    parser.add_argument("--meta", required=True, help="Model metadata JSON path")
    args = parser.parse_args()
    register(args.meta)
