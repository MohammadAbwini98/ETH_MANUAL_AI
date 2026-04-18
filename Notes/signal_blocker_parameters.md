# Signal Blocker Parameters — Complete Reference

> Active parameter set values pulled directly from DB (`ETH_BASE`, `ETH.strategy_parameter_sets` WHERE `status='Active'`).
> Last verified: 2026-04-17.

---

## Layer 0 — Pre-Evaluation Gates (`LiveTickProcessor`)

Checked before any indicator or strategy evaluation occurs. A block here means zero audit records are written for that evaluation cycle.

| # | Blocker | Parameter | Active DB Value | Notes |
|---|---------|-----------|----------------|-------|
| 0.1 | Data gap block | `GapBlockLookbackBars` | `2` | If unresolved gaps in last `2 × tf.Minutes`, entire TF skipped; regime marked stale |
| 0.2 | Regime staleness | `MaxRecoveredRegimeAgeBars` × `TimeframeBias` | `12 × 15m = 3 hours` | Kills the entire 1m eval loop if regime hasn't refreshed in 3h |
| 0.3 | Running candle maturity — fast TF (≤5m) | `RunningCandleMaturityFastTf` | `0.6` (60%) | 5m running candle needs ≥3 of 5 bars complete |
| 0.4 | Running candle maturity — slow TF (≥15m) | `RunningCandleMaturitySlowTf` | `0.5` (50%) | 15m running candle needs ≥8 of 15 bars complete |
| 0.5 | Scalp feature toggle | `ScalpingEnabled` | `true` | If `false`, entire 1m scalp path is unconditionally skipped |
| 0.6 | Scalp warmup | `ScalpWarmUpBars` | `30` | Need ≥30 closed 1m bars before scalp can fire |
| 0.7 | Indicator warmup | `WarmUpPeriod` | `50` | Indicator engine returns null/provisional until 50 bars exist |
| 0.8 | Scalp cooldown | `ScalpCooldownBars` | `15` | Minimum 15 minutes between any two 1m scalp signals |
| 0.9 | VWAP midnight crossing | *(hardcoded)* | — | Running candle skipped if 1m bucket spans UTC midnight — VWAP would be stale |

---

## Layer 1 — Strategy Engine (`SignalEngine.EvaluateWithDecision`)

| # | Blocker | Parameter | Active DB Value | Notes |
|---|---------|-----------|----------------|-------|
| 1.1 | Missing regime context | *(null check)* | — | `CONTEXT_NOT_READY` — fires on startup until first 15m candle closes |
| 1.2 | Provisional indicators | `WarmUpPeriod` | `50` | RSI/MACD/EMA unreliable below 50 bars → `CONTEXT_NOT_READY` |
| 1.3 | Neutral regime policy | `NeutralRegimePolicy` | `1` (AllowReducedRisk) | DB value `1` = `AllowReducedRiskEntriesInNeutral` → proceeds with evaluation. If `0` (BlockAll) hard-blocks all NEUTRAL entries |
| 1.4 | ATR too low — HTF | `MinAtrThreshold` | `0.3` | HTF signal blocked if ATR14 < 0.3 |
| 1.5 | ATR too low — scalp | `ScalpMinAtr` | `0.3` | 1m signal blocked if ATR14 < 0.3 |
| 1.6 | Spread too wide | `MaxSpreadPct` | `0.004` (0.4%) | Hard mandatory gate, direction-independent |
| 1.7 | Conflicting signals | `ConflictingScoreGap` | `3` | Both BUY and SELL above threshold AND within 3 pts of each other → `NO_TRADE` |
| 1.8 | Regime alignment | *(hardcoded)* | — | BUY requires BULLISH regime, SELL requires BEARISH — no parameter to relax |
| 1.9 | No entry condition | *(hardcoded)* | — | Pullback-and-reclaim **AND** (RSI pass **OR** MACD pass) both required |
| 1.10 | Score below threshold — HTF | `ConfidenceBuyThreshold` / `ConfidenceSellThreshold` | **`66` / `66`** | Code default is 45; DB uses 66 — requires nearly all conditions to pass simultaneously |
| 1.11 | Score below threshold — scalp | `ScalpConfidenceThreshold` | `60` | Applied after engine produces directional signal; 1m path only |

### Score Components

Max achievable score = **90** (with `WeightVolume = 0` in DB).

| Component | Weight (DB) | Condition to earn points |
|-----------|-------------|--------------------------|
| Regime aligned | `WeightRegime = 20` | BUY in BULLISH / SELL in BEARISH |
| Pullback-and-reclaim | `WeightPullback = 20` | Price dipped into EMA20/VWAP zone and reclaimed, or prev-close crossed EMA |
| RSI in zone | `WeightRsi = 15` | 30–70, rising for BUY (`RsiBuyFallback = 42`); 30–70, falling for SELL (`RsiSellFallback = 58`) |
| MACD histogram | `WeightMacd = 15` | Positive and rising/crossing for BUY; negative and falling/crossing for SELL |
| ADX trend strength | `WeightAdx = 10` | ADX14 ≥ `AdxTrendThreshold = 20` |
| Volume | `WeightVolume = **0**` | **Zero weight in DB** — volume check never contributes to score regardless of result |
| Spread bonus | `WeightSpread = 5` | Spread passes `MaxSpreadPct` gate |
| Body ratio | `WeightBody = 5` | Body ≥ `BodyRatioMin = 0.35` of candle range, in the signal direction |
| **Threshold to pass** | **66 / 66** | With volume at 0, requires regime + pullback + RSI + MACD as minimum combination (20+20+15+15=70) |

---

## Layer 2 — ML Gate (`SignalEngine.EvaluateWithMl`)

**Current `MlMode = SHADOW` (DB value `1`) — ML annotations are written but ML does NOT block any signal.**

If switched to `MlMode = ACTIVE` (value `2`), the following gates activate:

| # | Blocker | Parameter | Active DB Value | Notes |
|---|---------|-----------|----------------|-------|
| 2.1 | ML win probability — accuracy-first | `MlAccuracyFirstMinWinProbability` | `0.55` | Base gate when `MlAccuracyFirstMode = true` |
| 2.2 | ML weak context bump | `MlWeakContextMinWinProbabilityBump` | `0.10` | Gate becomes `0.65` in NEUTRAL regime, ADX < `AccuracyFirstLowAdxThreshold = 18`, or Asia session (22:00–04:00 UTC) |
| 2.3 | ML win probability — legacy | `MlMinWinProbability` | `0.70` | Only used when `MlAccuracyFirstMode = false` |
| 2.4 | ML blended confidence | `MlConfidenceBlendWeight = 0.5` | — | Rule score + ML score blended 50/50; result must exceed ML-recommended threshold |
| 2.5 | Dynamic threshold hardening | `MlDisableDynamicThresholdsWhenUnhealthy = true` | `true` | Prevents lowering the confidence bar when model is heuristic/unhealthy |

---

## Layer 3 — Operational Gates (`LiveTickProcessor`, post-engine)

| # | Blocker | Parameter | Active DB Value | Notes |
|---|---------|-----------|----------------|-------|
| 3.1 | Duplicate signal | *(hardcoded)* | — | Same bar + TF already in open signals → `RISK_BLOCKED` |
| 3.2 | Conflicting open direction | *(hardcoded)* | — | Opposite direction already open on same TF → `RISK_BLOCKED` |
| 3.3 | Global capacity | `MaxOpenPositions` | `2147483647` (unlimited) | Scoped capacity (`CheckScopedCapacity`) now used for scalp path |
| 3.4 | Per-timeframe capacity | `MaxOpenPerTimeframe` | `0` (disabled) | 0 = no per-TF limit |
| 3.5 | Per-direction capacity | `MaxOpenPerDirection` | `0` (disabled) | 0 = no per-direction limit |
| 3.6 | Daily drawdown | `DailyLossCapPercent` | `decimal.MaxValue` (unlimited) | Never fires at current value |
| 3.7 | Consecutive losses — HTF | `MaxConsecutiveLossesPerDay` | **`4` in DB** ⚠️ | Code default updated to `int.MaxValue`; live DB still has `4` — needs SQL update below |
| 3.8 | Consecutive losses — scalp | `ScalpMaxConsecutiveLossesPerDay` | not in DB yet | New field added to code; defaults to `int.MaxValue` until param set is regenerated |

---

## Layer 4 — Exit Engine (`ExitEngine.Compute`)

| # | Blocker | Parameter | Active DB Value | Notes |
|---|---------|-----------|----------------|-------|
| 4.1 | Spread too wide | `MaxSpreadPct` | `0.004` | Rechecked inside ExitEngine |
| 4.2 | ATR below minimum | `MinAtrThreshold` / `ScalpMinAtr` | `0.3` / `0.3` | Rechecked inside ExitEngine |
| 4.3 | Risk exceeds hard max | `RiskPerTradePercent > HardMaxRiskPercent` | `0.5% < 1.0%` | Never fires at current values |
| 4.4 | Stop too tight | *(hardcoded 0.15%)* | — | `stopDistance / entry < 0.0015` → rejected |
| 4.5 | Stop too wide | `ExitMaxStopDistancePct` | `0.05` (5%) | Stop wider than 5% of entry → rejected |
| 4.6 | Structure target too close | `MinRiskRewardAfterRounding` | `1.2` | Structure-based TP produces R:R < 1.2 AND ATR cannot support better → rejected |
| 4.7 | Final R:R too low | `MinRiskRewardAfterRounding` | `1.2` | After all regime/confidence adjustments, final TP must be ≥ 1.2R from SL |

---

## DB Values That Need Updating

| Field | DB Value | Recommended | SQL to fix |
|---|---|---|---|
| `maxConsecutiveLossesPerDay` | **`4`** | `2147483647` | See below |
| `confidenceBuyThreshold` | `66` | Operator choice (default: 45) | See below |
| `confidenceSellThreshold` | `66` | Operator choice (default: 45) | See below |
| `weightVolume` | **`0`** | `10` | Volume never scores; was zeroed by optimizer run |
| `scalpCooldownBars` | `15` | `5` | 15-min lockout between scalp signals is conservative |

### SQL Patches

```sql
-- Unlock consecutive loss limit (align with code default)
UPDATE "ETH".strategy_parameter_sets
SET parameters_json = parameters_json
    || '{"maxConsecutiveLossesPerDay": 2147483647}'::jsonb
WHERE status = 'Active';

-- Re-enable volume scoring weight (if desired)
UPDATE "ETH".strategy_parameter_sets
SET parameters_json = parameters_json
    || '{"weightVolume": 10}'::jsonb
WHERE status = 'Active';

-- Lower HTF confidence thresholds (if desired)
UPDATE "ETH".strategy_parameter_sets
SET parameters_json = parameters_json
    || '{"confidenceBuyThreshold": 55, "confidenceSellThreshold": 55}'::jsonb
WHERE status = 'Active';

-- Reduce scalp cooldown (if desired)
UPDATE "ETH".strategy_parameter_sets
SET parameters_json = parameters_json
    || '{"scalpCooldownBars": 5}'::jsonb
WHERE status = 'Active';
```

---

## Blocker Flow Summary (per signal candidate)

```
Tick arrives
  └── Layer 0: Gap? Regime stale? Candle mature? Warmup? Cooldown?
        └── Layer 1: SignalEngine — regime, ATR, spread, alignment, entry condition, score
              └── Layer 2: ML gate (SHADOW = pass-through; ACTIVE = win-prob + blended score)
                    └── Layer 3: Duplicate? Conflict? Capacity? Session limits?
                          └── Layer 4: ExitEngine — spread, ATR, stop width, R:R
                                └── SIGNAL PERSISTED
```
