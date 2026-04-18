# Accuracy Improvement Plan for Signal + ML Predictions

## Summary
Prioritize higher accuracy first, even if signal count drops temporarily.

Current evidence from this repo suggests the main accuracy bottlenecks are:
- On April 11, 2026, training exported only `131` direct linked rows even though diagnostics reported `414` labeled 5m outcomes.
- On April 11-12, 2026, diagnostics repeatedly showed `featureDrift=CRITICAL` or `WARNING`.
- On April 12, 2026, runtime fell back to `heuristic-v1`, so live predictions were sometimes not coming from the trained ONNX model.
- The live gate still uses a low recommended threshold (`45`) in logs, while calibration/sample quality was unstable.

## Implementation Changes
### 1. Fix training-data coverage before changing model logic
- Make `ml/export_features.py` and diagnostics use the same effective population definition for “trainable labeled samples”.
- Default the pipeline to include guarded proximity fallback when direct linked rows are below `200`, with strict caps already present.
- Add a training summary that prints:
  `direct linked`, `fallback linked`, `dropped no-trade`, `wins`, `losses`, `feature version`, and `timeframe mix`.
- Treat any large mismatch between diagnostics labeled count and export count as a warning/failure condition.

### 2. Reduce feature drift and keep live/training distributions aligned
- Split training by timeframe instead of one shared model if live 1m/15m/30m/1h behavior is materially different.
- Start with separate models or at minimum separate validation reports for `1m`, `5m`, and “higher TF”.
- Train on a rolling recent window first, not the full historical pool, to match current market structure.
- Exclude or downweight stale periods when diagnostics PSI is critical.
- Add a simple “train vs live feature drift” report into the pipeline so promotion is blocked when drift is critical.

### 3. Tighten model promotion and runtime fallback behavior
- Never auto-run `ACTIVE` with `heuristic-v1`; heuristic should remain `SHADOW` only.
- Block promotion unless all are true:
  at least `200` clean labeled samples,
  at least `30` wins and `30` losses,
  calibration sample count at least `50`,
  Brier `<= 0.20`,
  positive threshold lift,
  no critical feature drift.
- Persist and surface whether live predictions are from `onnx` or `heuristic`, and treat heuristic mode as not accuracy-eligible.
- Raise the live ML minimum win-probability gate from the current `0.55` only after calibration stabilizes; target first experiment range `0.58-0.62`.

### 4. Improve rule-based signal quality before widening frequency
- Keep “accuracy-first” by increasing selectivity in weak contexts:
  higher thresholds in `NEUTRAL` and low-ADX sessions,
  stricter Asia-session gating,
  stricter spread/ATR protection for marginal setups.
- Do not lower thresholds dynamically until the trained model is stable and calibrated.
- Use the existing adaptive tuner only for post-stability fine-tuning, not as the first lever.
- Review the highest-loss clusters by:
  timeframe,
  session,
  regime,
  ADX bucket,
  rule score band,
  ML probability band.
  Then hard-block the worst recurring bins.

### 5. Re-train with calibration as the main success metric
- Keep LightGBM/XGBoost, but optimize first for calibration and ranking stability, not just raw classification.
- Export and store fold metrics consistently in metadata; recent meta files showing null metrics should be treated as incomplete artifacts.
- Compare:
  baseline rule score only,
  ML probability only,
  blended confidence.
  Promote only if blended confidence improves pass/fail separation on recent data.
- Rebuild threshold lookup only from the same sample set used for the accepted calibrated model.

## Public APIs / Interfaces
- No trading API contract change is required.
- Extend diagnostics and training outputs to expose:
  `exported_direct_rows`,
  `exported_fallback_rows`,
  `dropped_no_trade_rows`,
  `active_model_format`,
  `is_heuristic_active`,
  `promotion_block_reason`.
- Treat missing fold metrics in model metadata as invalid for promotion.

## Test Plan
- Training export test: diagnostics labeled count and export sample count stay within an expected tolerance, or emit a blocking warning.
- Drift test: promotion is rejected when feature drift status is `CRITICAL`.
- Runtime test: no path allows `heuristic-v1` to operate in `ACTIVE`.
- Calibration test: promoted model has better Brier score and positive threshold lift versus previous active model.
- Signal-quality test: worst historical bins are blocked and backtest/live shadow results show lower false-positive rate.
- Regression test: ML fallback, linking, drift recording, and adaptive tuning still work after the stricter promotion rules.

## Assumptions and Defaults
- Default goal is higher accuracy, not more trades.
- Start with ETH only.
- Use recent-window training as the default if drift remains high.
- Keep dynamic threshold relaxation off until clean ONNX predictions are stable again.
- Prefer fewer, better-quality signals first; only expand frequency after calibration, drift, and export coverage are healthy.
