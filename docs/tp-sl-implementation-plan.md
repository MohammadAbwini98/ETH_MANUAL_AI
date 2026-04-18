# TP/SL Engine & Signal Quality — Technical Implementation Plan

## Status Key
- `[MISSING]` — Not implemented anywhere in codebase
- `[PARTIAL]` — Exists but incomplete or incorrectly scoped
- `[BUG]` — Implemented but has a correctness issue
- `[DONE]` — Resolved (noted for reference only)

---

## Part 1 — Immediate Fixes (No Schema Change Required)

### 1.1 `[BUG]` Timeframe-Agnostic Swing Lookback

**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` ~line 1385

**Problem:**
The swing extreme used for SL computation always looks back exactly 5 bars regardless of timeframe:

```csharp
var recentCandles = closed1m.TakeLast(5);
swingExtreme = signal.Direction == SignalDirection.BUY
    ? recentCandles.Min(c => c.MidLow)
    : recentCandles.Max(c => c.MidHigh);
```

For a 1h signal, 5 × 1m candles = 5 minutes of lookback. This produces structurally meaningless stops on higher timeframes. The swing low/high used for SL has no relation to the timeframe being traded.

**Fix — Step 1:** Add parameters to `StrategyParameters.cs`:

```csharp
/// <summary>Swing lookback in 1m bars for scalp signals.</summary>
public int ScalpSwingLookbackBars { get; init; } = 10;   // 10 × 1m = 10 min

/// <summary>Swing lookback in 1m bars for intraday signals (5m/15m/30m).</summary>
public int IntradaySwingLookbackBars { get; init; } = 48; // 48 × 1m = 48 min (approx 8 × 5m bars)

/// <summary>Swing lookback in 1m bars for higher timeframe signals (1h+).</summary>
public int HigherTfSwingLookbackBars { get; init; } = 300; // 300 × 1m = 5h (approx 5 × 1h bars)
```

**Fix — Step 2:** Replace the hardcoded `TakeLast(5)` in `LiveTickProcessor.cs`:

```csharp
int swingLookback = signal.Timeframe switch
{
    "1m" => p.ScalpSwingLookbackBars,
    "5m" or "15m" or "30m" => p.IntradaySwingLookbackBars,
    _ => p.HigherTfSwingLookbackBars
};
var recentCandles = closed1m.TakeLast(swingLookback);
swingExtreme = signal.Direction == SignalDirection.BUY
    ? recentCandles.Min(c => c.MidLow)
    : recentCandles.Max(c => c.MidHigh);
```

**Affected callers:** All paths that build `swingExtreme` before calling `RiskManager.ComputeRisk`. Search for `TakeLast(5)` or `swingExtreme` assignments in `LiveTickProcessor.cs` and apply the same pattern.

---

### 1.2 `[MISSING]` Confidence-Scaled TP Multiplier

**Files:**
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` (all `ComputeRisk` call sites)

**Problem:**
`RewardToRisk` in `RiskPolicy` is a fixed scalar. A confidence-90 signal and a confidence-60 signal both receive an identical TP at `1.5 × stopDistance`. High-confidence setups in directional conditions can support a wider target; marginal signals should use a tighter exit.

**Fix — Step 1:** Add to `StrategyParameters.cs`:

```csharp
/// <summary>TP R-multiple for signals at or above HighConfidenceThreshold.</summary>
public decimal HighConfidenceRewardMultiplier { get; init; } = 2.0m;

/// <summary>TP R-multiple for signals at or above MediumConfidenceThreshold.</summary>
public decimal MediumConfidenceRewardMultiplier { get; init; } = 1.5m;

/// <summary>TP R-multiple for signals below MediumConfidenceThreshold (at-threshold entries).</summary>
public decimal LowConfidenceRewardMultiplier { get; init; } = 1.2m;

/// <summary>Score >= this qualifies as high confidence for TP scaling.</summary>
public int HighConfidenceThreshold { get; init; } = 85;

/// <summary>Score >= this qualifies as medium confidence for TP scaling.</summary>
public int MediumConfidenceThreshold { get; init; } = 70;
```

Also propagate these fields through `RiskPolicy` (add matching properties) or pass directly into `ComputeRisk`.

**Fix — Step 2:** Modify `RiskManager.ComputeRisk` signature:

```csharp
public static RiskResult ComputeRisk(
    SignalDirection direction,
    decimal entryPrice,
    decimal atr,
    decimal swingExtreme,
    decimal spreadPct,
    RiskPolicy policy,
    int confidenceScore = 0)   // NEW parameter
```

**Fix — Step 3:** Replace the fixed `policy.RewardToRisk` usage inside `ComputeRisk`:

```csharp
decimal rewardMultiplier = confidenceScore >= policy.HighConfidenceThreshold
    ? policy.HighConfidenceRewardMultiplier
    : confidenceScore >= policy.MediumConfidenceThreshold
        ? policy.MediumConfidenceRewardMultiplier
        : policy.LowConfidenceRewardMultiplier;

decimal tp = direction == SignalDirection.BUY
    ? entryPrice + rewardMultiplier * stopDistance
    : entryPrice - rewardMultiplier * stopDistance;
```

**Fix — Step 4:** Update all `ComputeRisk` call sites in `LiveTickProcessor.cs` to pass `signal.ConfidenceScore` (or the score returned from `SignalEngine.EvaluateWithDecision`).

---

### 1.3 `[MISSING]` Regime-Aware TP Multiplier

**Files:**
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs`

**Problem:**
In a BULLISH or BEARISH trending regime, extended momentum can support a 2.0–2.5R target. In a ranging or NEUTRAL regime, a 1.5R target is often unreachable. Both get 1.5R today.

**Fix — Step 1:** Add to `StrategyParameters.cs`:

```csharp
/// <summary>TP R-multiple override when regime is strongly trending (BULLISH/BEARISH).</summary>
public decimal TrendingRegimeRewardMultiplier { get; init; } = 2.0m;

/// <summary>TP R-multiple override when regime is ranging/sideways.</summary>
public decimal RangingRegimeRewardMultiplier { get; init; } = 1.2m;

/// <summary>TP R-multiple for NEUTRAL or unknown regime.</summary>
public decimal NeutralRegimeRewardMultiplier { get; init; } = 1.5m;
```

**Fix — Step 2:** Pass `Regime?` into `ComputeRisk` and combine with confidence scaling:

```csharp
public static RiskResult ComputeRisk(
    SignalDirection direction,
    decimal entryPrice,
    decimal atr,
    decimal swingExtreme,
    decimal spreadPct,
    RiskPolicy policy,
    int confidenceScore = 0,
    Regime? regime = null)   // NEW parameter
{
    // ...existing checks...

    decimal regimeMultiplier = regime switch
    {
        Regime.BULLISH or Regime.BEARISH => policy.TrendingRegimeRewardMultiplier,
        Regime.RANGING                   => policy.RangingRegimeRewardMultiplier,
        _                                => policy.NeutralRegimeRewardMultiplier
    };

    decimal confidenceMultiplier = confidenceScore >= policy.HighConfidenceThreshold
        ? policy.HighConfidenceRewardMultiplier
        : confidenceScore >= policy.MediumConfidenceThreshold
            ? policy.MediumConfidenceRewardMultiplier
            : policy.LowConfidenceRewardMultiplier;

    // Take the higher of regime and confidence multipliers
    decimal rewardMultiplier = Math.Max(regimeMultiplier, confidenceMultiplier);
    // ...
}
```

Pass `_currentRegimeResult?.Regime` from `LiveTickProcessor` into all `ComputeRisk` call sites.

---

### 1.4 `[MISSING]` ATR-Based TP Sanity Check (Cap Unreachable Targets)

**File:** `src/EthSignal.Infrastructure/Engine/RiskManager.cs`

**Problem:**
`tp = entry + rewardMultiplier × stopDistance` can produce a TP that exceeds what the market can deliver in any realistic session move. There is no check that `tpDistance` is plausible relative to current ATR.

**Fix — Step 1:** Add to `StrategyParameters.cs`:

```csharp
/// <summary>
/// Maximum TP distance expressed as a multiple of ATR14.
/// Prevents targets that are statistically unreachable in a single session.
/// </summary>
public decimal AtrTpMaxMultiple { get; init; } = 3.0m;
```

**Fix — Step 2:** Add after `tp` is computed in `ComputeRisk`:

```csharp
decimal tpDistance = Math.Abs(tp - entryPrice);
decimal maxTpDistance = policy.AtrTpMaxMultiple * atr;

if (tpDistance > maxTpDistance)
{
    // Cap TP rather than reject — reduce target to ATR ceiling
    tp = direction == SignalDirection.BUY
        ? entryPrice + maxTpDistance
        : entryPrice - maxTpDistance;
    tpDistance = maxTpDistance;
}

// Verify R:R after potential cap
decimal actualRR = stopDistance > 0 ? tpDistance / stopDistance : 0;
if (actualRR < policy.MinRiskRewardAfterRounding)
    return Blocked($"R:R({actualRR:F2}) < min({policy.MinRiskRewardAfterRounding}) after ATR TP cap");
```

Note: the existing `actualRR` check at line 78 of `RiskManager.cs` must be moved to after this block, or merged into it, to avoid duplicate computation.

---

### 1.5 `[BUG]` Silent Pre-Audit Exits in `TryEvaluateScalpSignal`

**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` lines 1226–1265

**Problem:**
Five early-exit conditions return with no audit record and only `LogDebug` output (suppressed in production `appsettings.json`). Operators have no visibility into why scalp is silent during extended windows.

| Approx Line | Condition | Audit Written? | Current Log Level |
|-------------|-----------|----------------|-------------------|
| 1229 | Cooldown active | No | Debug |
| 1239 | Insufficient 1m bars | No | Debug |
| 1246 | Null snapshot | No | Debug |
| 1257 | Provisional snapshot | No | Debug |
| 1265 | ATR < ScalpMinAtr | No | Debug |

**Fix:** Promote the two highest-frequency gates to `LogInformation`:

```csharp
// Cooldown gate — replace LogDebug with:
_logger.LogInformation(
    "[1m] Scalp cooldown: {BarsSinceLast}/{Required} bars elapsed since last scalp @ {LastBar}",
    barsSinceLast, p.ScalpCooldownBars, _lastScalpBarTime);

// ATR gate — replace LogDebug with:
_logger.LogInformation(
    "[1m] Scalp skipped — ATR({Atr:F3}) < ScalpMinAtr({Min:F3}) @ bar {Bar}",
    snapshot.Atr14, p.ScalpMinAtr, barTime);
```

**Alternative (preferred for dashboard visibility):** Write a `CONTEXT_NOT_READY` audit record for each suppressed evaluation with a `FinalBlockReason` populated. This feeds the rejection funnel chart in the portal without requiring log parsing.

---

### 1.6 `[BUG]` `ScalpWarmUpBars` Parameter is Dead Code

**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` line 1233

**Problem:**
```csharp
var requiredBars = Math.Max(p.ScalpWarmUpBars, p.WarmUpPeriod) + 10;
// ...
if (closed1m.Count < p.WarmUpPeriod)   // gates on WarmUpPeriod, not ScalpWarmUpBars
```

`ScalpWarmUpBars = 30` is always overridden by `WarmUpPeriod = 50`. The smaller value never gates anything.

**Fix — Option A (recommended):** Remove `ScalpWarmUpBars` from `StrategyParameters.cs` and replace all usages with `WarmUpPeriod`. Update any optimizer or seeder that writes `ScalpWarmUpBars`.

**Fix — Option B:** Make it operative by changing the gate:

```csharp
if (closed1m.Count < p.ScalpWarmUpBars)
{
    _logger.LogDebug("[1m] Scalp warm-up: {Count}/{Required} 1m bars",
        closed1m.Count, p.ScalpWarmUpBars);
    return;
}
```

Choose Option A unless `ScalpWarmUpBars` is intentionally distinct from the HTF warmup period.

---

### 1.7 `[BUG]` `maxOpenPositions = 1` Blocks All 1m Scalp During Any Open HTF Trade

**File:** Active parameter set in DB; check enforced at `LiveTickProcessor.cs` ~line 1335

**Problem:**
`maxOpenPositions = 1` means any open 5m or 15m signal completely blocks all 1m scalp attempts. Since HTF signals can remain open for `OutcomeTimeoutBars × tf.Minutes` (e.g., 60 × 5m = 300 minutes), scalp is blocked for hours. The session logs showed `MaxOpenPositions(1) reached (1 open)` continuously throughout the day.

**Fix — Option A (trivial):** Change the active parameter set in DB:

```sql
UPDATE "ETH".parameter_sets
SET max_open_positions = 3
WHERE is_active = TRUE;
```

Set to `2` or `3` to allow scalp to coexist with one open HTF signal.

**Fix — Option B (structural):** Use the existing `MaxOpenPerTimeframe` scoping already in `CheckScopedCapacity`:

```sql
UPDATE "ETH".parameter_sets
SET max_open_per_timeframe = 1
WHERE is_active = TRUE;
```

This allows one signal per timeframe (1m gets its own independent slot), so a 15m signal does not block a 1m scalp slot. `max_open_positions` can be set to a higher ceiling (e.g., 4).

**Fix — Option C (code change):** Add a dedicated scalp capacity parameter:

```csharp
// In StrategyParameters.cs:
public int MaxOpenScalpPositions { get; init; } = 1;

// In CheckScopedCapacity — add a separate check for 1m:
if (signalTimeframe == "1m")
{
    int openScalps = openSignals.Count(s => s.Timeframe == "1m");
    if (openScalps >= p.MaxOpenScalpPositions)
        return (RejectReasonCode.MAX_OPEN_PER_TIMEFRAME,
            $"MaxOpenScalpPositions({p.MaxOpenScalpPositions}) reached ({openScalps} open scalps)");
}
```

**Recommendation:** Apply Option A immediately as a hotfix. Implement Option B or C in Sprint 1.

---

### 1.8 `[BUG]` Session Limits Are Not Scoped to Scalp vs. HTF

**File:** `src/EthSignal.Infrastructure/Engine/RiskManager.cs` lines 139–163

**Problem:**
`MaxConsecutiveLossesPerDay = 4` and `DailyMaxDrawdownPercent = 5.0%` are applied identically to both HTF and 1m scalp. After 4 HTF losses (which can each be large R moves), the scalp is blocked entirely even if no scalp trades have been taken. The two strategies have very different loss characteristics.

**Fix — Step 1:** Add to `StrategyParameters.cs`:

```csharp
/// <summary>Max consecutive losses before blocking scalp specifically. 0 = use shared limit.</summary>
public int ScalpMaxConsecutiveLossesPerDay { get; init; } = 6;

/// <summary>Max daily drawdown percent before blocking scalp specifically. 0 = use shared limit.</summary>
public decimal ScalpDailyMaxDrawdownPercent { get; init; } = 3.0m;
```

**Fix — Step 2:** Add a `bool isScalp` parameter to `CheckSessionLimits`:

```csharp
public static string? CheckSessionLimits(
    RiskPolicy policy,
    int openPositionCount,
    IReadOnlyList<SignalOutcome> todayOutcomes,
    bool isScalp = false)   // NEW
{
    // ...existing open position check...

    int maxConsecutive = isScalp && policy.ScalpMaxConsecutiveLossesPerDay > 0
        ? policy.ScalpMaxConsecutiveLossesPerDay
        : policy.MaxConsecutiveLossesPerDay;

    decimal maxDailyDD = isScalp && policy.ScalpDailyMaxDrawdownPercent > 0
        ? policy.ScalpDailyMaxDrawdownPercent
        : policy.DailyMaxDrawdownPercent;

    // Use maxConsecutive and maxDailyDD in checks instead of the policy properties directly
}
```

Pass `isScalp: true` from `TryEvaluateScalpSignal` call sites.

---

## Part 2 — Phase 2 Improvements (Schema Changes Required)

### 2.1 `[MISSING]` Multi-Target TP Model (TP1 / TP2 / Trailing)

**Problem:**
The `signals` table has a single `tp_price`. A partial close at TP1 + full exit at TP2 + trailing runner requires schema, outcome resolution, and risk manager changes.

**Required Schema Migration:**

```sql
ALTER TABLE "ETH".signals
    ADD COLUMN IF NOT EXISTS tp1_price          NUMERIC,
    ADD COLUMN IF NOT EXISTS tp2_price          NUMERIC,
    ADD COLUMN IF NOT EXISTS tp1_close_pct      NUMERIC  DEFAULT 0.5,
    ADD COLUMN IF NOT EXISTS partial_close_filled BOOLEAN DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS trailing_stop_active BOOLEAN DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS trailing_stop_atr_multiple NUMERIC;
```

**Required Code Changes:**

**A. `RiskManager.cs` — extend `RiskResult` record:**

```csharp
public record RiskResult(
    bool Allowed,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,          // legacy field / TP2
    decimal? TakeProfit1,        // NEW: early partial target
    decimal? TakeProfit2,        // NEW: full structure target
    bool TrailingEnabled,        // NEW
    decimal StopDistance,
    decimal RiskUsd,
    decimal RiskPercent,
    string? BlockReason);
```

**B. `RiskManager.ComputeRisk()` — compute TP1 and TP2:**

```csharp
// TP1 = entry ± 1.0 × stopDistance  (quick partial exit at 1:1)
// TP2 = entry ± rewardMultiplier × stopDistance  (structure target)
decimal tp1 = direction == SignalDirection.BUY
    ? entryPrice + 1.0m * stopDistance
    : entryPrice - 1.0m * stopDistance;

decimal tp2 = direction == SignalDirection.BUY
    ? entryPrice + rewardMultiplier * stopDistance
    : entryPrice - rewardMultiplier * stopDistance;

return new RiskResult(
    Allowed: true,
    EntryPrice: entryPrice,
    StopLoss: sl,
    TakeProfit: tp2,      // backward compat
    TakeProfit1: tp1,
    TakeProfit2: tp2,
    TrailingEnabled: regime is Regime.BULLISH or Regime.BEARISH,
    StopDistance: stopDistance,
    RiskUsd: riskUsd,
    RiskPercent: policy.RiskPercentPerTrade,
    BlockReason: null);
```

**C. `SignalRepository` — update `InsertSignalAsync` to persist `tp1_price`, `tp2_price`, `tp1_close_pct`, `trailing_stop_active`, `trailing_stop_atr_multiple`.**

**D. `LiveTickProcessor.EvaluateOpenSignalsAsync` — detect TP1 hit:**

```csharp
// When price reaches TP1 and partial close not yet filled:
if (!signal.PartialCloseFilled
    && signal.Tp1Price.HasValue
    && IsPriceAtOrBeyond(currentPrice, signal.Tp1Price.Value, signal.Direction))
{
    await _signalRepo.MarkPartialCloseAsync(signal.SignalId, ct);
    // Move SL to breakeven — see section 2.2
}
```

**E. `SignalOutcome` — track weighted PnL for partial close (see section 2.2).**

---

### 2.2 `[MISSING]` Breakeven Stop After TP1

**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — `EvaluateOpenSignalsAsync`

**Problem:**
After TP1 is hit and a partial close occurs, the SL remains at the original level. A reversal back through the original SL results in a net loss on the remaining position. Standard practice: move SL to entry after TP1.

**Required Change:**

```csharp
// Inside EvaluateOpenSignalsAsync, immediately after MarkPartialCloseAsync:
await _signalRepo.UpdateSlAsync(signal.SignalId, signal.EntryPrice, ct);
_logger.LogInformation(
    "[{Tf}] TP1 hit @ {Tp1} — partial close recorded, SL moved to breakeven {Entry}",
    signal.Timeframe, signal.Tp1Price, signal.EntryPrice);
```

**Required new repository method:**

```csharp
// In ISignalRepository and SignalRepository:
Task UpdateSlAsync(Guid signalId, decimal newSl, CancellationToken ct = default);
```

---

### 2.3 `[MISSING]` Trailing Stop for Runner Position

**File:** `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — `EvaluateOpenSignalsAsync`

**Problem:**
After TP1 partial close, the remaining half-position is exited at a fixed TP2. In strongly trending regimes, price may run significantly past TP2. A trailing stop captures extended moves without sacrificing guaranteed profit.

**Required Change (activates only after TP1 hit + partial close):**

```csharp
if (signal.PartialCloseFilled
    && signal.TrailingStopActive
    && currentAtr > 0)
{
    decimal trailMultiple = signal.TrailingStopAtrMultiple ?? 1.5m;
    decimal newTrailSl = signal.Direction == SignalDirection.BUY
        ? currentPrice - (trailMultiple * currentAtr)
        : currentPrice + (trailMultiple * currentAtr);

    bool slImproved = signal.Direction == SignalDirection.BUY
        ? newTrailSl > signal.SlPrice
        : newTrailSl < signal.SlPrice;

    if (slImproved)
    {
        await _signalRepo.UpdateSlAsync(signal.SignalId, newTrailSl, ct);
        _logger.LogDebug(
            "[{Tf}] Trailing SL updated: {OldSl:F2} → {NewSl:F2} (price={Price:F2})",
            signal.Timeframe, signal.SlPrice, newTrailSl, currentPrice);
    }
}
```

**Note:** Trailing stop is only activated in trending regime. The `TrailingStopActive` flag is set in `ComputeRisk` based on regime (see section 2.1-B above).

---

## Part 3 — Phase 3 Improvements (Data-Gated: ≥50 Outcomes Per Setup Type)

> **Prerequisite:** Do not implement Phase 3 features until the following minimums are met in the live DB:
> - ≥50 closed signal outcomes per `(timeframe, direction, regime)` combination
> - ≥50 closed 1m scalp outcomes specifically
>
> Current state (April 16): 8 total 1m scalp signals, ~5 HTF signals. Phase 3 is not actionable.

### 3.1 `[MISSING]` MFE / MAE Tracking Per Signal

**Required Schema Migration:**

```sql
ALTER TABLE "ETH".signal_outcomes
    ADD COLUMN IF NOT EXISTS max_favorable_excursion_r NUMERIC,
    ADD COLUMN IF NOT EXISTS max_adverse_excursion_r   NUMERIC,
    ADD COLUMN IF NOT EXISTS time_to_outcome_minutes   INTEGER;
```

**Required Code Change — `LiveTickProcessor.EvaluateOpenSignalsAsync`:**

Track running extremes on each evaluation tick for open signals:

```csharp
// Per open signal per tick — compute current excursion in R units
decimal currentExcursionR = signal.Direction == SignalDirection.BUY
    ? (currentPrice - signal.EntryPrice) / signal.StopDistance
    : (signal.EntryPrice - currentPrice) / signal.StopDistance;

// Update in-memory max favorable / adverse
if (currentExcursionR > signal.CurrentMfe) signal = signal with { CurrentMfe = currentExcursionR };
if (currentExcursionR < signal.CurrentMae) signal = signal with { CurrentMae = currentExcursionR };
```

Persist `CurrentMfe` and `CurrentMae` to `signal_outcomes` when the signal closes.

### 3.2 `[MISSING]` TP Calibration from Historical MFE Distribution

Once ≥50 MFE samples exist per setup type:

```sql
-- Query to find 60th-percentile MFE per setup:
SELECT timeframe, direction, regime,
       PERCENTILE_CONT(0.60) WITHIN GROUP (ORDER BY max_favorable_excursion_r) AS p60_mfe
FROM "ETH".signal_outcomes so
JOIN "ETH".signals s ON so.signal_id = s.id
WHERE so.closed_at_utc >= NOW() - INTERVAL '90 days'
GROUP BY timeframe, direction, regime;
```

Use the P60 MFE as a data-driven TP target:
- If `p60_mfe < 1.0` for a setup type, reject it (expected value negative after spread)
- If `p60_mfe` is available, set `TP = entry ± min(atr_tp, p60_mfe × stopDistance)`

### 3.3 `[MISSING]` SL Calibration from Historical MAE Distribution

Once ≥50 MAE samples exist per setup type:

```sql
-- 90th-percentile adverse excursion:
SELECT timeframe, direction, regime,
       PERCENTILE_CONT(0.90) WITHIN GROUP (ORDER BY ABS(max_adverse_excursion_r)) AS p90_mae
FROM "ETH".signal_outcomes so
...
```

Use P90 MAE as a minimum SL distance floor:
- If the computed `stopDistance / entryPrice` is less than `p90_mae × atr`, widen the SL
- Prevents stops that are historically too tight for the setup type

---

## Part 4 — Infrastructure / Observability Gaps

### 4.1 `[MISSING]` Resistance / Support Proximity Validation Before Accepting TP

**Problem:**
The design document requires rejecting or capping TP when a known resistance level (or recent swing high/low) lies between entry and TP. No resistance detection exists in the codebase.

**Minimum Viable Approach (no external data feed required):**

Use closed candle history as proxy resistance:

```csharp
// In RiskManager.ComputeRisk (or a new ValidateTpAgainstStructure method):
// For BUY: find nearest swing high between entry and computed TP
static decimal? FindNearestResistance(
    IReadOnlyList<Candle> candles,
    decimal entryPrice,
    decimal tpPrice,
    SignalDirection direction)
{
    if (direction == SignalDirection.BUY)
    {
        return candles
            .Where(c => c.MidHigh > entryPrice && c.MidHigh < tpPrice)
            .Select(c => (decimal?)c.MidHigh)
            .OrderBy(h => h)
            .FirstOrDefault();
    }
    else
    {
        return candles
            .Where(c => c.MidLow < entryPrice && c.MidLow > tpPrice)
            .Select(c => (decimal?)c.MidLow)
            .OrderByDescending(l => l)
            .FirstOrDefault();
    }
}
```

If resistance is found within 60% of the path from entry to TP:

```csharp
decimal pathToResistance = Math.Abs(resistance.Value - entryPrice);
decimal pathToTp = Math.Abs(tp - entryPrice);

if (pathToResistance < pathToTp * 0.60m)
{
    // Option A: cap TP at resistance - 1 tick
    tp = direction == SignalDirection.BUY
        ? resistance.Value - spreadBuffer
        : resistance.Value + spreadBuffer;

    // Option B: reject if adjusted R:R falls below minimum
    decimal newRR = Math.Abs(tp - entryPrice) / stopDistance;
    if (newRR < policy.MinRiskRewardAfterRounding)
        return Blocked($"ResistanceProximity: adjusted R:R({newRR:F2}) < min after S/R cap");
}
```

**Note:** Pass closed candle history into `ComputeRisk` or perform this check before calling it in `LiveTickProcessor`.

### 4.2 `[MISSING]` Scalp Evaluation Funnel Diagnostics

**Problem:**
With debug logs suppressed, there is no visibility into how many 1m evaluations are occurring vs. being silently suppressed by cooldown, ATR, warmup, or regime gates.

**Fix — Add counters to `LiveTickProcessor`:**

```csharp
// Private fields:
private int _scalpEvaluationsTotal;
private int _scalpCooldownBlocks;
private int _scalpAtrBlocks;
private int _scalpWarmupBlocks;
private int _scalpRegimeStaleBlocks;
private int _scalpCapacityBlocks;
private int _scalpCandidatesGenerated;

// Increment at each gate in TryEvaluateScalpSignal:
Interlocked.Increment(ref _scalpEvaluationsTotal);
// ...
Interlocked.Increment(ref _scalpCooldownBlocks);
return;
```

**Expose via portal diagnostics endpoint** in `MarketState` or a dedicated `/api/diagnostics/scalp` route:

```json
{
  "scalpEvaluationsTotal": 1247,
  "scalpCooldownBlocks": 820,
  "scalpAtrBlocks": 103,
  "scalpWarmupBlocks": 0,
  "scalpRegimeStaleBlocks": 41,
  "scalpCapacityBlocks": 214,
  "scalpCandidatesGenerated": 69
}
```

### 4.3 `[DONE]` DB Migration for Audit Columns

**Status:** Resolved. `migrate-srs-phase1.sql` adds `lifecycle_state`, `final_block_reason`, `origin`, `evaluation_id`, `effective_runtime_parameters_json` to `signal_decision_audit`. After this migration was applied, the live DB shows 91 1m audit rows.

**Verify post-migration:**

```sql
SELECT column_name
FROM information_schema.columns
WHERE table_schema = 'ETH'
  AND table_name   = 'signal_decision_audit'
ORDER BY ordinal_position;
```

Expected: 23 columns. If still 18, the migration has not been applied.

---

## Implementation Priority Matrix

| # | Item | Section | Effort | Impact | Target |
|---|------|---------|--------|--------|--------|
| 1 | Raise `maxOpenPositions` to 2–3 (DB param) | 1.7 Option A | Trivial | Critical | Now |
| 2 | Apply `migrate-srs-phase1.sql` | 4.3 | Done | Critical | Done |
| 3 | Timeframe-aware swing lookback | 1.1 | Small (~20 lines) | High | Sprint 1 |
| 4 | Promote silent scalp exits to Info log | 1.5 | Small (~10 lines) | High (visibility) | Sprint 1 |
| 5 | Fix `ScalpWarmUpBars` dead code | 1.6 | Small | Low | Sprint 1 |
| 6 | ATR-based TP sanity check / cap | 1.4 | Small–Medium | Medium | Sprint 1 |
| 7 | Confidence-scaled TP multiplier | 1.2 | Medium (2 files) | High | Sprint 1 |
| 8 | Regime-aware TP multiplier | 1.3 | Medium (2 files) | High | Sprint 1 |
| 9 | Scalp-specific session limits | 1.8 | Medium (2 files) | Medium | Sprint 2 |
| 10 | `MaxOpenPerTimeframe` scoping for 1m | 1.7 Option B | Medium | High | Sprint 2 |
| 11 | Scalp evaluation funnel diagnostics | 4.2 | Medium | Medium | Sprint 2 |
| 12 | Schema migration: `tp1_price`, `tp2_price`, etc. | 2.1 | Small (SQL only) | Dependency | Sprint 3 |
| 13 | `RiskResult` TP1/TP2 fields + `ComputeRisk` | 2.1 | Medium (2 files) | High | Sprint 3 |
| 14 | Breakeven stop after TP1 | 2.2 | Medium (1 file + repo) | High | Sprint 3 |
| 15 | Trailing stop for runner | 2.3 | Medium (1 file) | Medium | Sprint 3 |
| 16 | Resistance proximity check | 4.1 | Large | Medium | Sprint 4 |
| 17 | Schema migration: MFE/MAE columns | 3.1 | Small (SQL only) | Dependency | Sprint 4 |
| 18 | MFE/MAE collection in tick processor | 3.1 | Medium | High | Sprint 4 |
| 19 | TP calibration from MFE distribution | 3.2 | Large | High | Sprint 5 (data-gated) |
| 20 | SL calibration from MAE distribution | 3.3 | Large | High | Sprint 5 (data-gated) |

---

## Appendix: Files Touched Per Sprint

### Sprint 1
- `src/EthSignal.Domain/Models/StrategyParameters.cs` — add swing lookback, confidence/regime multiplier, ATR TP max params
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs` — confidence/regime scaling, ATR TP cap, update `ComputeRisk` signature
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — fix swing lookback, promote log levels, fix `ScalpWarmUpBars` gate, update `ComputeRisk` call sites

### Sprint 2
- `src/EthSignal.Domain/Models/StrategyParameters.cs` — scalp session limit params, `MaxOpenPerTimeframe`
- `src/EthSignal.Domain/Models/RiskPolicy.cs` — propagate scalp limit fields
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs` — `CheckSessionLimits` `isScalp` flag
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — pass `isScalp`, counters, expose diagnostics

### Sprint 3
- `migrations/add_tp_multi_target.sql` — new migration file
- `src/EthSignal.Domain/Models/SignalRecommendation.cs` — add `Tp1Price`, `Tp2Price`, `PartialCloseFilled`, `TrailingStopActive`
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs` — `RiskResult` TP1/TP2 fields
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs` — `InsertSignalAsync`, `UpdateSlAsync`, `MarkPartialCloseAsync`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — TP1 detection, breakeven SL, trailing stop

### Sprint 4
- `migrations/add_mfe_mae.sql` — new migration file
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs` — MFE/MAE tracking in open signal evaluation
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs` / `OutcomeRepository.cs` — persist MFE/MAE
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs` — resistance proximity check (new helper method)

### Sprint 5 (data-gated)
- `src/EthSignal.Infrastructure/Engine/RiskManager.cs` or new `TpCalibrationService` — MFE/MAE-driven TP and SL calibration
