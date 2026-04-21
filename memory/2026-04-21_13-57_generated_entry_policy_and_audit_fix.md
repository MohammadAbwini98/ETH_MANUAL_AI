# Title
generated entry policy and audit fix

## Date
2026-04-21 13:57 Asia/Amman

## User Request
Implement all of the following:
1. relax generated entry-drift handling
2. give generated signals a different execution mode/tolerance than recommended
3. fix the `SIGNAL_GENERATED` + `RISK_BLOCKED` audit inconsistency

## Scope Reviewed
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Web/appsettings.json`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Infrastructure/Engine/SignalDecisionTransition.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionPolicyTests.cs`
- `tests/EthSignal.Tests/Engine/SignalDecisionTransitionTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`

## Key Findings
- Generated execution failures were happening in `TradeExecutionPolicy` on `EntryDriftExceeded`, not in signal generation itself.
- Existing execution policy treated recommended, generated, and blocked signals with the same entry-mode and drift settings.
- The audit inconsistency came from multiple `LiveTickProcessor` branches downgrading directional candidates to blocked states by changing lifecycle/final reason only, without normalizing `DecisionType` and `OutcomeCategory`.
- Legacy inconsistent rows can persist in `signal_decision_audit`, so generated-history queries also needed a lifecycle guard to stop those rows from appearing as generated recommendations.

## Root Cause
- Generated execution rejection: shared execution-policy settings were too strict for reconstructed generated entries.
- Audit inconsistency: service-layer transition bug in `LiveTickProcessor` block branches.
- Dashboard/generated history exposure risk: generated-history query trusted `outcome_category` alone and could include stale inconsistent rows.

## Files Reviewed
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Web/appsettings.json`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionPolicyTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`
- `tests/EthSignal.Tests/Engine/SignalDecisionTransitionTests.cs`

## Files Changed
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Web/appsettings.json`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Infrastructure/Engine/SignalDecisionTransition.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionPolicyTests.cs`
- `tests/EthSignal.Tests/Engine/SignalDecisionTransitionTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`

## Implementation / Outcome
- Added source-specific generated execution settings to the existing `CapitalTrading` policy model.
- Implemented generated-specific entry validation in `TradeExecutionPolicy`, while keeping recommended and blocked behavior unchanged unless explicitly configured.
- Configured app defaults so generated execution uses `GeneratedEntryMode = MarketNow` and relaxed generated drift thresholds.
- Added `SignalDecisionTransition.ToOperationalBlock(...)` and used it across `LiveTickProcessor` block paths so blocked generated candidates are persisted consistently as `NO_TRADE + OPERATIONAL_BLOCKED` while preserving `CandidateDirection`.
- Hardened generated-history queries to require `lifecycle_state = PERSISTED`, which prevents legacy `SIGNAL_GENERATED + RISK_BLOCKED` rows from surfacing as generated signals.
- Added tests for generated-specific policy behavior, decision normalization, and exclusion of inconsistent generated audit rows.

## Verification
- `node --check src/EthSignal.Web/wwwroot/js/dashboard.js`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-restore --filter "FullyQualifiedName~TradeExecutionPolicyTests|FullyQualifiedName~SignalDecisionTransitionTests|FullyQualifiedName~DecisionHistorySourceIsolationTests|FullyQualifiedName~ApiEndpointTests"`
- `dotnet test ETH_MANUAL.sln --no-restore`
- Final result: 421 passing tests, 0 failing tests.

## Risks / Notes
- The running app may still use environment overrides instead of `appsettings.json`; if so, the new generated policy values must also be reflected there.
- `GeneratedEntryMode = MarketNow` intentionally bypasses drift rejection for generated signals, so generated execution is now more permissive by design.
- Generated history now excludes non-persisted generated rows, which is the desired source-safe behavior for dashboard/execution use.

## Current State
- Generated signals can use a different execution mode and wider drift policy than recommended signals.
- Recommended signal drift validation remains unchanged.
- Blocked generated decisions are normalized consistently in audit persistence.
- Legacy inconsistent generated audit rows no longer leak into generated history or generated execution lookup.

## Next Recommended Step
- If you want tighter operational control, add portal/admin visibility for generated-specific execution policy values so they can be tuned without editing config files.
