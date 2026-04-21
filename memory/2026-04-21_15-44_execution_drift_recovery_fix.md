# Title
execution drift recovery fix

## Date
2026-04-21 15:44 Asia/Amman

## User Request
Fix the execution failure `Broker Error: Price drift 0.16% / 3.69 USD exceeds tolerance 0.50% and +/-1.00 USD`, find the root cause of failed trades, apply a suitable fix, and stop failed statuses from surfacing for handled execution issues.

## Scope Reviewed
- `memory/2026-04-21_13-44_generated_signal_failure_investigation.md`
- `memory/2026-04-21_13-57_generated_entry_policy_and_audit_fix.md`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionQueueService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Domain/Models/TradeExecutionModels.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeLifecycleReconciliationService.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionServiceTests.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionQueueServiceTests.cs`

## Key Findings
- The drift error was raised in `TradeExecutionPolicy`, but the larger defect was execution-path handling after that rejection.
- `TradeExecutionService` treated recoverable pre-broker issues as terminal by inserting `ValidationFailed` or `Failed` rows immediately.
- `TradeExecutionQueueService` only retried `MaxConcurrentOpenTrades`; other recoverable conditions were finalized and queue entries were marked `Failed`.
- Immediate naive requeue would loop inside the same drain cycle, so retry handling needed a deferred requeue step rather than instant `Queued` status.
- Deal-confirmation timeouts after broker submission were also being treated too harshly; those trades are safer to keep in `Submitted` for lifecycle reconciliation.

## Root Cause
- Primary root cause: API/service-layer execution handling bug.
- The quoted drift error was recoverable, but the service/queue stack treated it as terminal instead of automatically retrying with force-market execution.
- Secondary root cause: queue-state handling bug.
- Recovered or terminally handled execution outcomes were being represented as queue `Failed` even when the queue itself had not malfunctioned.
- Additional contributing cause: confirmation timeout handling was too strict and could produce false terminal failure pressure for broker-submitted trades.

## Files Reviewed
- `memory/2026-04-21_13-44_generated_signal_failure_investigation.md`
- `memory/2026-04-21_13-57_generated_entry_policy_and_audit_fix.md`
- `src/EthSignal.Domain/Models/TradeExecutionModels.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionPolicy.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionQueueService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeLifecycleReconciliationService.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionServiceTests.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionQueueServiceTests.cs`

## Files Changed
- `src/EthSignal.Domain/Models/TradeExecutionModels.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionService.cs`
- `src/EthSignal.Infrastructure/Trading/TradeExecutionQueueService.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionServiceTests.cs`
- `tests/EthSignal.Tests/Trading/TradeExecutionQueueServiceTests.cs`

## Implementation / Outcome
- Added retry metadata to `TradeExecutionResult` so execution can explicitly tell the queue when to retry and whether the retry must force market execution.
- Changed `TradeExecutionService` so `EntryDriftExceeded` requests are not persisted as failed trades; they now request a bounded retry with `ForceMarketExecution`.
- Changed transient policy exceptions such as Capital `429` rate-limit failures to request queue retry instead of creating terminal failed rows.
- Kept non-transient policy failures explicit as `ValidationFailed` rather than `Failed`.
- Changed broker confirmation timeout handling to keep the trade in `Submitted` and let lifecycle reconciliation confirm the final state.
- Changed `TradeExecutionQueueService` so handled business outcomes complete the queue entry instead of marking the queue row `Failed`.
- Added deferred retry scheduling inside the queue so retryable entries do not spin repeatedly in the same dispatch cycle.

## Verification
- `node --check src/EthSignal.Web/wwwroot/js/dashboard.js`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-restore --filter "FullyQualifiedName~TradeExecutionServiceTests|FullyQualifiedName~TradeExecutionQueueServiceTests|FullyQualifiedName~TradeExecutionPolicyTests|FullyQualifiedName~TradeLifecycleReconciliationServiceTests"`
- `dotnet test ETH_MANUAL.sln --no-restore`
- Final result: 426 passing tests, 0 failing tests.

## Risks / Notes
- Truly ambiguous broker placement failures that happen before a deal reference is returned are still the hardest class to recover automatically without broker-side idempotency support.
- `ExecutedTradeStatus.Failed` still exists in the model for genuinely unhandled broker execution faults and existing historical rows; this change removes it from the quoted drift path and other handled retry paths, but does not remove the enum from the system.
- The running environment may still use external overrides; if an override forces strict entry policy again, the new retry path will still protect the queue by switching retryable drift into forced market execution.

## Current State
- Entry drift no longer becomes an immediate terminal execution failure.
- Retryable policy issues are absorbed by the queue and retried automatically.
- Queue `Failed` is now reserved for queue corruption/crash scenarios rather than normal business rejections.
- Broker-submitted trades with delayed confirmation remain visible as `Submitted` and continue through reconciliation.

## Next Recommended Step
- Review live `executed_trades` and `trade_execution_queue` rows after the next generated/recommended execution window to confirm that new drift cases are retried to open/submitted states rather than ending in terminal queue failure.
