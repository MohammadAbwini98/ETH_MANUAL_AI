# ETHUSD Startup Historical Candle Sync Implementation

## 1. Objective

Implement startup-time historical candle synchronization for `ETHUSD` so the portal always has candle history for every supported timeframe (`1m`, `5m`, `15m`, `30m`, `1h`, `4h`) and the service can recover the exact offline gap after a restart.

Required behavior:

1. If a timeframe candle table is empty, fetch and persist the last 30 days for that timeframe in chunked requests that respect API rate limits.
2. If the service stopped and later restarted, calculate the offline duration per timeframe and fill only the missing closed-candle gap before live processing continues.
3. Expose the synchronization state to the portal, health endpoints, logging, and database explorer.

This document is written against the current codebase under:

- `src/EthSignal.Web`
- `src/EthSignal.Infrastructure`
- `src/EthSignal.Domain`
- `tests/EthSignal.Tests`

## 2. Current State

### 2.1 What exists today

- `DataIngestionService` starts DB migration, optional auth/backfill, optional replay, then `LiveTickProcessor`.
- `BackfillService` currently backfills only from `1m`, then derives higher timeframes from `1m` history.
- `BackfillService` uses `GetLatestClosedTimeAsync(Timeframe.M1, ...)` as the only resume anchor.
- `BackfillService` currently uses `CapitalApi:BackfillDays` and defaults to 7 days.
- `DataIngestionService` skips auth and backfill completely when `HighFreqTicks:UiPriceOnly = true`.
- The dashboard can read candles and health, but it has no explicit startup sync or offline recovery visibility.

### 2.2 Why this does not satisfy the requirement

The current startup flow does not guarantee:

- 30 days of history for all portal timeframes.
- per-timeframe empty-table bootstrap behavior.
- per-timeframe offline-gap calculation on restart.
- visibility of startup history sync progress and failures.
- historical sync when the system is in UI-price-only live mode.

The biggest functional gap is that `UiPriceOnly` currently disables historical sync entirely, which means the portal can start without populated candle history even though live quote rendering still works.

## 3. Scope

### In scope

- ETHUSD startup historical sync
- Per-timeframe empty-table bootstrap
- Per-timeframe restart gap recovery
- Rate-limit-aware chunking and retry
- DB state tracking for sync status
- Health/API/dashboard visibility
- Logging and test coverage

### Out of scope

- Historical replay redesign
- Signal logic changes unrelated to missing candle recovery
- Tick-level backfill
- Multi-instrument generalization beyond keeping the design reusable

## 4. Design Principles

1. Closed historical candles must be synchronized before live processing starts.
2. The sync must be idempotent. Re-running startup must not duplicate rows or corrupt state.
3. The sync must be per timeframe, not inferred only from `1m`.
4. API chunking must be explicit and slow enough to survive Capital rate limits.
5. `UiPriceOnly` must control live quote acquisition only, not historical candle synchronization.
6. Front-end status must come from persisted sync state, not guesswork.

## 5. Proposed Architecture

## 5.1 New startup component

Add a new service in `src/EthSignal.Infrastructure/Engine`:

- `HistoricalCandleSyncService.cs`

This service becomes the single startup coordinator for:

- empty-table bootstrap
- restart gap recovery
- per-timeframe chunk planning
- per-timeframe status persistence
- sync summary logging

`BackfillService` should not be deleted immediately. It should either:

1. be replaced by `HistoricalCandleSyncService`, or
2. be reduced to a helper used only for legacy `1m` rebuild cases.

Recommended approach: replace startup usage with `HistoricalCandleSyncService` and keep `BackfillService` temporarily only if another internal flow still needs it.

## 5.2 New execution flow

Update `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs` so startup order becomes:

1. Migrate DB
2. Authenticate with Capital API if historical sync is enabled
3. Run startup historical candle sync
4. Optionally run replay
5. Start `LiveTickProcessor`

Important change:

- Do not gate historical candle sync on `HighFreqTicks:UiPriceOnly`.
- Gate it on a new config flag such as `CapitalApi:HistoricalSyncEnabled`.

## 5.3 Proposed sync modes

For each timeframe, exactly one startup mode is selected:

- `EMPTY_BOOTSTRAP`
  - target table has zero rows for the symbol
  - fetch last 30 days for that timeframe
- `OFFLINE_GAP_RECOVERY`
  - target table has rows, but latest closed candle is older than the current closed boundary
  - fetch only the missing closed-candle range
- `NOOP`
  - table is already up to date through the last closed boundary

## 5.4 Timeframe processing order

Recommended order:

1. `4h`
2. `1h`
3. `30m`
4. `15m`
5. `5m`
6. `1m`

Reason:

- Higher timeframes require fewer API calls and become portal-ready quickly.
- The user requirement is portal completeness on startup.
- Signal logic still remains blocked until all mandatory timeframes succeed, so order does not change correctness.

If later we want to optimize live-start latency, we can switch to a two-phase strategy, but the first implementation should stay simple and deterministic.

## 6. Technical Mechanism

## 6.1 Closed-boundary calculation

For a timeframe `tf`:

- `currentOpenBoundary = tf.Floor(DateTimeOffset.UtcNow)`
- closed candles are all candles with `OpenTime < currentOpenBoundary`

This means startup sync must never fetch or persist the currently open candle. Open candles remain owned by `LiveTickProcessor`.

## 6.2 Empty-table bootstrap range

For each empty timeframe table:

- `syncToUtc = currentOpenBoundary`
- `syncFromUtc = tf.Floor(syncToUtc.AddDays(-30))`

Result:

- the portal gets 30 days of closed candles for that timeframe
- no partial current candle is written

## 6.3 Offline-gap recovery range

For a non-empty timeframe table:

- `latestClosed = repo.GetLatestClosedTimeAsync(tf, symbol)`
- `expectedNextOpen = latestClosed + tf.Duration`
- `syncToUtc = currentOpenBoundary`

If `expectedNextOpen >= syncToUtc`, no recovery is needed.

If `expectedNextOpen < syncToUtc`, then:

- `offlineDuration = syncToUtc - expectedNextOpen`
- `syncFromUtc = expectedNextOpen`
- mode = `OFFLINE_GAP_RECOVERY`

This directly measures the missing closed-candle gap for that timeframe.

## 6.4 Chunk planning

Use timeframe-aware chunking by candle count, not hardcoded hours.

Recommended config:

- `CapitalApi:StartupHistoricalDays = 30`
- `CapitalApi:HistoricalSyncEnabled = true`
- `CapitalApi:HistoricalSyncChunkCandles = 400`
- `CapitalApi:HistoricalSyncChunkDelayMs = 1200`
- `CapitalApi:HistoricalSyncMaxRetries = 6`
- `CapitalApi:HistoricalSyncRetryBaseDelayMs = 2000`

Chunk algorithm:

1. Compute `chunkDuration = tf.Duration * HistoricalSyncChunkCandles`
2. Split `[syncFromUtc, syncToUtc)` into sequential windows
3. For each chunk:
   - call `ICapitalClient.GetCandlesAsync(epic, tf.ApiResolution, chunkStart, chunkEnd, max=HistoricalSyncChunkCandles, ct)`
   - upsert results into `tf.Table`
   - persist progress
   - delay `HistoricalSyncChunkDelayMs`

Why this is preferred:

- consistent across all timeframes
- predictable request count
- aligned with the existing `Timeframe.ApiResolution` model
- easier to explain in logs and UI

## 6.5 Retry and rate-limit behavior

For a chunk request:

- retry on `429`, `500`, `503`, and network exceptions
- exponential backoff with cap
- persist the failure in sync status if all retries fail
- abort startup sync if any required timeframe fails

Recommendation:

- use the same retry style already present in `DataIngestionService.AuthenticateWithRetryAsync`
- keep implementation internal; no new NuGet package is required

## 6.6 Upsert behavior

Use existing upsert semantics in `CandleRepository.BulkUpsertAsync`.

This gives:

- idempotency
- safe overlap between repeated startup runs
- safe recovery if a chunk was partially written before a crash

The startup sync must persist only closed candles (`IsClosed = true`).

## 6.7 Ownership boundary with live processing

Startup sync owns:

- all closed historical candles before service enters live mode

`LiveTickProcessor` owns:

- current open candle state
- all new candles created after startup sync completes

This keeps responsibility clear and avoids partial-candle conflicts.

## 7. Back-end Changes by File

## 7.1 `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs`

Change:

- inject `HistoricalCandleSyncService`
- call it after auth and before replay/live mode
- stop using `UiPriceOnly` as the switch for historical sync

Behavior:

- if `CapitalApi:HistoricalSyncEnabled = false`, skip sync and log explicitly
- if enabled, always authenticate and sync history even when live quote mode is UI-only
- block live processor start until sync completes successfully

## 7.2 `src/EthSignal.Infrastructure/Engine/HistoricalCandleSyncService.cs`

New file.

Responsibilities:

- build a per-timeframe startup plan
- determine empty-table bootstrap vs gap recovery
- split each range into chunks
- fetch candles via `ICapitalClient`
- upsert via `ICandleRepository`
- persist sync state
- write audit rows
- return a summary object for logs and health

Recommended supporting models in the same folder or a new `Sync` subfolder:

- `StartupCandleSyncPlan`
- `TimeframeSyncPlan`
- `TimeframeSyncMode`
- `TimeframeSyncResult`
- `StartupCandleSyncSummary`

## 7.3 `src/EthSignal.Infrastructure/Db/ICandleRepository.cs`

Add methods needed for planning and status display:

- `Task<long> CountCandlesAsync(Timeframe tf, string symbol, CancellationToken ct = default);`
- `Task<DateTimeOffset?> GetEarliestClosedTimeAsync(Timeframe tf, string symbol, CancellationToken ct = default);`
- `Task<Dictionary<string, DateTimeOffset?>> GetLatestClosedTimesAsync(string symbol, CancellationToken ct = default);`

These avoid repeated ad hoc SQL in the sync service.

## 7.4 `src/EthSignal.Infrastructure/Db/CandleRepository.cs`

Implement the new planning methods.

Keep `BulkUpsertAsync` as the write path.

No schema change is needed for candle tables themselves.

## 7.5 `src/EthSignal.Infrastructure/Db/IAuditRepository.cs`

Either extend this interface or introduce a dedicated `ICandleSyncRepository`.

Recommended approach:

- keep `IAuditRepository` for `ingestion_audit` and `gap_events`
- add a new `ICandleSyncRepository` for startup sync state

Reason:

- startup history sync is operational state, not just audit logging
- this keeps gap auditing separate from startup sync progress tracking

## 7.6 `src/EthSignal.Infrastructure/Db/CandleSyncRepository.cs`

New file.

Responsibilities:

- upsert latest per-timeframe sync state
- load sync state for health and UI
- record current running chunk progress

## 7.7 `src/EthSignal.Web/Program.cs`

Changes:

- register `HistoricalCandleSyncService`
- register `ICandleSyncRepository`
- expose new sync status endpoints
- include sync summary in `/health`
- add `candle_sync_status` to DB explorer allow-list

Recommended new endpoints:

- `GET /api/admin/candle-sync/status`
- `GET /api/admin/candle-sync/status/{timeframe}`
- `POST /api/admin/candle-sync/retry`

The retry endpoint should remain localhost-only like the existing admin endpoints.

## 7.8 `src/EthSignal.Infrastructure/Apis/CapitalClient.cs`

No major contract change is required because `GetCandlesAsync` already accepts:

- `resolution`
- `fromUtc`
- `toUtc`
- `max`

Recommended small enhancement:

- improve error classification for rate-limit vs permanent failure so logs can say `rate_limited`, `server_error`, or `not_found`

## 8. Database Changes

## 8.1 Existing tables that remain in use

- `candles_1m`
- `candles_5m`
- `candles_15m`
- `candles_30m`
- `candles_1h`
- `candles_4h`
- `ingestion_audit`
- `gap_events`

## 8.2 New table: `candle_sync_status`

Add in `src/EthSignal.Infrastructure/Db/DbMigrator.cs`:

```sql
CREATE TABLE IF NOT EXISTS "ETH".candle_sync_status (
    symbol                      TEXT        NOT NULL,
    timeframe                   TEXT        NOT NULL,
    status                      TEXT        NOT NULL,
    sync_mode                   TEXT        NOT NULL,
    is_table_empty              BOOLEAN     NOT NULL DEFAULT FALSE,
    requested_from_utc          TIMESTAMPTZ,
    requested_to_utc            TIMESTAMPTZ,
    last_existing_candle_utc    TIMESTAMPTZ,
    last_synced_candle_utc      TIMESTAMPTZ,
    offline_duration_sec        BIGINT      NOT NULL DEFAULT 0,
    chunk_size_candles          INT         NOT NULL DEFAULT 0,
    chunks_total                INT         NOT NULL DEFAULT 0,
    chunks_completed            INT         NOT NULL DEFAULT 0,
    last_run_started_at_utc     TIMESTAMPTZ,
    last_run_finished_at_utc    TIMESTAMPTZ,
    last_success_at_utc         TIMESTAMPTZ,
    last_error                  TEXT,
    updated_at_utc              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (symbol, timeframe)
);
```

Purpose:

- current per-timeframe startup sync state
- dashboard and health visibility
- operational debugging without searching logs

## 8.3 `ingestion_audit` extension

Extend `ingestion_audit` with optional columns:

- `sync_mode TEXT NULL`
- `trigger TEXT NULL`
- `chunk_index INT NULL`
- `chunk_count INT NULL`
- `offline_duration_sec BIGINT NULL`

Why:

- `ingestion_audit` is currently too coarse for chunked startup sync
- chunk-level entries will make investigation much easier

If schema changes to `ingestion_audit` are considered too risky, keep the table unchanged and write chunk details only into `candle_sync_status` plus logs. The preferred implementation is to extend it.

## 8.4 Relationship with `gap_events`

`gap_events` remains for detected integrity gaps, not for planning startup recovery.

Startup sync should optionally resolve matching historical `gap_events` after successful recovery when:

- `gap_events.timeframe = synced timeframe`
- `expected_time` is inside the recovered range
- the candle now exists in the target table

This is useful but should be a second pass, not a blocker for the first implementation.

## 9. Front-end Changes

## 9.1 Files to update

- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `src/EthSignal.Web/wwwroot/data.html`

## 9.2 Dashboard additions

Add a new "History Sync" card to `index.html`.

Show:

- overall startup sync status: `RUNNING`, `READY`, `FAILED`
- last startup sync time
- number of timeframes ready
- number of timeframes still syncing or failed
- for each timeframe:
  - status
  - mode (`EMPTY_BOOTSTRAP`, `OFFLINE_GAP_RECOVERY`, `NOOP`)
  - last synced candle time
  - offline duration
  - chunk progress
  - error text if failed

## 9.3 Dashboard JS behavior

In `dashboard.js`, add a new polling function:

- `refreshCandleSyncStatus()`

It should call:

- `GET /api/admin/candle-sync/status`

Poll interval:

- every 5 seconds during startup sync
- every 30 seconds once all timeframes are `READY`

## 9.4 Health surface

Enhance `/health` response to include:

```json
"historySync": {
  "status": "running",
  "readyTimeframes": 4,
  "totalTimeframes": 6,
  "failedTimeframes": 0,
  "runningTimeframes": ["5m", "1m"],
  "lastSuccessAt": "..."
}
```

The dashboard can also consume this for a compact top-level badge.

## 9.5 Data Explorer

Add `candle_sync_status` to the DB explorer allow-list in `Program.cs` so operators can inspect it from `data.html`.

No special UI logic is needed in `data.html`; the existing generic table viewer is enough.

## 10. Logging Requirements

## 10.1 Log sources

Primary loggers:

- `HistoricalCandleSyncService`
- `DataIngestionService`
- `CapitalClient`

## 10.2 Required structured log events

At minimum, log the following:

- `Startup candle sync starting`
  - symbol
  - total timeframes
  - current UTC time
- `Timeframe sync planned`
  - timeframe
  - mode
  - from
  - to
  - last existing candle
  - offline duration seconds
- `Timeframe sync chunk starting`
  - timeframe
  - chunk index
  - chunk count
  - chunk from
  - chunk to
- `Timeframe sync chunk completed`
  - timeframe
  - chunk index
  - candles fetched
  - candles upserted
  - duration ms
- `Timeframe sync rate limited`
  - timeframe
  - chunk index
  - retry attempt
  - delay
- `Timeframe sync completed`
  - timeframe
  - mode
  - total candles fetched
  - total rows upserted
  - offline duration
  - elapsed
- `Timeframe sync failed`
  - timeframe
  - mode
  - failed chunk
  - exception
- `Startup candle sync completed`
  - symbol
  - ready timeframes
  - total elapsed

## 10.3 Logging behavior expectations

- Successful no-op timeframes must still log a concise line so operators know the service checked them.
- Failures must be at `Warning` or `Error` level with timeframe and chunk context.
- The final startup summary must be `Information` level.

## 11. Configuration Changes

Add to `src/EthSignal.Web/appsettings.json`:

```json
"CapitalApi": {
  "Symbol": "ETHUSD",
  "BackfillDays": 7,
  "HistoricalSyncEnabled": true,
  "StartupHistoricalDays": 30,
  "HistoricalSyncChunkCandles": 400,
  "HistoricalSyncChunkDelayMs": 1200,
  "HistoricalSyncMaxRetries": 6,
  "HistoricalSyncRetryBaseDelayMs": 2000
}
```

Notes:

- `BackfillDays` can remain temporarily if replay still uses it.
- `StartupHistoricalDays` becomes the new setting for portal history bootstrap.
- The implementation should prefer `StartupHistoricalDays` over `BackfillDays` for candle sync.

## 12. Behavioral Rules

## 12.1 Fresh empty database

When all candle tables are empty:

- startup sync runs for all six timeframes
- each timeframe fetches 30 days of closed candles
- live processing does not start until all six timeframes are complete
- dashboard shows `RUNNING` until the final timeframe finishes

## 12.2 Partially empty database

If only some timeframe tables are empty:

- bootstrap only those empty timeframes
- non-empty tables use gap-recovery or no-op logic
- do not truncate or rebuild populated tables

## 12.3 Normal restart after downtime

For each populated timeframe:

- calculate the missing range from latest closed candle to current closed boundary
- fetch only that range
- do not re-fetch the full 30 days

## 12.4 Restart with tiny downtime

If downtime is shorter than one timeframe bucket:

- some timeframes may be `NOOP`
- others may recover exactly one missing candle

Example:

- 8-minute downtime:
  - `1m` may recover 8 candles
  - `5m` may recover 1 candle
  - `15m`, `30m`, `1h`, `4h` may be `NOOP`

## 12.5 UI-price-only mode

When `HighFreqTicks:UiPriceOnly = true`:

- live quote/tick acquisition remains UI-driven
- startup historical candle sync still authenticates and uses Capital candle endpoints
- this is mandatory for portal candle history completeness

## 12.6 Failure handling

If any mandatory timeframe fails after retries:

- startup sync status becomes `FAILED`
- failure is persisted in `candle_sync_status`
- live processing should not start
- `/health` should report degraded or failed startup state

This is the safest first implementation because the signal engine depends on all timeframes.

## 13. API Contract Additions

## 13.1 `GET /api/admin/candle-sync/status`

Response should include:

- overall status
- startup timestamp
- per-timeframe rows

Example:

```json
{
  "symbol": "ETHUSD",
  "status": "running",
  "readyTimeframes": 3,
  "totalTimeframes": 6,
  "timeframes": [
    {
      "timeframe": "1m",
      "status": "RUNNING",
      "syncMode": "OFFLINE_GAP_RECOVERY",
      "requestedFromUtc": "2026-04-10T08:15:00Z",
      "requestedToUtc": "2026-04-10T10:23:00Z",
      "offlineDurationSec": 7680,
      "chunksCompleted": 2,
      "chunksTotal": 5,
      "lastSyncedCandleUtc": "2026-04-10T09:01:00Z",
      "lastError": null
    }
  ]
}
```

## 13.2 `POST /api/admin/candle-sync/retry`

Localhost-only endpoint to retry failed startup sync without restarting the app.

This can be added after the read-only status endpoint. It is useful but not required for the first delivery.

## 14. Implementation Steps

1. Add new config keys and read them in `DataIngestionService`.
2. Add `candle_sync_status` migration and optional `ingestion_audit` columns in `DbMigrator`.
3. Add repository methods for table row counts and latest candle lookup.
4. Add `ICandleSyncRepository` and `CandleSyncRepository`.
5. Implement `HistoricalCandleSyncService`.
6. Wire startup flow in `DataIngestionService`.
7. Add `/api/admin/candle-sync/status` and extend `/health`.
8. Add dashboard card and polling in `index.html` and `dashboard.js`.
9. Add `candle_sync_status` to DB explorer allow-list.
10. Add tests.

## 15. Test Plan

## 15.1 Unit tests

Add under `tests/EthSignal.Tests/Engine`:

- `HistoricalCandleSyncServiceTests.cs`

Required unit cases:

1. Empty table selects `EMPTY_BOOTSTRAP`.
2. Non-empty up-to-date table selects `NOOP`.
3. Non-empty stale table selects `OFFLINE_GAP_RECOVERY`.
4. `syncFromUtc` for bootstrap is exactly 30 days aligned to timeframe floor.
5. `syncFromUtc` for restart equals `latestClosed + tf.Duration`.
6. `syncToUtc` always equals current open boundary, never includes the open candle.
7. Chunk planner splits ranges correctly for `1m`, `5m`, `15m`, `1h`, and `4h`.
8. Retry logic backs off on `429`.
9. No-op timeframes still persist status.

## 15.2 Repository tests

Extend `tests/EthSignal.Tests/Infrastructure/CandleRepositoryTests.cs` or add:

- `CandleSyncRepositoryTests.cs`

Required DB cases:

1. `CountCandlesAsync` returns zero for empty tables.
2. `GetLatestClosedTimeAsync` returns the latest closed candle for each timeframe.
3. `candle_sync_status` upsert creates and updates the same primary row.
4. `offline_duration_sec`, `chunks_total`, and `last_error` are persisted correctly.

## 15.3 API tests

Extend `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`.

Required API cases:

1. `/api/admin/candle-sync/status` returns valid JSON.
2. `/health` includes `historySync`.
3. failed sync state is surfaced in the health payload.
4. mocked sync state returns expected per-timeframe objects.

## 15.4 Integration behavior tests

Add a new integration suite that uses a test DB and mocked `ICapitalClient`.

Required scenarios:

1. Fresh start with all tables empty:
   - all six tables receive 30 days
   - live processor start is delayed until sync success
2. Restart after 20 minutes offline:
   - only missing closed windows are requested
   - already present earlier candles are not re-fetched unnecessarily
3. Mixed state:
   - `1m` populated
   - `1h` empty
   - only `1h` does 30-day bootstrap
4. Idempotent rerun:
   - second startup with no new downtime results in `NOOP`
5. Rate-limit retry:
   - chunk fails with `429` twice then succeeds
   - final status is `READY`
6. Permanent failure:
   - chunk fails after max retries
   - status is `FAILED`
   - live mode does not start
7. UI-price-only mode:
   - historical sync still runs
   - live quote provider remains unchanged

## 15.5 Manual QA

Required manual checks:

1. Start with empty ETH candle tables and open portal.
2. Confirm all timeframe charts load historical candles after startup.
3. Stop the service for a known duration, restart, and confirm only the missing candle window is filled.
4. Confirm `data.html` shows `candle_sync_status`.
5. Confirm `logs/ethsignal-YYYYMMDD.log` shows chunk-by-chunk sync progress.

## 16. Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Capital API rate limit during large `1m` bootstrap | startup delay or failure | small chunk size, forced delay, exponential retry |
| `UiPriceOnly` still accidentally skips history sync | portal starts empty again | separate `HistoricalSyncEnabled` from `UiPriceOnly` |
| Partial crash during sync | inconsistent progress visibility | persist `candle_sync_status` after every chunk |
| Open candle accidentally written by startup sync | duplicate/incorrect current bar | always use `tf.Floor(now)` as exclusive upper bound |
| One timeframe succeeds while another fails | stale cross-timeframe context | block live start until all mandatory timeframes succeed |

## 17. Recommended File Inventory

Files to create:

- `src/EthSignal.Infrastructure/Engine/HistoricalCandleSyncService.cs`
- `src/EthSignal.Infrastructure/Db/ICandleSyncRepository.cs`
- `src/EthSignal.Infrastructure/Db/CandleSyncRepository.cs`
- `tests/EthSignal.Tests/Engine/HistoricalCandleSyncServiceTests.cs`
- `tests/EthSignal.Tests/Infrastructure/CandleSyncRepositoryTests.cs`

Files to modify:

- `src/EthSignal.Web/BackgroundServices/DataIngestionService.cs`
- `src/EthSignal.Infrastructure/Db/ICandleRepository.cs`
- `src/EthSignal.Infrastructure/Db/CandleRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/appsettings.json`
- `src/EthSignal.Web/appsettings.Gold.json` if shared settings are mirrored there
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## 18. Final Recommendation

The cleanest implementation for this repository is to introduce a dedicated startup `HistoricalCandleSyncService` that performs direct per-timeframe closed-candle synchronization before live mode begins, persists status in a new `candle_sync_status` table, and exposes that state through health and dashboard APIs.

The two non-negotiable changes are:

1. decouple historical sync from `UiPriceOnly`
2. switch startup history planning from a single `1m` anchor to per-timeframe empty-table and offline-gap logic

Once those two changes are in place, the portal will consistently receive the required history on startup and restart recovery will fill only the missing gaps instead of re-running a broad backfill.
