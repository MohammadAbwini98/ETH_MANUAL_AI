Act as a senior .NET 9 / C# engineer working inside the existing repository:

MohammadAbwini98/ETH_MANUAL_AI

Task:
Fix the dashboard/API/trading issues identified in the latest audit report.

This prompt is based on a completed review of:
- Program.cs
- dashboard.js
- index.html
- signal-history services
- execution services
- repositories
- Web API test fixture

Important:
Treat the findings below as validated review input.
Do not restart from scratch or ignore them.
Use them as the working defect list, then inspect the exact code paths and implement the fixes cleanly.

================================================================
AUDIT FINDINGS TO FIX
================================================================

1. HIGH — Generated-signal history is not source-safe
- `GeneratedSignalHistoryService.cs` around line 120 pulls every BUY/SELL decision from `signal_decision_audit` instead of only true generated rows.
- `TradeAutoExecutionService.cs` around line 119 auto-executes directly from that service.
- Result:
  - blocked decisions can leak into the generated path
  - dashboard stats become polluted
  - wrong source-type execution can occur

2. HIGH — Latest dashboard card mixes unrelated snapshots
- `Program.cs` around line 686 returns latest signal and latest decision independently, without guaranteed shared linkage.
- `dashboard.js` around line 602 allows a newer decision to override the signal card.
- Result:
  - a valid current signal can be suppressed by a later unrelated decision from another timeframe/evaluation

3. MEDIUM — Dashboard binding bug on timeframe
- `dashboard.js` around line 624 reads `decision.timeframe`
- `Program.cs` around line 699 does not return that field
- Result:
  - timeframe label can go blank in blocked/no-trade states even when data exists

4. MEDIUM — Trading safety indicator is hardcoded to DEMOAI
- `dashboard.js` around line 1255 treats the account as safe only if `accountName === 'DEMOAI'`
- Result:
  - dashboard can show BLOCKED for a valid demo session if the configured preferred demo account changes

5. MEDIUM — History sections silently truncate data
- `dashboard.js` around line 705 loads only first 500 recommended rows
- `dashboard.js` around lines 713 and 727 load only first 2000 blocked/generated rows
- pagination is done client-side on partial data
- `dashboard.js` around line 921 reports counts from loaded subset, not full server total
- Result:
  - older rows disappear from dashboard as volume grows
  - counts/stats become misleading

6. MEDIUM — Blocked/generated execution lifecycle is flattened incorrectly in UI
- `dashboard.js` around line 774 maps OPEN to caller pending label
- `dashboard.js` around lines 931 and 1054 pass PENDING
- Result:
  - blocked/generated trades that are actually OPEN display as PENDING

7. HIGH — Web API integration suite is effectively broken
- `ApiEndpointTests.cs` around line 514 registers `Mock.Of<IDbMigrator>()`
- startup awaits `MigrateAsync()` in `Program.cs` around line 2148
- mock returns no configured Task
- startup fails
- `Program.cs` around line 2633 swallows exception
- Result:
  - test host never starts
  - tests fail with generic startup/server-not-started errors
  - dashboard/API regression safety net is broken

================================================================
VERIFIED CURRENT STATE
================================================================

Use these as baseline facts:
1. `node --check src/EthSignal.Web/wwwroot/js/dashboard.js` passes
2. `dotnet test ETH_MANUAL.sln --no-restore` currently shows:
   - 372 passing tests
   - 40 failing tests
   - all failures are in `EthSignal.Tests.Web.ApiEndpointTests`
   - failures happen before assertions because the test host never starts

================================================================
PRIMARY GOAL
================================================================

Implement clean, minimal, production-style fixes for the above issues.

Priority order:
1. Fix generated-history source filtering and source-type safety
2. Fix `/api/dashboard/latest` contract/linkage correctness
3. Restore `ApiEndpointTests`
4. Then fix UI contract/binding and history truncation issues
5. Then fix the trading safety indicator hardcoding

Do not treat dashboard counts, source labels, or latest-card state as trustworthy until those fixes are complete.

================================================================
PHASE 1 — REVIEW BEFORE CHANGING
================================================================

Before implementing, inspect the exact code paths and confirm the current behavior.

You must review:

A) Generated / blocked / recommended source integrity
1. How generated rows are selected
2. How blocked rows are selected
3. How recommended rows are selected
4. How source type is preserved from repository -> service -> execution -> API -> dashboard
5. Whether blocked rows can leak into generated flow
6. Whether auto-execution consumes source-unsafe data

B) Dashboard latest-card data contract
1. How `/api/dashboard/latest` builds its payload
2. Whether signal + decision + ML prediction are linked by:
   - signalId
   - evaluationId
   - timeframe
   - bar time
   - decision correlation
3. Whether dashboard override logic is safe
4. Whether unrelated decisions can suppress valid signal display

C) Dashboard UI bindings
1. Which fields dashboard.js expects
2. Which fields Program.cs actually returns
3. Any missing fields in contract
4. Any UI fallback logic that incorrectly rewrites lifecycle state

D) History loading/pagination
1. Whether backend already supports full pagination
2. Whether frontend is loading partial subsets only
3. Whether totals in UI are based on server totals or loaded rows only
4. Safest way to move from client-side subset paging to correct server-backed paging/counts

E) Tests
1. Why `IDbMigrator` mock breaks startup
2. Whether test fixture should:
   - stub `MigrateAsync()` correctly
   - replace startup migration behavior
   - or use a fake migrator implementation
3. Whether exception swallowing in startup is hiding useful failures from tests

================================================================
PHASE 2 — REQUIRED IMPLEMENTATION
================================================================

Implement the following fixes:

1) Fix generated-history source safety
Requirements:
1. `GeneratedSignalHistoryService` must only return true generated rows.
2. Blocked decisions must never leak into generated history.
3. Auto-execution must not consume source-unsafe generated history data.
4. Preserve correct source type in:
   - repositories
   - services
   - execution path
   - API response
   - dashboard rendering
5. Add/update tests proving source isolation.

2) Fix `/api/dashboard/latest` linkage correctness
Requirements:
1. Do not return unrelated latest signal and latest decision as if they belong together.
2. Ensure signal/decision pairing is linked by correct shared identity or correlation.
3. If safe linkage cannot be guaranteed, return contract fields that let the UI render each independently without incorrect override.
4. Prevent a newer unrelated decision from suppressing a valid current signal.
5. Update dashboard logic accordingly.

3) Fix missing timeframe contract
Requirements:
1. Ensure the backend returns the timeframe needed by dashboard blocked/no-trade state rendering.
2. Keep API contract explicit and consistent.
3. Remove any UI blank timeframe case caused only by missing backend field.
4. Add/update API tests for this contract.

4) Fix trading safety indicator hardcoding
Requirements:
1. Do not hardcode `DEMOAI` in dashboard safety logic unless repository configuration explicitly requires it.
2. Use backend-provided account safety info or configurable preferred demo account identity.
3. Dashboard should reflect actual valid demo-account state, not string equality only.
4. Preserve demo/live safety behavior.

5) Fix history truncation and subset-only counts
Requirements:
1. Stop relying on limited first-page bulk loads plus client-side pagination for full history views.
2. Use proper server-backed pagination and full totals where appropriate.
3. Recommended, blocked, and generated histories must:
   - paginate correctly
   - show correct totals
   - not silently drop older rows
4. UI counts/stats must reflect server totals, not only the currently loaded subset.
5. Keep UI changes minimal and consistent with current dashboard style.

6) Fix blocked/generated lifecycle label flattening
Requirements:
1. Do not map actual OPEN lifecycle state to PENDING in blocked/generated UI flow.
2. Preserve real trade lifecycle labels.
3. If there is a distinction between:
   - queued/pending-confirmation
   - open
   - closed
   - win
   - loss
   - expired
   show them accurately.
4. Do not break recommended history lifecycle labels.

7) Restore Web API integration tests
Requirements:
1. Fix the `IDbMigrator` test registration so startup can complete.
2. Ensure `MigrateAsync()` returns a valid completed task in tests.
3. Remove the “server has not been started” false failure condition.
4. Restore real API assertions in `ApiEndpointTests`.
5. Keep production startup behavior correct while making tests reliable.
6. If exception swallowing in startup hurts test diagnosability, improve that safely.

================================================================
PHASE 3 — TESTING REQUIREMENTS
================================================================

Add/update tests for:

A) Source integrity
1. generated history excludes blocked decisions
2. blocked history excludes generated decisions
3. auto-execution uses correct source type only

B) Dashboard latest payload
1. signal/decision pairing is correct
2. unrelated decisions do not suppress valid signal card state
3. timeframe is present when UI expects it

C) History pagination/counts
1. server totals are correct
2. paged history includes older records
3. UI-facing API contracts no longer rely on subset-only counts

D) Lifecycle label correctness
1. blocked/generated OPEN stays OPEN
2. pending/open/closed mapping is correct

E) Startup / Web API tests
1. test host starts successfully
2. `ApiEndpointTests` execute real assertions
3. dashboard/API regression coverage is restored

================================================================
PHASE 4 — IMPLEMENTATION RULES
================================================================

Follow these rules:
1. Make minimal clean changes.
2. Reuse existing architecture.
3. Do not create parallel history services unnecessarily.
4. Do not patch only the frontend when backend data contract is wrong.
5. Do not patch only backend when UI mapping is wrong.
6. Keep source-type integrity as a top priority.
7. Keep demo/live safety logic robust and configurable.
8. Preserve existing dashboard structure unless a small change is required.

================================================================
PHASE 5 — DELIVERABLES
================================================================

Provide:
1. code changes
2. updated tests
3. any minimal contract/API updates
4. short implementation note describing:
   - root cause of generated-path contamination
   - root cause of latest-card mismatch
   - root cause of blank timeframe bug
   - root cause of subset-only history counts
   - root cause of OPEN->PENDING UI flattening
   - root cause of broken ApiEndpointTests
   - files changed

Important:
Before changing code, explicitly confirm whether each defect is caused by:
- repository query bug
- service-layer source mixing
- API contract mismatch
- frontend binding bug
- frontend lifecycle mapping bug
- server pagination design gap
- test fixture misconfiguration
- startup exception handling
- or a combination of these

Then implement the cleanest fix consistent with the current codebase.