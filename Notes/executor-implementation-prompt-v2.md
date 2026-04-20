Act as a senior .NET 9 / C# trading-platform engineer working inside the existing repository:

MohammadAbwini98/ETH_MANUAL_AI

Task:
Review the current codebase and fix the Capital.com account-selection and execution flow so that the system uses the Capital.com DEMO account named exactly:

DEMOAI

Problem statement:
1. The “Executed Signals / Trades” section is still showing LIVE account details instead of DEMOAI.
2. The system is still capturing a LIVE account id in the database.
3. The attached `account_snapshots` data confirms the current bug:
   - rows are being stored with `is_demo = false`
   - only a live `account_id` is being captured
   - `account_name` is empty/null
4. Capital.com offers both live and demo accounts.
5. The system must use the DEMO account named DEMOAI for:
   - all signal executions
   - all account snapshot captures
   - all dashboard account widgets and executed trade account details
6. Review whether the system actually executes signals automatically and instantly on Capital.com demo account DEMOAI, and fix any issue preventing that.

Important:
Do NOT make assumptions.
Inspect the current implementation end-to-end before changing anything.

Primary objective:
Ensure the application always resolves, validates, and uses the DEMO Capital.com account named DEMOAI for all trading execution and dashboard/account reporting, and never leaks live-account details into executed trades, account snapshots, or dashboard summaries.

Critical constraints:
1. Never use the live account for order execution.
2. Never show live account details in the dashboard trading sections.
3. Never persist live account ids in `account_snapshots` or related execution/account tables for the demo trading flow.
4. Do not apply a UI-only fix.
5. Fix the actual backend account-selection logic and all dependent flows.
6. Preserve loose coupling and current architecture style.

What you must do first:
Perform a real code review and identify:

A) Current account-selection flow
1. Where Capital.com authentication/session is created.
2. Whether the code explicitly selects an account after login.
3. Whether it defaults to the first account returned by Capital.com.
4. Whether it is using a live session/base URL/account context by mistake.
5. Where account snapshots are captured and persisted.
6. Where dashboard account widgets read their data from.
7. Where execution/trading code gets the account id or active account context from.
8. Whether account selection is based on:
   - account id
   - account name
   - is_demo flag
   - environment
   - or first returned account only

B) Current execution flow
Review and verify whether the system currently:
1. executes signals automatically
2. executes them instantly/immediately after signal creation
3. executes recommended signals only, or also generated/blocked
4. actually places orders through Capital.com
5. confirms order placement correctly
6. uses the same account context for:
   - order placement
   - account snapshots
   - dashboard display
7. silently fails and only simulates execution instead of placing real demo orders

C) Current dashboard/data flow
Review:
1. Executed Signals / Trades section
2. account widgets
3. account summary endpoints
4. snapshot repositories/tables
5. any cached account state
6. any endpoint returning live account details even after a demo switch

Implementation requirements:

1) Fix Capital.com account resolution
Implement robust account resolution logic after authentication.

Requirements:
1. After session/authentication, fetch available Capital.com accounts.
2. Explicitly locate the DEMO account whose account name is exactly `DEMOAI`.
3. Validate that this account is truly a demo account.
4. Store/select this account as the active trading account context.
5. If DEMOAI is not found, fail safely and clearly:
   - do not fall back to live account
   - log a clear error
   - expose a clear status/error to dashboard/admin health if applicable
6. Never rely on the first returned account.
7. Never silently choose a live account.

2) Make DEMOAI the authoritative account for all trading/account flows
Once DEMOAI is resolved, use it consistently for:
1. order placement
2. order confirmation
3. position retrieval
4. force-close
5. account summary
6. account snapshot capture
7. executed trade account metadata
8. dashboard account widgets
9. any cached account state

3) Review and fix environment/base-url assumptions
Inspect whether the current system is mixing:
- demo API environment
- live API environment
- demo account selection
- live account selection

Requirements:
1. Validate the configured Capital.com environment.
2. Ensure the app is aligned with demo trading usage.
3. If demo trading still requires explicit account switching after session creation, implement it correctly.
4. Prevent any path that could authenticate successfully but still operate on the live account context.

4) Fix account snapshot persistence
Review the current `account_snapshots` persistence flow and fix it.

Requirements:
1. Snapshot rows must represent DEMOAI only for the demo trading flow.
2. Persist:
   - correct demo account id
   - `is_demo = true`
   - `account_name = DEMOAI`
3. Stop persisting live account ids for this flow.
4. If historical bad rows exist, do not silently rewrite history unless there is already an approved migration/repair pattern.
5. Fix future writes first.
6. If appropriate, add a repair/admin script or migration note for old bad rows, but keep that separate from the runtime fix.

5) Fix dashboard account display
Review all dashboard/account endpoints and UI bindings.

Requirements:
1. “Executed Signals / Trades” must show DEMOAI account details.
2. Account widgets must show DEMOAI balance/equity/funds/margin.
3. Any account label should clearly identify DEMOAI when helpful.
4. Ensure the data source is not reading stale live-account snapshots.
5. If dashboard reads from persisted snapshots, make sure it now reads the corrected demo snapshots.
6. If dashboard reads from live service calls, make sure those calls use DEMOAI account context.

6) Review automatic execution behavior
You must explicitly inspect whether signals are currently executed automatically and instantly.

Determine and document:
1. where automatic execution starts
2. what event/service triggers it
3. whether execution is synchronous, queued, background, or manual
4. whether recommended signals are actually being sent to Capital.com immediately
5. whether execution is blocked by configuration, gating, or environment mismatch
6. whether execution is using DEMOAI or still using live/default account context

Then:
1. Fix any issue preventing automatic immediate execution on DEMOAI.
2. Keep behavior aligned with the project’s intended execution model.
3. Do not add fake execution if real Capital.com demo execution is intended.

7) Add safeguards against live-account leakage
Add explicit backend safeguards:
1. If resolved account is not demo, abort execution.
2. If resolved account name is not DEMOAI, abort execution unless configuration explicitly supports a different demo account and is intentionally changed.
3. Log why execution/snapshot/dashboard sync was blocked.
4. Add a clear health/admin status so operators can see whether DEMOAI is active.

8) Add or extend diagnostic visibility
Add enough observability to make this issue easy to detect in future.

Expose or log:
1. current active account id
2. current active account name
3. whether active account is demo
4. source of account selection
5. latest account resolution timestamp
6. latest execution account used
7. any mismatch/warning if live account is ever seen in demo execution flow

9) Preserve architecture quality
Do not implement this as scattered hotfixes.
Prefer a clean design such as:
- dedicated account context resolver
- dedicated active trading account provider
- centralized broker account selection policy
- shared account context injected into execution/snapshot services

Avoid:
1. duplicating account selection logic in multiple services
2. UI-only overrides
3. hardcoded account ids without validation
4. fallback to live account on error

10) Database / model / repository updates
If needed, extend models/repositories so account data is stored properly:
- account_id
- account_name
- is_demo
- source/account type
- captured_at
- active flag if useful

Do this only if needed and keep schema changes minimal and justified.

11) Testing requirements
Add tests that prove the fix.

Include:
A. account selection tests
1. when DEMOAI exists and is demo -> selected
2. when DEMOAI does not exist -> fail safely
3. when only live account exists -> fail safely
4. when first account is live but DEMOAI exists later -> DEMOAI still selected

B. execution tests
1. recommended signal executes using DEMOAI
2. execution is blocked if active account is live
3. execution path uses the resolved demo account context consistently

C. snapshot tests
1. account snapshot persists `is_demo = true`
2. account snapshot persists `account_name = DEMOAI`
3. account snapshot no longer writes live account id for this flow

D. API/dashboard tests
1. account summary endpoint returns DEMOAI details
2. executed trades endpoint/dashboard contract uses DEMOAI-linked data
3. no live account leakage in trading/account widgets

12) Deliverables
Provide:
1. code changes
2. minimal schema/model/repository updates if required
3. updated account-selection logic
4. updated execution/account snapshot/dashboard integration
5. tests
6. short implementation note describing:
   - where the bug was
   - why live account was being selected
   - how DEMOAI is now resolved and enforced
   - whether automatic signal execution is truly immediate and real
   - what files were changed

Very important implementation notes:
1. Inspect the attached account snapshot issue and use it as a concrete debugging lead.
2. Do not stop at fixing dashboard text only.
3. Do not stop at fixing snapshot storage only.
4. Ensure the actual active trading account used for execution is DEMOAI.
5. Review the codebase fully enough to answer whether the system really executes automatically and instantly on Capital.com demo account DEMOAI.
6. If the system currently does not execute automatically and instantly, identify the exact reason and fix it properly.
7. If there is any ambiguous broker/account behavior in current code, prefer strict safe behavior over risky fallback behavior.