# Capital.com Demo Execution Implementation

## What Was Extended

- `src/EthSignal.Infrastructure/Apis/CapitalClient.cs`
  - Extended the existing Capital client with demo trading operations:
    - session/demo readiness guard
    - market lookup
    - account summary
    - place position
    - deal confirmation
    - open positions
    - close position
- `src/EthSignal.Web/Program.cs`
  - Added DI wiring and minimal APIs for execution, force-close, broker health, account summary, open positions, and execution stats.
- `src/EthSignal.Web/wwwroot/index.html`
  - Added the new `Executed Signals / Trades` dashboard section.
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
  - Added client-side loading, filtering, paging, diagnostics, and force-close behavior for executed trades.

## New Modules Added

- `src/EthSignal.Domain/Models/TradeExecutionModels.cs`
  - Internal execution contracts and models.
- `src/EthSignal.Infrastructure/Trading/*`
  - `ITradeExecutionService`
  - `ITradeExecutionPolicy`
  - `IExecutionCandidateMapper`
  - `IAccountSnapshotService`
  - `ICapitalTradingClient`
  - policy/service/runtime-state implementations
- `src/EthSignal.Infrastructure/Db/IExecutedTradeRepository.cs`
- `src/EthSignal.Infrastructure/Db/ExecutedTradeRepository.cs`
- `src/EthSignal.Web/BackgroundServices/TradeAutoExecutionService.cs`

## Signal To Order Flow

1. Existing recommended signals continue to be created by the current engine and persisted to `ETH.signals`.
2. Generated and blocked recommendation-shaped records continue to come from their current history reconstruction services.
3. `ExecutionCandidateMapper` converts recommended, generated, and blocked records into a normalized `TradeExecutionCandidate`.
4. `TradeExecutionService` consumes `TradeExecutionRequest` objects, not signal-engine internals.
5. `TradeExecutionPolicy` validates staleness, direction, TP/SL correctness, size, duplicate execution, demo-only guard, and broker/account state.
6. Only if policy passes does the service call the Capital trading client.
7. Order placement is not treated as success until Capital confirm succeeds.
8. Every attempt is persisted to execution tables for auditability.

## Demo-Only Safety

- Startup now rejects execution mode if the configured Capital base URL is not the demo environment.
- `TradeExecutionPolicy` also checks the resolved broker client/account snapshot and rejects non-demo execution.
- The dashboard only talks to repository-backed web APIs; it never calls Capital directly.
- The signal engine is not tightly coupled to broker placement. Execution remains an additive consumer layer.

## Persistence Added

- `ETH.executed_trades`
- `ETH.execution_attempts`
- `ETH.execution_events`
- `ETH.account_snapshots`
- `ETH.close_trade_actions`

## Notes

- Recommended signal execution uses the persisted `SignalRecommendation` row as the source of truth for entry, TP, SL, timeframe, and direction.
- Generated and blocked execution preserves the original source category all the way into persisted trade records and the dashboard.
- Auto-execution is configuration-driven and uses the same `ITradeExecutionService` path as manual execution.
