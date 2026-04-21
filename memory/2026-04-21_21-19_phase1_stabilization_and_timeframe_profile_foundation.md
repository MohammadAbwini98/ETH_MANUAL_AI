# Title
phase 1 stabilization and timeframe profile foundation

## Date
2026-04-21 21:19:31 +03

## User Request
Perform a full technical audit first, classify issues by severity, stabilize the repository, then improve signal pricing realism and add per-timeframe active parameter/strategy profile support without compromising trading safety.

## Scope Reviewed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Db/*`
- `src/EthSignal.Infrastructure/Engine/*`
- `src/EthSignal.Infrastructure/Engine/ML/*`
- `src/EthSignal.Infrastructure/Trading/*`
- `tests/EthSignal.Tests/*`
- prior project memory files in `/memory`

## Key Findings
- `Critical` `SignalRepository` had evolved to write evaluation/exit metadata while the migrated `ETH.signals` schema still lacked those columns in existing databases.
- `High` auto-execution was willing to queue historical/non-actionable recommended/generated/blocked signals instead of only live actionable candidates.
- `High` adaptive runtime overlays were still persisting and activating global parameter snapshots, creating cross-timeframe/global interference and blocking safe Phase 3 work.
- `High` parameter activation retired all active sets across all strategy versions and chose active sets nondeterministically.
- `Medium` generated/blocked recommendation reconstruction was still using older entry/exit reconstruction paths instead of the live timeframe-aware pricing/exit logic.
- `Medium` open-signal timeout handling and live signal evaluation had partial global-parameter assumptions instead of resolving by signal timeframe.
- `Medium` default non-empty timeframe profiles initially overrode operator-supplied base thresholds; this was corrected by making profile overrides opt-in.

## Root Cause
- Repository/model/schema evolution had drifted: write paths, read paths, and migrations were not updated together.
- Execution candidate sourcing was history-driven without enough lifecycle/outcome/expiry safety filters.
- Adaptive logic still had legacy persistence behavior that contradicted the intended transient-overlay design.
- Parameter activation logic was written as globally singleton rather than strategy-version scoped.
- Timeframe-aware behavior existed in multiple places, but the parameter and pricing path was not consistently resolved end-to-end.

## Files Reviewed
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/SignalRecommendation.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs`
- `src/EthSignal.Infrastructure/Engine/SignalEngine.cs`
- `src/EthSignal.Infrastructure/Engine/OutcomeEvaluator.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `tests/EthSignal.Tests/Engine/*`
- `tests/EthSignal.Tests/Infrastructure/*`
- `tests/EthSignal.Tests/Web/TradeAutoExecutionServiceTests.cs`

## Files Changed
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/TimeframeStrategyProfiles.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs`
- `src/EthSignal.Infrastructure/Engine/SignalEngine.cs`
- `src/EthSignal.Infrastructure/Engine/OutcomeEvaluator.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `tests/EthSignal.Tests/Engine/ML/AdaptiveAuditFixTests.cs`
- `tests/EthSignal.Tests/Engine/RiskManagerTests.cs`
- `tests/EthSignal.Tests/Engine/StrategyParametersTests.cs`
- `tests/EthSignal.Tests/Infrastructure/ParameterRepositoryTests.cs`
- `tests/EthSignal.Tests/Infrastructure/SignalRepositoryTests.cs`
- `tests/EthSignal.Tests/Web/TradeAutoExecutionServiceTests.cs`

## Implementation / Outcome
- Added missing `ETH.signals` migration columns for `evaluation_id`, TP ladder fields, RR, and exit metadata.
- Completed `SignalRepository` read-path mapping for persisted evaluation/exit metadata.
- Scoped `ParameterRepository` activation to the target strategy version and made active-set selection deterministic.
- Removed adaptive live snapshot persistence/activation so runtime adaptation stays transient and does not mutate the global active set.
- Added explicit `TimeframeStrategyProfileSet` support with safe default no-op profiles plus opt-in recommended bucket presets.
- Added timeframe-aware entry fill estimation, exit policy resolution, timeout handling, and open-signal evaluation across live and reconstructed signal flows.
- Filtered auto-execution to actionable candidates only:
  recommended must still be `OPEN`,
  generated/blocked must be directional, unexpired, and `PENDING`.
- Added regression coverage for repository schema/mapping, parameter activation isolation, adaptive no-persist behavior, auto-execution candidate filtering, timeframe profile resolution, and timeframe-aware entry pricing.

## Verification
- `dotnet build ETH_MANUAL.sln --no-restore`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-build --filter "FullyQualifiedName~AdaptiveAuditFixTests|FullyQualifiedName~TradeAutoExecutionServiceTests|FullyQualifiedName~RiskManagerTests|FullyQualifiedName~StrategyParametersTests|FullyQualifiedName~SignalRepositoryTests|FullyQualifiedName~ParameterRepositoryTests|FullyQualifiedName~DecisionHistorySourceIsolationTests"`
- `dotnet test ETH_MANUAL.sln --no-build`
- Final result: `437` tests passed, `0` failed.

## Risks / Notes
- Replay/backtest parity still uses some older risk/exit assumptions outside the live/reconstructed exit engine path and should be audited separately.
- Per-timeframe profiles are now supported inside the active parameter set, but no dedicated admin/portal editing UX was added in this step.
- Recommended timeframe profiles are available as an opt-in preset; the safe default remains no override to avoid breaking existing tuned global parameters.

## Current State
- Repository build and full test suite are green.
- Phase 1 high-risk blockers found in this pass are fixed.
- Entry/TP/SL handling is now more timeframe-aware in live and reconstructed flows.
- The parameter architecture now supports per-timeframe profile overrides without forcing global cross-timeframe side effects.

## Next Recommended Step
- Continue Phase 2 calibration by tuning concrete per-timeframe/profile values against historical outcome data, then expose the selected profile overrides through the existing active-parameter management path if operator control is needed.
