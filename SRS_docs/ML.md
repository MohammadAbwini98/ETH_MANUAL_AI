When ML Mode Switches
DISABLED → SHADOW (default at startup)
MlMode defaults to SHADOW in StrategyParameters.cs:117
Runs on every closed candle but predictions only log — they don't affect trading
If no trained model exists yet, a heuristic fallback activates automatically (still SHADOW only)
SHADOW → ACTIVE (manual promotion)
You control this. The recommended gate before switching to ACTIVE:

At least 50 shadow evaluations collected (MlShadowEvalCount = 50)
A real ONNX model is loaded (not heuristic fallback)
You call POST /api/admin/ml/models/{id}/activate — this hot-reloads the model and you then update MlMode in the DB to ACTIVE
ACTIVE → SHADOW (automatic downgrade)
The system auto-downgrades back to SHADOW if:

No real model is found at startup (MlInferenceService.cs:233-244)
Drift is detected: AUC drops below 0.52 or Brier score exceeds 0.28
When Training Runs
Training is manual — there is no auto-scheduler currently:


cd ml && bash train_pipeline.sh
Prerequisites before first run:

PG_CONNECTION env var set
200+ labeled samples in ml_feature_snapshots joined with signal_outcomes
Each signal takes ~20 min to be labeled (outcome determined after the trade closes)
So the timeline is:

App starts → collects live signals in SHADOW mode
After ~20 min per signal, outcomes get labeled in the DB
Once you have 200+ labeled outcomes → run train_pipeline.sh
Pipeline exports features → trains ONNX model → registers it as "Candidate" in DB
You validate it, then call the activate API → model goes ACTIVE if Brier ≤ 0.24
Retraining triggers (currently advisory, not automated):

Every 100 new signals (MlRetrainSignalThreshold)
Every 7 days max (MlRetrainMaxDays)
When drift detector fires (AUC/Brier thresholds crossed)
Bottom line: Right now you're in SHADOW mode accumulating data. You need to wait for ~200 labeled outcomes before training is even possible. That's your first milestone to watch.