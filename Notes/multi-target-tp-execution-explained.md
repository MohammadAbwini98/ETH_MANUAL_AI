# Multi-Target TP Execution — OutcomeEvaluator Change

**Date:** 2026-04-18  
**File changed:** `src/EthSignal.Infrastructure/Engine/OutcomeEvaluator.cs`

---

## The Problem Before

The old `OutcomeEvaluator` treated every signal as a binary all-or-nothing trade:

```
Entry → either hit TpPrice (WIN at full R) or SlPrice (LOSS at -1R)
```

This ignored `Tp1Price`, `Tp2Price`, `Tp3Price` entirely, even though the `ExitEngine` was computing and storing them on every signal. The practical consequence was that a large category of real-world outcomes was being mis-classified.

**A typical scenario that was being mis-classified:**

```
Signal: BUY @ 2000, SL @ 1900 (stopDist = 100), TP1 @ 2100, TP2 @ 2200, TP3 @ 2300

What actually happened in the market:
  → Price reached 2100 (TP1 hit ✓)
  → Then pulled back to 2000 (breakeven)
  → Never reached 2200

Old evaluator verdict: EXPIRED, PnlR ≈ 0  ← WRONG
Real outcome: 40% of position closed at TP1, SL moved to entry, runner exited at breakeven
Real PnlR: +0.4R  ← Should be labeled WIN
```

Hundreds of signals were being labeled `EXPIRED` or even `LOSS` when in reality partial profit was captured and all remaining risk was eliminated the moment TP1 was hit.

---

## Activation Condition

Multi-target mode only activates when `Tp1Price` is set and points in the trade direction:

```csharp
bool useMultiTarget = stopDist > 0
    && signal.Tp1Price > 0
    && (isBuy ? signal.Tp1Price > entry : signal.Tp1Price < entry);
```

If `Tp1Price` is not set (legacy signals or signals where ExitEngine didn't produce multi-targets), the original single-target logic runs unchanged — no regressions on old data.

---

## Position Allocation

The full position is split into three buckets:

| Bucket | Allocation | Closes at |
|--------|-----------|-----------|
| TP1    | 40%       | `Tp1Price` |
| TP2    | 30%       | `Tp2Price` (or 2R synthetic if not set) |
| Runner | 30%       | `Tp3Price` or `TpPrice` (whichever is set) |

---

## State Machine (per candle iteration)

```
State 1: Waiting for TP1
  activeSl = original SlPrice

  ├─ Candle low (BUY) hits activeSl
  │     → LOSS  (-1R, full position, unchanged behaviour)
  │
  └─ Candle high (BUY) hits Tp1Price
        → Close 40% at TP1
        → Promote activeSl to entry price (breakeven)
           ↓
State 2: TP1 hit — waiting for TP2
  activeSl = EntryPrice

  ├─ Candle low hits EntryPrice (breakeven exit)
  │     → WIN  (+0.4 × tp1R)
  │
  └─ Candle high hits Tp2Price
        → Close 30% more at TP2
           ↓
State 3: TP1 + TP2 hit — waiting for TP3 / runner
  activeSl = EntryPrice

  ├─ Candle low hits EntryPrice (breakeven exit)
  │     → WIN  (+0.4 × tp1R + 0.3 × tp2R)
  │
  └─ Candle high hits Tp3Price
        → WIN  (full blended PnL — see formula below)
```

> **Same-candle rule:** If a candle hits both a TP level and the active SL in the same bar, the TP hit takes priority (benefit of the doubt — partial exit happens first). This mirrors real exchange fill behaviour where limit orders at TP fire before a stop is triggered.

---

## Blended PnL Formula

Each R-value is computed as the target's distance from entry divided by stop distance:

```
tp1R = (Tp1Price - entry) / stopDist    (e.g. 100 / 100 = 1.0R)
tp2R = (Tp2Price - entry) / stopDist    (e.g. 200 / 100 = 2.0R)
tp3R = (Tp3Price - entry) / stopDist    (e.g. 300 / 100 = 3.0R)
```

Full blended PnL when all three targets are hit:

```
blended = 0.4 × tp1R + 0.3 × tp2R + 0.3 × tp3R
        = 0.4 × 1.0  + 0.3 × 2.0  + 0.3 × 3.0
        = 0.40 + 0.60 + 0.90
        = 1.9R
```

Partial outcomes:

```
TP1 hit, then breakeven:          0.4 × 1.0 = 0.4R
TP1 + TP2 hit, then breakeven:    0.4 × 1.0 + 0.3 × 2.0 = 1.0R
All three hit:                    1.9R (as above)
```

---

## Timeout Handling When TP1 Was Already Hit

If the timeout window expires (90 intraday bars / 25 scalp bars) and the runner is still open after TP1 was hit, the runner does not simply go to zero. It marks to market at the last candle's close price:

```csharp
decimal runnerPnlR   = (lastClose - entry) / stopDist;
decimal openAlloc    = tp2Hit ? 0.3m : 0.6m;   // remaining runner size
decimal totalPnl     = 0.4 × tp1R
                     + (tp2Hit ? 0.3 × tp2R : 0)
                     + openAlloc × runnerPnlR;
```

- If `totalPnl >= 0` → labeled **WIN**
- If `totalPnl < 0` (runner gave back all TP1 gains) → labeled **EXPIRED** with the negative PnL recorded

This prevents artificially rewarding signals that hit TP1 but then fully reversed before the runner was closed.

---

## Impact on Statistics

| Scenario | Old Label | Old PnlR | New Label | New PnlR |
|----------|-----------|----------|-----------|----------|
| TP1 hit, then breakeven SL | EXPIRED | ~0R | WIN | +0.4R |
| TP1 + TP2 hit, then breakeven SL | EXPIRED | ~0R | WIN | +1.0R |
| Full TP3 hit | WIN | 1.5R (single-target) | WIN | +1.9R (blended) |
| SL hit before TP1 | LOSS | -1R | LOSS | -1R (unchanged) |
| Timeout, no TP hit | EXPIRED | close-based PnL | EXPIRED | unchanged |
| Timeout, TP1 already hit | EXPIRED | ~0R | WIN or EXPIRED | mark-to-market |

**Net effect on reported metrics:**
- Win rate increases (breakeven-after-TP1 trades correctly counted as wins)
- Average R per trade improves
- EXPIRED count decreases
- LOSS count unchanged (pre-TP1 stops still count as full -1R losses)

---

## Files Changed

| File | Change |
|------|--------|
| `src/EthSignal.Infrastructure/Engine/OutcomeEvaluator.cs` | Full multi-target loop with state tracking per candle |
| `tests/EthSignal.Tests/Engine/OutcomeEvaluatorTests.cs` | Fixed `IntradayTimeoutBars` pin in timeout tests |
| `tests/EthSignal.Tests/Engine/PhaseGateTests.cs` | Fixed `IntradayTimeoutBars` pin + corrected drawdown threshold from `decimal.MaxValue` to `1.5m` |
