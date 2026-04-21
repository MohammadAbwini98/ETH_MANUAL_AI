# Title
generated signal failure investigation

## Date
2026-04-21 13:44 Asia/Amman

## User Request
Check why generated signals were failing.

## Scope Reviewed
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Infrastructure/Trading/ExecutionCandidateMapper.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Web/appsettings.json`
- live PostgreSQL data in `ETH.signal_decision_audit`
- live PostgreSQL data in `ETH.trade_execution_queue`
- live PostgreSQL data in `ETH.executed_trades`

## Key Findings
- Recent generated signals were created and queued successfully by `auto-executor`; they did not fail at signal generation time.
- The actual runtime failure was broker-execution validation rejection with `EntryDriftExceeded`.
- Recent generated queue rows and executed-trade rows show the same rejection reason and message.
- The failure is triggered because the policy requires both percentage drift and absolute USD drift to stay within bounds, and the absolute `EntryPriceMarginUsd` limit is currently only `1.0`.
- Recent generated candidates were about `$2.89` to `$3.78` away from the executable market price, so they failed even though the percentage drift was only about `0.12%` to `0.16%`.
- One audited generated decision row still showed `outcome_category = SIGNAL_GENERATED` together with `lifecycle_state = RISK_BLOCKED` and a populated `final_block_reason`; that is an audit-state inconsistency worth follow-up, but it was not the immediate cause of the execution failures observed in the queue/trade records.

## Root Cause
- Primary root cause: execution-policy rejection in `TradeExecutionPolicy.EvaluateAsync` due to `EntryDriftExceeded`.
- Contributing design behavior: generated candidates are mapped directly into broker execution with persisted/reconstructed entry levels, so any fast move away from that entry is rejected before broker submission.
- Secondary note: there appears to be at least one inconsistent generated decision audit row where lifecycle state/final block reason do not align with outcome category.

## Files Reviewed
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Infrastructure/Trading/ExecutionCandidateMapper.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Web/appsettings.json`

## Files Changed
- none

## Implementation / Outcome
- Investigated recent generated failures in the live database.
- Confirmed generated items were enqueued by `auto-executor`.
- Confirmed failures occurred in validation before broker placement, not in signal generation/history loading.
- Confirmed the rejecting policy path is the entry-drift guard.

## Verification
- Queried recent generated rows from:
  - `ETH.signal_decision_audit`
  - `ETH.trade_execution_queue`
  - `ETH.executed_trades`
- Observed recent failures:
  - `signal_id=3d51264a-645d-419b-ba70-c3f95c764002` → `EntryDriftExceeded` (`0.12% / $2.89`)
  - `signal_id=9e5c0584-d980-4b33-bc9e-177973b5276b` → `EntryDriftExceeded` (`0.16% / $3.70`)
  - `signal_id=255cde52-fbee-4b0b-922c-3d423b352c66` → `EntryDriftExceeded` (`0.16% / $3.78`)
- Verified configured policy defaults in `appsettings.json`:
  - `EntryDriftTolerancePct = 0.005`
  - `EntryPriceMarginUsd = 1.0`
  - `EntryMode = NearRecommendedEntry`

## Risks / Notes
- `appsettings.json` currently says `CapitalTrading.Enabled = false` and `AutoExecuteEnabled = false`, but the live queue rows were created by `auto-executor`, so the running process likely had environment/runtime overrides at the time of execution.
- The inconsistent `SIGNAL_GENERATED` + `RISK_BLOCKED` audit row should be traced separately because it can blur generated-vs-blocked semantics even after the earlier history-filter fix.

## Current State
- Generated signals are being produced.
- Their recent failures are execution-policy validation failures before broker submission.
- The direct operational reason is that the executable market price moved more than `$1.00` away from the reconstructed generated entry.

## Next Recommended Step
- If generated auto-execution is intended to tolerate normal ETH movement, review whether `EntryPriceMarginUsd = 1.0` is too strict for generated entries or whether generated auto-execution should use a different entry mode/tolerance than recommended signals.
