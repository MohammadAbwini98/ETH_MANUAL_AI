# ETHUSD Startup Historical Candle Sync - Implementation Prompt

Implement the startup historical candle sync design for `ETHUSD` in this repository.

Use the implementation spec in:

- `SRS_docs/ETHUSD_Startup_Historical_Candle_Sync_Implementation.md`

Read the current code first and implement against the existing architecture. Do not create a parallel design. Reuse existing services, repository patterns, endpoint style, Serilog logging style, and test conventions already in the repo.

## Goal

Add startup-time historical candle synchronization so:

1. If a timeframe candle table is empty, the service fetches the last 30 days for that timeframe in chunked API calls that respect rate limits.
2. If the service was offline and restarts later, it computes the offline gap per timeframe and fills only the missing closed candles.
3. The sync state is visible through the backend, health endpoint, DB explorer, logs, and dashboard.

Supported timeframes:

- `1m`
- `5m`
- `15m`
- `30m`
- `1h`
- `4h`

## Important repository constraints

- Do not gate historical candle sync on `HighFreqTicks:UiPriceOnly`.
- `UiPriceOnly` must continue to affect live quote/tick acquisition only.
- Startup sync must run before `LiveTickProcessor` starts.
- Do not persist the currently open candle during startup sync.
- Use the existing `ICapitalClient.GetCandlesAsync(...)` contract and `Timeframe.ApiResolution`.
- Use existing `CandleRepository.BulkUpsertAsync(...)` semantics for idempotent writes.
- Preserve current coding style and existing DI / minimal API patterns.

## Required implementation

### 1. New startup sync service

Create a dedicated startup sync service, for example:

- `src/EthSignal.Infrastructure/Engine/HistoricalCandleSyncService.cs`

It must:

- plan sync per timeframe
- detect per-timeframe mode:
  - `EMPTY_BOOTSTRAP`
  - `OFFLINE_GAP_RECOVERY`
  - `NOOP`
- fetch history in chunked requests
- retry rate-limited and transient failures
- persist sync state after each chunk
- return a startup summary

### 2. Startup flow integration

Update:

- `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs`

So startup order becomes:

1. DB migrate
2. authenticate if historical sync is enabled
3. run startup historical candle sync
4. optional replay
5. live tick processor

If any mandatory timeframe fails after retries, do not start live processing.

### 3. Repository changes

Update:

- `src/EthSignal.Infrastructure/Db/ICandleRepository.cs`
- `src/EthSignal.Infrastructure/Db/CandleRepository.cs`

Add the methods needed to:

- count candles per timeframe and symbol
- get earliest/latest closed candles as needed for planning
- support per-timeframe status reporting

### 4. DB migration

Update:

- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`

Add a new table:

- `candle_sync_status`

This table must store persisted per-timeframe startup sync state including:

- symbol
- timeframe
- status
- sync mode
- whether the table was empty
- requested range
- last existing candle
- last synced candle
- offline duration seconds
- chunk progress
- last run timestamps
- last success timestamp
- last error

If you need a dedicated repository for this table, add:

- `src/EthSignal.Infrastructure/Db/ICandleSyncRepository.cs`
- `src/EthSignal.Infrastructure/Db/CandleSyncRepository.cs`

### 5. API and health visibility

Update:

- `src/EthSignal.Web/Program.cs`

Add:

- `GET /api/admin/candle-sync/status`
- optional `GET /api/admin/candle-sync/status/{timeframe}`

Extend:

- `/health`

So health includes a startup candle sync summary.

Also add:

- `candle_sync_status`

to the DB explorer allow-list.

### 6. Front-end visibility

Update:

- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`

Add a dashboard card for startup candle sync that shows:

- overall status
- number of ready/running/failed timeframes
- per-timeframe mode
- last synced candle
- offline duration
- chunk progress
- error text if any

Poll the new backend status endpoint.

### 7. Configuration

Update config handling to support:

- `CapitalApi:HistoricalSyncEnabled`
- `CapitalApi:StartupHistoricalDays`
- `CapitalApi:HistoricalSyncChunkCandles`
- `CapitalApi:HistoricalSyncChunkDelayMs`
- `CapitalApi:HistoricalSyncMaxRetries`
- `CapitalApi:HistoricalSyncRetryBaseDelayMs`

Use `StartupHistoricalDays = 30` for the initial implementation.

### 8. Logging

Add structured logs for:

- startup sync start
- per-timeframe plan
- chunk start
- chunk completion
- retry / rate limit events
- timeframe completion
- timeframe failure
- final startup sync summary

Use the repo’s current logging style and Serilog conventions.

### 9. Tests

Add or extend tests under:

- `tests/EthSignal.Tests/Engine`
- `tests/EthSignal.Tests/Infrastructure`
- `tests/EthSignal.Tests/Web`

Cover at least:

1. empty table -> `EMPTY_BOOTSTRAP`
2. stale non-empty table -> `OFFLINE_GAP_RECOVERY`
3. up-to-date table -> `NOOP`
4. chunk splitting logic
5. retry on `429`
6. persisted sync state
7. `/api/admin/candle-sync/status`
8. `/health` includes sync summary
9. `UiPriceOnly` does not disable historical sync

## Current files you should inspect before editing

- `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Infrastructure/Engine/BackfillService.cs`
- `src/EthSignal.Infrastructure/Db/CandleRepository.cs`
- `src/EthSignal.Infrastructure/Db/ICandleRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Apis/CapitalClient.cs`
- `src/EthSignal.Domain/Models/Timeframe.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Infrastructure/CandleRepositoryTests.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Behavioral requirements

- Use per-timeframe closed-boundary planning:
  - `currentOpenBoundary = tf.Floor(DateTimeOffset.UtcNow)`
  - only sync `OpenTime < currentOpenBoundary`
- For empty tables, fetch the last 30 days aligned to timeframe floor.
- For non-empty tables, sync only from `latestClosed + tf.Duration` to `currentOpenBoundary`.
- Do not rebuild populated tables from scratch.
- Keep writes idempotent.
- Persist progress after every chunk.

## Acceptance criteria

The implementation is complete only if all of the following are true:

1. Fresh startup with empty ETH candle tables fills 30 days for all six timeframes.
2. Restart after downtime fills only the missing closed-candle windows.
3. Historical sync still runs when `HighFreqTicks:UiPriceOnly = true`.
4. Live processing does not start until startup sync succeeds.
5. Sync status is visible in:
   - `/health`
   - `/api/admin/candle-sync/status`
   - dashboard UI
   - DB explorer
   - logs
6. Tests are added and pass for the new behavior.

## Deliverables

When finished:

1. summarize what changed
2. list every file added and modified
3. report what tests were run
4. report any remaining risks or follow-ups

