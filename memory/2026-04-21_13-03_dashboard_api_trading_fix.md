# Title
dashboard api trading fix

## Date
2026-04-21 13:03 Asia/Amman

## User Request
Fix the dashboard/API/trading issues from the latest audit report, preserve source-type safety, restore Web API integration tests, and provide implementation notes.

## Scope Reviewed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Db/DecisionAuditRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`

## Key Findings
- Generated-history contamination was caused by generated-history queries accepting any BUY/SELL decision without requiring `outcome_category = SIGNAL_GENERATED`.
- The latest dashboard card was built from an unlinked latest signal plus an unrelated latest decision, and the frontend then let a newer decision override the signal display.
- The blocked/no-trade timeframe blank state came from `dashboard.js` expecting `decision.timeframe` while the API omitted it.
- History totals and pagination were misleading because the frontend loaded partial bulk subsets and paged them client-side.
- Blocked/generated execution status rendering collapsed actual `OPEN` rows into `PENDING`.
- `ApiEndpointTests` were broken by a no-op `IDbMigrator` mock with no completed task plus startup warmup hitting real DB-backed parameter initialization.
- Fresh test databases were also missing several `signal_decision_audit` columns used by `DecisionAuditRepository.InsertDecisionAsync`.

## Root Cause
- Generated path defect: repository query bug plus service-layer source mixing.
- Latest card defect: API contract mismatch plus frontend override bug.
- Blank timeframe defect: API contract mismatch.
- History truncation defect: frontend pagination design relying on subset loads instead of server totals/pages.
- OPEN to PENDING defect: frontend lifecycle mapping bug.
- API test failure: test fixture misconfiguration plus startup warmup side effects during `Testing`.
- Fresh DB insert failure: migrator/schema drift for `signal_decision_audit`.

## Files Reviewed
- `src/EthSignal.Infrastructure/Db/IDecisionAuditRepository.cs`
- `src/EthSignal.Infrastructure/Db/DecisionAuditRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`

## Files Changed
- `src/EthSignal.Infrastructure/Db/IDecisionAuditRepository.cs`
- `src/EthSignal.Infrastructure/Db/DecisionAuditRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionAbstractions.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `tests/EthSignal.Tests/Infrastructure/DecisionHistorySourceIsolationTests.cs`

## Implementation / Outcome
- Added `GetDecisionByEvaluationIdAsync` so `/api/dashboard/latest` can pair a signal with its linked decision by evaluation id instead of mixing unrelated snapshots.
- Tightened generated-history selection to true generated decisions only and preserved that source identity through API/dashboard usage.
- Extended `/api/dashboard/latest` to return a linked `decision`, a separate `latestDecision` when different, and explicit decision contract fields including `timeframe` and `evaluationId`.
- Updated trading health output with required demo-account match metadata so the dashboard safety badge no longer depends on a hardcoded `DEMOAI` string.
- Switched dashboard history sections from subset-only client paging to server-backed pagination with server totals.
- Fixed blocked/generated lifecycle rendering so `OPEN` remains `OPEN`.
- Restored API test host startup by using a no-op migrator implementation, adding parameter repository/provider test doubles, and skipping production startup warmup in `Testing`.
- Added missing `signal_decision_audit` columns to the migrator so fresh test databases match repository insert usage.
- Added source-isolation tests and dashboard latest-contract coverage.

## Verification
- `node --check src/EthSignal.Web/wwwroot/js/dashboard.js`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-restore --filter "FullyQualifiedName~DecisionHistorySourceIsolationTests"`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-restore --filter "FullyQualifiedName~ApiEndpointTests"`
- `dotnet test ETH_MANUAL.sln --no-restore`
- Final result: 415 passing tests, 0 failing tests.

## Risks / Notes
- The worktree already contained unrelated user changes before this task; they were left intact.
- Dashboard history stats now use server totals/pages, but the UI still depends on existing endpoint shapes and polling cadence.
- Testing warmup is skipped only for `Testing`; production startup behavior remains intact.

## Current State
- Generated history excludes blocked decisions and no longer feeds source-unsafe rows into the generated execution/dashboard path.
- Dashboard latest-card rendering uses linked signal/decision data and no longer lets unrelated later decisions suppress a valid current signal.
- Blocked/no-trade timeframe now has explicit API support.
- Demo-account safety rendering follows backend account-safety metadata rather than a hardcoded name.
- API integration tests start normally and assert real endpoint behavior again.

## Next Recommended Step
- Add a focused integration test around `TradeAutoExecutionService` proving generated auto-execution rejects blocked-source decisions even when audit data volume grows.
