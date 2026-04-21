# Title
dashboard audit report

## Date
2026-04-21 12:20 Asia/Amman

## User Request
Review the project codebase, observe issues and bugs, especially dashboard binding issues, and provide a full report.

## Scope Reviewed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeLifecycleReconciliationService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionQueueService.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Db/ExecutedTradeRepository.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `src/EthSignal.Web/appsettings.json`

## Key Findings
- Generated-signal history currently queries every directional decision, not just true generated signals, which contaminates dashboard data and can leak blocked decisions into the generated execution path.
- `/api/dashboard/latest` returns a latest signal and a latest decision without shared linkage, while the dashboard client lets the newer decision override the signal card.
- The dashboard expects `decision.timeframe`, but the API does not send it.
- The trading summary hardcodes `DEMOAI` in the dashboard, so a valid configured demo account can render as `BLOCKED`.
- History sections fetch only fixed first pages (`500` / `2000`) and paginate client-side, so the dashboard silently truncates older rows.
- Blocked/generated history UI collapses `OPEN` execution state into `PENDING`.
- Web API integration tests are currently non-functional because the test fixture registers a bare `Mock.Of<IDbMigrator>()` while startup awaits `MigrateAsync()`, and startup exceptions are swallowed by `Program.cs`.

## Root Cause
- Root cause confirmed for generated/dashboard source mixing: `GeneratedSignalHistoryService` filters on `decision_type IN ('BUY', 'SELL')` instead of isolating generated outcome rows.
- Root cause confirmed for one dashboard binding bug: client expects `decision.timeframe`, but `/api/dashboard/latest` omits it.
- Root cause confirmed for dead API integration tests: `IDbMigrator.MigrateAsync()` is awaited during startup, but the test fixture provides an unconfigured Moq instance, which yields a null `Task`; the surrounding `try/catch` in `Program.cs` hides the startup exception and leaves TestServer unstarted.
- Additional dashboard risks are confirmed by code inspection, not by browser/runtime reproduction.

## Files Reviewed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Engine/GeneratedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Engine/BlockedSignalHistoryService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeLifecycleReconciliationService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionQueueService.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Db/ExecutedTradeRepository.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Files Changed
- `memory/2026-04-21_12-20_dashboard_audit_report.md`

## Implementation / Outcome
- No production code changed.
- Produced a static/end-to-end audit focused on dashboard contracts, signal-source separation, executed-trade visibility, and test coverage health.
- Confirmed multiple dashboard and audit-safety defects with line-level references for follow-up implementation.

## Verification
- Reviewed backend API endpoints and frontend bindings end to end.
- Ran `node --check src/EthSignal.Web/wwwroot/js/dashboard.js` successfully.
- Ran `dotnet test ETH_MANUAL.sln --no-restore`.
- Result: 372 passed, 40 failed.
- Failing group: `EthSignal.Tests.Web.ApiEndpointTests`.
- Failure mode: `The server has not been started or no web application was configured.`

## Risks / Notes
- The generated-source contamination is a trading-safety issue because source identity can drift in dashboard views, statistics, and auto-execution inputs.
- The dead Web API tests materially reduce confidence in dashboard/API regression safety.
- The working tree was already dirty before this audit; no attempt was made to revert or normalize unrelated changes.

## Current State
- Recommended, blocked, generated, and executed-trade flows exist separately in architecture, but generated history currently breaches source separation at query time.
- Dashboard latest-card rendering is vulnerable to mixed-snapshot decisions and one confirmed missing field.
- Demo/live safety is enforced server-side in key trading endpoints, but one dashboard indicator still hardcodes demo-account identity.
- API integration coverage exists on paper but is not currently exercising the app host.

## Next Recommended Step
- Fix the generated-history query first, then repair the dashboard/API contract for `/api/dashboard/latest`, then restore `ApiEndpointTests` so further dashboard fixes are regression-tested.
