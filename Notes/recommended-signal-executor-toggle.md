# Recommended Signal Executor Toggle

## Summary

This change adds a portal-managed global configuration flag:

- `RecommendedSignalExecutionEnabled`

It is persisted in the existing `ETH.portal_overrides` JSON settings record and controls only the **automatic execution of recommended signals**.

## Files Changed

- `src/EthSignal.Infrastructure/Db/IPortalOverridesRepository.cs`
- `src/EthSignal.Infrastructure/Db/PostgresPortalOverridesRepository.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`
- `src/EthSignal.Web/Program.cs`
- `portal/server.js`
- `portal/public/index.html`
- `tests/EthSignal.Tests/Web/TradeAutoExecutionServiceTests.cs`
- `tests/EthSignal.Tests/Infrastructure/PostgresPortalOverridesRepositoryTests.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Where The Toggle Is Persisted

- Table: `ETH.portal_overrides`
- JSON key: `recommendedSignalExecutionEnabled`

The existing portal overrides repository was extended instead of introducing a separate configuration path.

## Where Execution Is Gated

- `TradeAutoExecutionService`

The gate is enforced only when the background auto-executor is gathering **recommended** execution candidates. This means:

- recommended signals still generate normally
- recommended signals still appear in dashboard/history normally
- manual execution endpoints are not disabled by this flag
- generated execution remains unchanged
- blocked execution remains unchanged

## Runtime Refresh Behavior

- The auto-executor reads `IPortalOverridesRepository.GetAsync()` on each poll cycle.
- Portal changes therefore become effective without a restart.
- The portal API also persists through the shared overrides mechanism, so the same source of truth is used across portal and web runtime.

## Dependencies Considered

- Existing portal/global configuration storage already used `ETH.portal_overrides`
- Existing blocker save behavior was updated to **merge** settings instead of replacing the JSON payload, preventing this new flag from wiping blocker values or vice versa
- Recommended / generated / blocked execution all share the same execution pipeline, so the safest insertion point was **before candidate collection for recommended auto-execution only**

## Notes

- Default behavior remains effectively **enabled** when no override exists, preserving the current recommended auto-execution behavior.
- Logging was added for:
  - loading the global config through admin API
  - changing the toggle
  - transition to recommended auto-execution enabled/disabled
  - recommended auto-execution candidates skipped while the toggle is OFF
