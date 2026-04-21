# Title
generated signal timeframe pricing and base set investigation

## Date
2026-04-21 23:09 +03

## User Request
Investigate why generated signals appear to be concentrated on the `1m` timeframe, why `Entry/TP/SL` prices are unrealistic, and why the dashboard still shows an old `Base Active Parameter Set` activation row (`#636`, `v3.1`, `adaptive-live`, `04/21 08:47 PM`).

## Scope Reviewed
- `logs/ethsignal-20260421.log`
- signal generation flow
- running-candle / scalp evaluation flow
- exit validation and pricing flow
- active parameter-set repository/provider path
- dashboard parameter-set overview contract
- adaptive runtime state service

## Key Findings
- The live engine does not only evaluate `1m`. It evaluates direct `1m` scalp signals and also evaluates `5m`, `15m`, `30m`, and `1h` on every `1m` close using running candles.
- The log shows higher-timeframe `SIGNAL_GENERATED` decisions for `5m`, `15m`, and `30m`, but many are immediately rejected by the exit engine as `Structure target too close`.
- The runtime DB used by the app is still throwing schema errors when persisting accepted signals: `column "evaluation_id" of relation "signals" does not exist`.
- The same log also contains older adaptive persistence behavior (`Persisted live parameter snapshot ...`) and early startup errors for missing `ETH.strategy_parameter_sets`, which indicates the runtime/database seen by the user is not fully aligned with the current repository state.
- The dashboard `Base Active Parameter Set` card is intentionally showing the global active row from `ETH.strategy_parameter_sets`; it is not the per-timeframe adaptive runtime state.

## Root Cause
- Apparent `1m` concentration is primarily caused by two combined issues:
  - higher-timeframe generated decisions are often rejected by `ExitEngine.Compute(...)` because structure targets are too close relative to the stop distance and the hard minimum reward/risk gate,
  - accepted signal inserts are failing against a stale runtime schema because the `signals` table is missing `evaluation_id`.
- Unrealistic `Entry/TP/SL` behavior is rooted in the current interaction between:
  - `RiskManager.EstimateLiveFillPrice(...)`,
  - structure-based target selection in `ExitEngine.Compute(...)`,
  - the hard `MinRewardToRisk` gate.
  This combination is still producing many mathematically valid but practically poor setups.
- The old `Base Active Parameter Set` row is shown because the dashboard reads the global active set from `strategy_parameter_sets`, and the current runtime DB still has an older active row created by older adaptive persistence behavior.

## Files Reviewed
- `logs/ethsignal-20260421.log`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/ParameterProvider.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/TimeframeStrategyProfiles.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`

## Files Changed
- `memory/2026-04-21_23-09_generated_signal_timeframe_pricing_and_base_set_investigation.md`

## Implementation / Outcome
- No production code changed in this investigation step.
- Confirmed that the dashboard card is reflecting the global base-set row by design.
- Confirmed that higher timeframes are being evaluated and are visible in the log.
- Confirmed that pricing realism remains a backend issue in the exit/pricing path, not a dashboard-only issue.
- Confirmed that a stale runtime schema is still interfering with accepted signal persistence.

## Verification
- Reviewed runtime logs for:
  - `SIGNAL_GENERATED`
  - `Structure target too close`
  - `evaluation_id`
  - `strategy_parameter_sets`
  - adaptive persistence traces
- Cross-checked the runtime behavior against:
  - `LiveTickProcessor`
  - `ExitEngine`
  - `RiskManager`
  - `ParameterRepository`
  - dashboard overview API / JS bindings
- No tests were run because this step was root-cause investigation only.

## Risks / Notes
- Any pricing fix must be done carefully because it directly affects signal validity and execution quality across all source types.
- The stale runtime DB/schema must be corrected before trusting generated-signal counts, persisted-signal history, or base-set metadata shown in the dashboard.
- The presence of older adaptive persistence traces in the current log suggests the app instance, DB, or deployment state is not fully synchronized with the current codebase.

## Current State
- `1m` is the most visible timeframe because:
  - it has a direct scalp path,
  - higher-timeframe provisional/closed-bar signals are frequently blocked by exit validation,
  - accepted inserts are also being lost to schema mismatch.
- `Entry/TP/SL` pricing is still too brittle in live conditions, especially when nearby structure compresses the achievable TP while stops remain ATR/structure buffered.
- `Base Active Parameter Set` still shows an older global active row from the runtime database rather than the newer per-timeframe adaptive runtime state.

## Next Recommended Step
- Fix the runtime database/schema alignment first.
- Then rework the pricing/exit path so higher timeframes do not collapse under structure-too-close rejections.
- After that, decide whether the global base active row should be cleaned/re-activated or whether the dashboard should surface a schema/runtime mismatch warning when the DB is stale.
