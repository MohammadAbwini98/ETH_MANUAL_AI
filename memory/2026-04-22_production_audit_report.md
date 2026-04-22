# ETH_MANUAL Production Audit Report

Date: 2026-04-22
Workspace: ETH_MANUAL
Service: EthSignal.Web on port 5234

## 1. Executive Verdict

The system is materially closer to production-grade, but it is not yet fully production-ready.

Two real control-path defects were identified and fixed:

- Active parameter truth was being mutated by legacy runtime normalization that injected timeframe profiles.
- Higher-timeframe closed bars were evaluated against stale regime state because regime refresh happened after signal evaluation.

The live backend now self-heals the previously corrupted active parameter row on startup, and the active base set has been repaired.

## 2. Scope

This audit covered:

- dynamic adaptive strategy behavior
- dynamic active parameter-set truth
- signal prediction and ML linkage
- signal generation and executor wiring
- the root cause of observed 1m dominance
- blockers preventing other timeframes from functioning correctly

## 3. System Trace Summary

The runtime path was traced end to end:

- live ticks enter `LiveTickProcessor`
- 1m candles are built from tick flow
- higher timeframes are aggregated from closed 1m candles
- indicators and regimes are computed per timeframe
- `SignalEngine` evaluates closed-bar and running-candle opportunities
- decisions and signals are persisted
- execution candidates are filtered by execution policy and queued for broker execution

## 4. Dynamic Adaptive Strategy

The adaptive strategy is real, not cosmetic.

- Runtime adaptive overlays are applied through `MarketAdaptiveParameterService`.
- Adaptive state is persisted and rehydrated on restart.
- The live parameter overview currently reports `adaptiveEnabled=true`, `timeframeSetupCount=6`, and primary condition `NORMAL_MODERATE_LONDON_TIGHT_DRY`.

## 5. Dynamic Active Parameter Set

The dynamic active parameter-set mechanism is real and now restored to truthful behavior.

Before the fix, legacy startup/runtime normalization could create or preserve an active row whose timeframe profiles were auto-injected rather than operator-chosen. That broke the meaning of the active base set.

Current live state after the startup repair:

- `baseSetId=887`
- `createdBy=startup-repair`
- `parentParameterSetId=886`
- `notes=removed auto-injected timeframe profiles from legacy startup repair`
- `timeframeProfilesEmpty=true`
- `mlMode=SHADOW`

## 6. Signal Prediction / ML

The ML path is substantively wired, but live trade gating is not currently driven by a trained model.

- `SignalEngine` consumes ML thresholds when present.
- `MlInferenceService` supports a real ONNX model and dynamic threshold computation.
- The live service is currently running with heuristic fallback and `MlMode=SHADOW` because no promotable active trained model is loaded.

Conclusion: ML integration is real, but live predictive quality is not production-grade until a trained model is loaded and validated.

## 7. Signal Executor

The signal executor is real and wired through the broker path.

- Execution requests are mapped, validated, queued, and submitted through the Capital.com integration.
- Broker execution intentionally excludes `1m` signals.

That exclusion is policy, not a bug. It explains why `1m` recommendation volume and broker-executed trades should not be interpreted as the same thing.

## 8. Root Cause of 1m Dominance

The observed 1m dominance was not caused by a fake higher-timeframe architecture. The main causes were:

1. Higher-timeframe same-bar evaluations used stale regime state, which suppressed valid HTF signals.
2. The active base parameter truth had been corrupted by auto-hydrated timeframe profiles, making runtime behavior diverge from intended configuration.
3. The broker execution layer excludes `1m` by design, which can confuse interpretation of signal-vs-execution distribution.

## 9. Fix 1: Stop Parameter Mutation

`StrategyParameters.EnsureProductionSafeDefaults()` now normalizes only legacy daily-loss-cap defaults and no longer injects timeframe profiles.

That preserves operator intent and keeps timeframe-profile hydration opt-in instead of silent.

## 10. Fix 2: Refresh HTF Regime Before HTF Signal Evaluation

`LiveTickProcessor` now refreshes the just-closed higher-timeframe regime before attempting same-bar signal generation.

That removes the stale-bias bug where the current 15m/30m/1h/4h bar could be scored against the previous regime state.

## 11. Fix 3: Startup Self-Heal for Existing Bad Rows

Because the live database already contained an auto-mutated active row, fixing source code alone was insufficient.

Startup now detects legacy repaired rows with the bad hydration signature and reverts their timeframe profiles to the empty default set, then activates the repaired replacement row.

## 12. Validation Performed

Focused validation completed successfully:

- `ApiEndpointTests` passed after the final startup-repair change.
- `StrategyParameters` tests passed for the no-auto-hydration regression.
- Earlier focused runs for signal-engine and execution paths passed during the fix cycle.
- `Program.cs` had no diagnostics after the final patch.
- The backend restarted successfully and the live admin endpoint confirmed repaired parameter truth.

## 13. Residual Risks

The remaining blockers to calling this fully production-grade are operational, not architectural:

- ML is still heuristic fallback in `SHADOW` mode.
- Higher-timeframe resurgence after the regime-ordering fix still needs longer live soak observation.
- Existing open-trade saturation and queue pressure were observed in the running environment and should be managed operationally.

## 14. Final Conclusion

The two most important correctness defects in the current audit scope were real, were root-cause issues, and are now fixed.

The dynamic adaptive strategy, active parameter infrastructure, signal engine, persistence, dashboard history, and broker execution path are genuine. The system was being distorted by startup/runtime parameter mutation and stale higher-timeframe regime ordering, not by an absent multi-timeframe design.

Current recommendation:

- Treat the runtime as structurally repaired.
- Do not treat it as fully production-ready until a real ML model replaces heuristic fallback and the post-fix timeframe distribution is rechecked after additional live runtime.