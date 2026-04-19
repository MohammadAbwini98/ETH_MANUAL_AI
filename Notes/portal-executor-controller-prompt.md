Act as a senior .NET 9 / C# engineer working inside the existing repository `MohammadAbwini98/ETH_MANUAL_AI`.

Task:
Add a toggle switch button to the portal page under **Global Configuration** that enables or disables the **signal recommendation executor** from executing **recommended signals**.

Very important:
Before implementing anything, inspect the current codebase and understand the existing execution flow, configuration flow, portal/global-configuration flow, and any dependencies that may be affected. Do not assume the toggle can be added only at UI level. Implement it end-to-end safely.

Goal:
When the toggle is OFF, the system must not execute recommended signals automatically.
When the toggle is ON, the current recommended-signal execution behavior should work normally.
This toggle must affect only **recommended signal execution** unless the existing architecture clearly requires a broader shared execution gate. If there is any shared dependency that would unintentionally disable generated or blocked signal execution, detect it and implement the change carefully so only the intended behavior is affected.

Current-project expectations:
1. This is an existing solution with:
   - EthSignal.Web
   - EthSignal.Infrastructure
   - EthSignal.Domain
2. The project already has:
   - portal/global configuration concepts
   - parameter/configuration loading
   - signal generation/recommendation flow
   - execution-related functionality
   - dashboard and portal APIs
3. Do not introduce a hacky UI-only toggle.
4. Do not hardcode behavior in the page itself.
5. The toggle must be backed by server-side state/configuration and enforced in execution logic.

Implementation requirements:

A) Analyze before coding
First inspect and document in comments / implementation notes:
1. Where recommended signals are currently executed.
2. Whether execution is triggered automatically, manually, via background service, via dashboard action, or via another orchestration service.
3. How portal/global configuration is currently stored and refreshed.
4. Whether there is already an overrides/config repository or global settings table that should be extended.
5. What dependencies currently consume execution-related configuration.
6. Whether recommended, blocked, and generated executions share the same execution pipeline.
7. The safest insertion point for enforcing the new toggle.

B) Add a new global configuration flag
Add a dedicated persisted setting for example:
- `RecommendedSignalExecutionEnabled`

Requirements:
1. It must be stored in the same configuration/override mechanism already used by the portal if one exists.
2. If there is an existing `portal_overrides` or equivalent configuration repository/service, extend it instead of creating a disconnected config path.
3. Default behavior should preserve current behavior unless the existing system already has a stronger safer default.
4. On startup and refresh, the system must load this setting correctly.
5. Changes from portal must apply without needing unsafe manual code edits.

C) Backend enforcement
Implement the actual enforcement in the signal recommendation execution flow:
1. If `RecommendedSignalExecutionEnabled = false`:
   - recommended signals must not be executed automatically
   - the system should log a clear informational message when execution is skipped because of this toggle
   - optional dashboard/portal-visible reason should be available if the current architecture supports it
2. If `RecommendedSignalExecutionEnabled = true`:
   - current behavior should continue normally
3. Do not silently break other execution flows.
4. Do not disable signal generation itself unless the current design couples them and you explicitly decouple or handle it safely.
5. Ensure this toggle controls execution only, not signal creation, not signal history, and not dashboard display of recommendations.

D) Portal / Global Configuration UI
In the portal page under **Global Configuration**, add a proper switch button:
1. Label should be clear, for example:
   - `Recommended Signal Executor`
   - or `Enable Recommended Signal Execution`
2. Show the current ON/OFF state based on server data.
3. When toggled:
   - persist the change through the backend API
   - update the UI state cleanly
   - show success/failure feedback
4. Follow the current portal styling and patterns already used in the project.
5. Do not add inconsistent UI patterns.

E) API changes
Add or extend the needed API endpoints consistent with the current project style:
1. GET endpoint to read the current value
2. PATCH/POST endpoint to update it
3. Reuse current admin / portal config endpoints if appropriate instead of inventing unnecessary new ones

F) Refresh / cache behavior
If the project uses cached parameters/providers/override repositories:
1. Ensure the new toggle participates in refresh behavior
2. Ensure runtime execution uses the latest value safely
3. Avoid stale cached behavior after portal change

G) Logging and observability
Add useful logging:
1. when the toggle is loaded
2. when it is changed
3. when recommended execution is skipped because it is disabled
4. when recommended execution resumes after being enabled

H) Safety and dependency checks
Before finalizing, verify:
1. recommended signals still generate and appear in dashboard/history even when execution is OFF
2. generated/blocked execution behavior is unchanged unless explicitly intended
3. no null/config refresh issue is introduced
4. no startup failure is introduced
5. no portal page regression is introduced

I) Testing
Add tests matching the project style:
1. config repository / override persistence test
2. service-level execution gate test:
   - OFF => recommendation not executed
   - ON => recommendation can execute
3. API endpoint tests for reading/updating the flag
4. any portal/UI test if current project has that pattern
5. regression tests ensuring the toggle does not break non-recommended flows

J) Deliverables
Provide:
1. clean implementation
2. minimal required schema/config changes
3. backend enforcement
4. portal UI switch
5. tests
6. short implementation note listing:
   - files changed
   - where the toggle is persisted
   - where execution is gated
   - how runtime refresh works
   - what dependencies were considered

Important constraints:
- Do not implement this as a temporary front-end-only flag.
- Do not hardcode the value in appsettings if the portal already has dynamic configuration storage.
- Do not disable signal generation.
- Do not disable blocked/generated execution unless explicitly required.
- Prefer extending the existing configuration/override system already present in the repository.
- Make the smallest clean architectural change that fits the current codebase.