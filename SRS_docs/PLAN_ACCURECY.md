You are Claude, working inside the `ETH_MANUAL` repository.

Your task is to implement an accuracy-first improvement pass for signal prediction quality and ML prediction quality.

Primary goal:
Increase prediction accuracy first, even if signal frequency drops temporarily. Do not optimize for more trades in this task.

Known issues already identified:
- On April 11, 2026, training exported only 131 direct linked rows while diagnostics reported 414 labeled 5m outcomes.
- On April 11-12, 2026, ML diagnostics showed WARNING/CRITICAL feature drift.
- On April 12, 2026, runtime sometimes fell back to `heuristic-v1`, so live predictions were not consistently using the trained ONNX model.
- Recent model metadata files show missing or null fold metrics, which makes promotion decisions unreliable.

Instructions:
- Start by inspecting the current implementation and confirming the relevant code paths before editing.
- Then implement the changes end-to-end.
- Prefer small, safe, coherent changes over broad rewrites.
- Preserve backward compatibility where practical.
- Keep all new thresholds and behavior configurable rather than hardcoded when possible.
- After coding, run targeted tests/checks and summarize results.

Implementation scope:

1. Align diagnostics and training export counts
- Investigate and fix the mismatch between diagnostics labeled counts and training export counts.
- Make training export and diagnostics use the same practical definition of “trainable labeled samples.”
- In `ml/export_features.py`, keep the guarded proximity fallback logic, but make the pipeline automatically use it when direct linked rows are below 200.
- Add clear export/training summary output including:
  - `direct_linked_rows`
  - `fallback_rows`
  - `dropped_no_trade_rows`
  - `wins`
  - `losses`
  - `feature_version`
  - timeframe distribution
- If diagnostics and export counts diverge beyond a reasonable tolerance, emit a blocking warning or prevent promotion/registration.

Likely files:
- `ml/export_features.py`
- `ml/train_pipeline.sh`
- `src/EthSignal.Infrastructure/Engine/ML/MlDataDiagnosticsService.cs`
- `src/EthSignal.Infrastructure/Db/MlDataDiagnosticsRepository.cs`

2. Tighten model promotion and heuristic fallback safety
- Ensure heuristic fallback is never treated as a valid ACTIVE model.
- If no real ONNX model is available, heuristic may be used only for SHADOW logging/annotation behavior.
- Strengthen promotion gating so a model cannot be promoted unless all are true:
  - at least 200 clean labeled samples
  - at least 30 WIN and 30 LOSS samples
  - at least 50 calibration samples
  - Brier score <= 0.20
  - positive threshold lift
  - no CRITICAL feature drift
  - required fold metrics are present in metadata
- Surface clear block reasons for failed promotion.
- Expose whether the currently loaded model is ONNX or heuristic in diagnostics/health responses.

Likely files:
- `src/EthSignal.Infrastructure/Engine/ML/MlInferenceService.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MlModelPromotionService.cs`
- `src/EthSignal.Web/Program.cs`

3. Improve metadata and validation trust
- Ensure training artifacts consistently persist fold metrics in model metadata.
- Treat null or missing fold metrics as incomplete artifacts for promotion purposes.
- Improve validation output so it clearly compares:
  - rule-based score only
  - ML probability only
  - blended confidence
- Promotion should favor models that improve calibration and pass/fail separation on recent data, not just models that trained successfully.

Likely files:
- `ml/train_outcome_predictor.py`
- `ml/validate_model.py`
- `ml/register_model.py`

4. Make feature drift a real blocker in accuracy-first mode
- Strengthen training/promotion flow so CRITICAL feature drift blocks promotion.
- Add a compact train-vs-live drift summary to the training/promotion path.
- If recent-window retraining is too large for this pass, implement the blocking/reporting first and structure the code cleanly for future recent-window filtering.

5. Tighten runtime gating for accuracy-first behavior
- Keep dynamic threshold relaxation disabled unless the real trained model is healthy and calibrated.
- Make runtime gating modestly stricter for accuracy-first mode by raising the effective ML minimum win-probability gate into the 0.58-0.62 range, using configuration rather than hardcoding.
- Be more conservative in weak contexts such as:
  - neutral regime
  - low ADX
  - weak Asia-session conditions
- Do not add “more signals” behavior in this task.

Likely files:
- `src/EthSignal.Infrastructure/Engine/SignalEngine.cs`
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Infrastructure/Engine/ML/SignalFrequencyManager.cs`

6. Tests and verification
Add or update tests for:
- diagnostics/export count alignment behavior
- promotion blocked on CRITICAL drift
- promotion blocked when fold metrics are missing
- heuristic model cannot be treated as ACTIVE
- diagnostics/health fields exposing model format, heuristic state, and promotion block reason
- stricter runtime ML gate behavior

Likely test areas:
- `tests/EthSignal.Tests/Engine/ML/*`
- `tests/EthSignal.Tests/Web/*`

Acceptance criteria:
- Heuristic fallback is never treated as an accuracy-approved ACTIVE model.
- Promotion is blocked when drift is CRITICAL or required metrics are missing.
- Training/export output clearly shows direct vs fallback row counts.
- Diagnostics and export counts are materially more consistent, and mismatches are explicit.
- ML diagnostics/health expose model format, heuristic status, and promotion block reason.
- Runtime gating is stricter for accuracy-first mode and remains configurable.
- Relevant tests/checks pass.

Execution requirements:
- Inspect first, then implement.
- Do not stop at analysis.
- Carry the work through code changes, tests, and a final summary.

Final response format:
1. Summary of what changed
2. Files touched
3. Tests/checks run
4. Remaining risks or follow-up items
