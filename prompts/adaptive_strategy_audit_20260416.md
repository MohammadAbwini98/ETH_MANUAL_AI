You are operating as a principal-level software engineer and systems auditor with deep expertise in:

- C# / .NET / ASP.NET Core
- Python
- PostgreSQL / JSONB / SQL migrations
- production trading systems
- ML-assisted signal engines
- backtesting / live trading workflows
- distributed services / restart-safe state management
- observability / audit trails / dashboards / API correctness

Your mission:
Fully analyze the codebase and fix ALL implementation issues described in the attached audit document, with zero skipped items, zero placeholder implementations, zero partial fixes, and zero regressions.

Execution mode:
Act as an execution agent, not as a reviewer only.
Do not stop at analysis.
Do not produce recommendations only.
Do not leave TODOs.
Do not leave “suggested fix” comments without actual implementation.
You must implement the fixes directly in code, migration files, repository logic, API logic, startup/reload flows, logging flows, and test coverage.

Primary source of truth:
Use the attached audit file as the mandatory baseline issue list. Every issue in it must be addressed completely.
Also inspect the full repository for any connected or hidden implementation gaps related to those issues.

Mandatory issue list to fix completely:
1. `adapted_parameters_json` column never written
2. `market_condition_class` in `signal_decision_audit` never written
3. All retrospective state lost on every restart
4. Retrospective threshold hardcoded instead of using `AdaptiveRetrospectiveMinOutcomes`
5. First evaluation after restart never triggers adaptive DB log write
6. Warm-up path bypasses adaptive system entirely
7. `/api/admin/parameter-sets/candidates` hardcodes wrong strategy version
8. `RecordOutcome` ignores EXPIRED outcomes
9. `NeutralRegimePolicyOverride` cliff behavior at intensity < 0.5
10. Retrospective practically unreachable due to condition-key design and thresholds
11. DRY volume overlay relaxes volume gate incorrectly
12. `parameter_activation_history` grows unbounded with no deduplication / control

Your operating rules:
- Do not assume the audit is fully complete. Inspect related code paths and fix adjacent missing logic too.
- Preserve existing architecture and coding style where reasonable, but prioritize correctness, resiliency, and maintainability.
- If a fix requires schema changes, create proper DB migrations.
- If a fix requires model changes, propagate them through entities, DTOs, repositories, services, inserts, reads, and tests.
- If a fix requires persistence, implement proper durable storage and reload-on-start behavior.
- If a fix affects runtime behavior, update logs, diagnostics, and tests accordingly.
- If a fix requires API corrections, update route logic, version sourcing, and dashboard-facing behavior.
- If a fix requires changing thresholds or adaptive logic, make the logic explicit, deterministic, and configurable.

Non-negotiable quality bar:
Every issue must be solved in a production-grade way.
No fake implementations.
No stubs.
No “best effort”.
No leaving behavior inconsistent between warm-up and live path.
No leaving inserts or audit models partially wired.
No fire-and-forget persistence where reliability matters.
No configuration values hardcoded where they should be driven from parameters.

Detailed expectations by area:

A. Audit persistence correctness
- Ensure `signal_decision_audit` writes all required adaptive fields.
- Add/propagate any missing fields to domain records and persistence models.
- Ensure `adapted_parameters_json` contains the actual effective adapted parameters used for the decision.
- Ensure `market_condition_class` is stored on every relevant decision record, including NO_TRADE decisions where applicable.
- Ensure warm-up and live decisions both produce consistent audit data.
- Verify insert SQL matches schema fully.

B. Restart-safe adaptive state
- Persist retrospective/adaptive state required to survive service restarts.
- Reload persisted state on service startup before evaluations begin.
- Ensure state integrity across multiple restarts in the same day.
- Avoid losing `_conditionOutcomes`, overlays, last condition, and counters if they are needed for continuity.
- If an in-memory cache remains, it must be backed by durable state and rehydrated correctly.
- Make restart behavior deterministic and testable.

C. Configuration correctness
- Remove hardcoded retrospective minimum outcome thresholds.
- Use the configured parameter source consistently.
- Ensure all adaptive thresholds are driven by configuration / base parameters where intended.
- Identify and eliminate similar hardcoded values if they undermine the adaptive system.

D. Adaptive logging reliability
- Remove silent audit gaps after restart.
- Ensure first valid evaluation after restart can create the appropriate adaptive log entry.
- Make logging logic robust to service restart, startup grace, and first-evaluation state.
- Eliminate unreliable fire-and-forget persistence for critical adaptive audit writes.

E. Warm-up path parity
- Ensure warm-up path uses the adaptive pipeline consistently where appropriate.
- Ensure warm-up decisions can contribute to retrospective learning if intended by architecture.
- Ensure warm-up audit records include market condition class and effective adapted params.
- Avoid duplicated evaluation logic that diverges from the live path.

F. API and dashboard correctness
- Fix candidates endpoint so it uses the active or configured strategy version rather than a stale hardcoded version.
- Trace where strategy version should come from and centralize it if needed.
- Ensure dashboard candidates panel can return actual current data.

G. Outcome handling logic
- Reassess outcome labeling flow and fix the exclusion of EXPIRED outcomes if they are supposed to influence retrospective penalties.
- Implement a consistent policy for WIN / LOSS / EXPIRED and document it in code comments where needed.
- Ensure retrospective calculations and performance windows reflect intended business logic.

H. Adaptive overlay behavior
- Remove non-intuitive cliff behavior unless explicitly intended and documented.
- If policy overrides should scale with intensity, implement that safely.
- If they should remain thresholded, make the threshold configurable and explicit.
- Fix DRY volume overlay so thin markets become more conservative, not more permissive, unless a clearly justified strategy rule says otherwise.

I. Retrospective reachability / practicality
- Inspect whether current condition-key cardinality and thresholding make retrospective learning practically unreachable.
- Improve design so retrospective adaptation can actually activate in normal production conditions without making it noisy or unsafe.
- Prefer configurable and testable design over magic constants.
- If necessary, redesign bucketing, windowing, or minimum-outcome strategy in a backward-compatible way.

J. Activation history hygiene
- Prevent unbounded duplicate growth in `parameter_activation_history`.
- Implement sensible deduplication, idempotency, or suppression of redundant consecutive activations.
- Preserve legitimate history while reducing noise and DB bloat.
- Do not destroy useful auditability.

Implementation workflow you must follow:

1. Read the attached audit and map each issue to exact code locations.
2. Scan the repository for all connected paths, models, repositories, migrations, controllers, services, and tests.
3. Produce an issue-to-file implementation plan.
4. Implement code changes for all issues.
5. Add or update migrations if schema changes are required.
6. Add or update automated tests.
7. Run build, tests, and lint/static checks if available.
8. Perform a self-audit comparing completed changes against all 12 issues.
9. Fix any remaining gaps before concluding.

Required deliverables:
- Actual code changes in the repository
- Migration files if required
- Updated tests
- Final implementation report

In the final implementation report, include these sections:

1. Executive Summary
   - Brief description of what was fixed

2. Issue-by-Issue Resolution
   For each of the 12 issues:
   - issue number and title
   - root cause found in code
   - files changed
   - exact fix implemented
   - whether schema/model/API/test updates were needed
   - any side effects handled

3. Additional Hidden Issues Found
   - Any related problems discovered and fixed beyond the audit

4. Validation Performed
   - Build results
   - Tests executed
   - Migrations validated
   - Runtime/logging behavior verified
   - Restart continuity verified
   - Audit row correctness verified

5. Risk Notes
   - Any residual risks or assumptions that still need business confirmation

6. Final Completion Checklist
   Explicitly confirm:
   - no issue skipped
   - no placeholder left
   - no missing implementation left
   - no known broken path left unfixed
   - no audit field left unwired
   - no hardcoded strategy-version issue remaining
   - no restart-state continuity gap remaining

Important coding constraints:
- Keep code production-ready and clean.
- Prefer explicit and readable logic over clever shortcuts.
- Keep naming consistent with the repo style.
- Preserve backward compatibility where safe, but do not keep broken behavior for compatibility.
- Add concise comments only where logic is subtle or business-critical.
- Do not fabricate test success; run real validations available in the repo.
- If something cannot be safely inferred, inspect surrounding code and implement the most correct repo-consistent behavior.

Failure is defined as ANY of the following:
- fixing fewer than all 12 issues
- partial wiring of a field without end-to-end usage
- fixing code but not tests
- fixing runtime logic but not persistence
- fixing schema but not inserts
- fixing live path but not warm-up path
- fixing state persistence but not reload
- fixing API but leaving hardcoded versioning elsewhere
- claiming completion without validation

Success is defined only when:
- all 12 audit issues are fully resolved in code
- all related gaps discovered during implementation are also handled
- the solution builds and tests cleanly
- the final report proves end-to-end completion