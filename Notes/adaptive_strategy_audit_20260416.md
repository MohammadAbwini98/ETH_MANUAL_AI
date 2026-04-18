# Adaptive Strategy Audit — 2026-04-16

## Why No New `adaptive_parameter_log` Entries Since 2026-04-15 22:35:34

The adaptive system **IS running** — condition changes appear in the logs throughout April 15 and 16.
However the `adaptive_parameter_log` DB table stopped receiving rows at `2026-04-15 22:35:34`.

### Root Cause Chain

1. The session that ran from ~19:19:50 ended; last DB write occurred at **22:35:34** (condition change during that session).
2. Service restarted at **22:31:23** — this reset `_lastCondition = null` and `_evaluationCount = 0`.
3. At 22:35:00 — startup grace skipped the pipeline: `5m candle incomplete during startup grace: 4/5 1m bars`. No `AdaptParameters` call.
4. First live eval at **22:40:00**: `_lastCondition = null → conditionChanged = false`, `1 % 12 ≠ 0` → **no DB write triggered**.
5. Second live eval at **22:45:00**: condition changed MODERATE → STRONG → `conditionChanged = true` → `_logRepo.LogAsync()` fires fire-and-forget — but condition changes continue appearing in the console log without corresponding DB writes.
6. The April 15 log shows **6 full service restarts** that day, each resetting all in-memory adaptive state.

---

## Full List of Adaptive Strategy Implementation Issues

### Issue #1 — `adapted_parameters_json` column NEVER written (Gap #2 incomplete)
**Severity:** 🔴 High  
**File:** `src/EthSignal.Infrastructure/Db/DecisionAuditRepository.cs` — `InsertDecisionAsync`

`DbMigrator` added the `adapted_parameters_json JSONB` column to `signal_decision_audit`, but `InsertDecisionAsync` never populates it. The `SignalDecision` record has no such property. This column is always `NULL` in the DB — the entire purpose of gap #2 (audit trail of what adaptive parameters were applied per decision) is not achieved.

---

### Issue #2 — `market_condition_class` in `signal_decision_audit` NEVER written
**Severity:** 🔴 High  
**File:** `src/EthSignal.Infrastructure/Db/DecisionAuditRepository.cs` — `InsertDecisionAsync`

Column was added to the schema by the migration, but the INSERT statement never writes to it. `currentConditionClass` is computed in `LiveTickProcessor.cs` and attached to `Signal` rows (line 1033) but not to `SignalDecision` rows. You cannot query "what condition was active when this NO_TRADE decision was made" — making condition-outcome analysis impossible on the full decision set.

---

### Issue #3 — All retrospective state lost on every restart
**Severity:** 🔴 High  
**File:** `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs` (lines 27–42)

`_conditionOutcomes`, `_retrospectiveOverlays`, `_lastCondition`, and `_evaluationCount` are all pure in-memory state with no persistence. The log shows **6+ restarts on April 15 alone**. After each restart:

- All outcome windows reset to zero.
- All retrospective overlays are cleared.
- The 15-outcome minimum to trigger a retrospective overlay cannot be reached across restart boundaries.
- **The retrospective refinement feature is permanently ineffective in production.**

**Fix required:** Persist `_conditionOutcomes` snapshots to DB on update and reload on startup to survive restarts.

---

### Issue #4 — Retrospective threshold hardcoded, ignores `AdaptiveRetrospectiveMinOutcomes`
**Severity:** 🟡 Medium  
**File:** `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs` line 173

```csharp
if (window.Count >= 15) // AdaptiveRetrospectiveMinOutcomes default
    RecomputeRetrospective(conditionClassKey, window);
```

The comment acknowledges the issue but the code ignores `baseParams.AdaptiveRetrospectiveMinOutcomes`. Changing this parameter in the DB has no effect on retrospective trigger behavior.

---

### Issue #5 — First evaluation after restart never triggers a DB log write (10–60 min gap)
**Severity:** 🟡 Medium  
**File:** `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs` line 127

```csharp
var conditionChanged = _lastCondition != null && _lastCondition != condition;
```

After restart `_lastCondition = null`. Even if the condition is different from the previous session's last condition, `conditionChanged` evaluates to `false` on the first evaluation (null guard fails). Combined with startup grace skipping the first incomplete 5m bar, there is always a **10–60 minute silence gap** in `adaptive_parameter_log` after every restart.

---

### Issue #6 — Warm-up path bypasses the adaptive system entirely
**Severity:** 🟡 Medium  
**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` line 247

The TF-5 warm-start evaluation calls `SignalEngine.EvaluateWithDecision` directly with base parameters `p`, skipping the `AdaptParameters` call at line 925. Consequences:

- Warm-up decisions are evaluated with unadapted parameters.
- `MarketConditionClass = null` on all warm-up `SignalDecision` rows.
- These decisions cannot contribute to retrospective learning.
- `adaptive_parameter_log` has no warm-up entries.

---

### Issue #7 — `/api/admin/parameter-sets/candidates` hardcodes `"v2.0"` version
**Severity:** 🟡 Medium  
**File:** `src/EthSignal.Web/Program.cs` line 973

```csharp
var candidates = await paramRepo.GetCandidatesAsync("v2.0");
```

The live strategy version is now `v3.1`. This endpoint always returns an empty list since no candidate exists with `strategy_version = 'v2.0'`. The candidates panel on the dashboard is permanently empty.

---

### Issue #8 — `RecordOutcome` ignores EXPIRED outcomes; worst conditions never penalized
**Severity:** 🟡 Medium  
**File:** `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs` line 162

```csharp
if (outcome.OutcomeLabel is not (OutcomeLabel.WIN or OutcomeLabel.LOSS)) return;
```

EXPIRED outcomes are silently discarded. Given that `labeled=2` in the ML diagnostics (most outcomes are still OPEN or EXPIRED), this means:

- Conditions that generate signals that expire are never penalized.
- True performance of high-signal-rate conditions is systematically understated.
- The retrospective overlay can only compute on the rare fully-resolved WIN/LOSS subset.

---

### Issue #9 — Cliff behavior: `NeutralRegimePolicyOverride` drops off at intensity < 0.5
**Severity:** 🟠 Low  
**File:** `src/EthSignal.Infrastructure/Engine/ML/AdaptiveOverlayResolver.cs` line 197

```csharp
if (o.NeutralRegimePolicyOverride.HasValue && intensity >= 0.5m)
    adapted = adapted with { NeutralRegimePolicy = o.NeutralRegimePolicyOverride.Value };
```

Setting intensity to `0.3` (light adaptation) silently disables all policy overrides while numeric deltas still scale linearly. This is non-intuitive and undocumented. A user expecting mild adaptation at 30% intensity would lose the policy gate entirely without warning.

---

### Issue #10 — Only 2–3 distinct condition keys; retrospective practically unreachable
**Severity:** 🟠 Low  
**Evidence:** Logs show oscillation between `NORMAL_MODERATE_NEW_YORK_TIGHT_DRY` and `NORMAL_STRONG_NEW_YORK_TIGHT_DRY`

With the current market state and 5m evaluation frequency, the system cycles through only 2–3 condition keys. Reaching 15 resolved WIN/LOSS outcomes per key requires 15+ resolved trades on exactly the same condition — taking many days at the current signal rate (~0–3 per session). Combined with restart state loss (Issue #3), the 15-outcome retrospective threshold is practically unreachable during normal operation.

---

### Issue #11 — DRY volume overlay incorrectly relaxes the volume gate
**Severity:** 🟠 Low  
**File:** `src/EthSignal.Infrastructure/Engine/ML/AdaptiveOverlayResolver.cs` lines 305–312

```csharp
VolumeTier.DRY => new ParameterOverlay
{
    ConfidenceBuyThresholdDelta = 5,
    ConfidenceSellThresholdDelta = 5,
    VolumeMultiplierMinOverride = 0.3m,  // ← relaxes the gate in thin market
    ...
}
```

When volume is DRY (thin/illiquid market), the overlay LOWERS the `VolumeMultiplierMin` from its base value. This makes it **easier** to enter a trade in an illiquid market — the opposite of safe behavior. The intent was likely to avoid requiring normal volume in slow periods, but it inadvertently allows lower-quality setups through.

---

### Issue #12 — `parameter_activation_history` grows unbounded with no deduplication
**Severity:** 🟠 Low  
**File:** `src/EthSignal.Infrastructure/Db/ParameterRepository.cs` — `ActivateAsync`

Every service restart that triggers an ML mode-switch or startup seed writes a new row to `parameter_activation_history`. With 6+ restarts per day and repeated startup mode-switches between the same parameter sets, the table accumulates duplicate activation records with no archival or deduplication logic.

---

## Summary Table

| # | Issue | Severity | Impact |
|---|-------|----------|--------|
| 1 | `adapted_parameters_json` always NULL — no adaptive audit per decision | 🔴 High | Audit trail incomplete |
| 2 | `market_condition_class` in decisions always NULL | 🔴 High | Can't analyze decisions by condition |
| 3 | Retrospective state lost on every restart | 🔴 High | Retrospective feature effectively disabled |
| 4 | Min-outcomes parameter ignored; hardcoded to 15 | 🟡 Medium | Config has no effect |
| 5 | 10–60 min gap in `adaptive_parameter_log` after restart | 🟡 Medium | Gaps in audit trail |
| 6 | Warm-up decisions bypass adaptive system | 🟡 Medium | Warm-start unaffected; NULL condition on decisions |
| 7 | Candidates API returns empty list (hardcoded `v2.0`) | 🟡 Medium | Dashboard candidates panel broken |
| 8 | EXPIRED outcomes silently discarded | 🟡 Medium | Worst conditions never penalized |
| 9 | Policy override cliff at intensity 0.5 | 🟠 Low | Non-intuitive behavior at low intensity |
| 10 | Only 2–3 condition keys; retrospective unreachable | 🟠 Low | Retrospective never fires in practice |
| 11 | DRY volume overlay relaxes gate instead of raising it | 🟠 Low | Higher entry risk in thin market |
| 12 | Activation history grows unbounded | 🟠 Low | DB bloat over time |
