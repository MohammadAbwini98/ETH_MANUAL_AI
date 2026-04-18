# Market-Context Feature Expansion — Implementation Report

**Date:** 2026-04-12
**Feature Version:** v1.0 -> v2.0
**Features Added:** 21 new features (59 -> 80 total)

---

## 1. Summary of Changes

Added 21 new market-context features across 4 new categories (E, F, G, H) to the ML pipeline, end-to-end across C# feature extraction, Python training scripts, and tests.

### Category E: Market Structure (7 features) — ACTIVE
| Feature | Description |
|---------|-------------|
| `session_range_position_pct` | Where price sits within current session range [0..1] |
| `distance_to_prior_day_high_pct` | (close - priorDayHigh) / close |
| `distance_to_prior_day_low_pct` | (close - priorDayLow) / close |
| `distance_to_session_vwap_pct` | (close - sessionVwap) / close |
| `range_position_pct` | Where close sits in 20-bar high/low range [0..1] |
| `distance_to_20_bar_high_pct` | (close - 20barHigh) / close |
| `distance_to_20_bar_low_pct` | (close - 20barLow) / close |

### Category F: Volatility Regime (6 features) — ACTIVE
| Feature | Description |
|---------|-------------|
| `realized_vol_15m` | Realized vol (std of log returns) over ~3 bars |
| `realized_vol_1h` | Realized vol over ~12 bars |
| `realized_vol_4h` | Realized vol over ~48 bars |
| `volatility_compression_flag` | 1 if short-vol < 0.7 * long-vol |
| `volatility_expansion_flag` | 1 if short-vol > 1.5 * long-vol |
| `atr_percentile_rank` | ATR percentile within rolling 50-bar window [0..1] |

### Category G: Signal Saturation (5 features) — ACTIVE
| Feature | Description |
|---------|-------------|
| `signals_last_10_bars` | Total signals in last 10 bars |
| `same_direction_signals_last_10` | Same-direction signals in last 10 bars |
| `opposite_direction_signals_last_10` | Opposite-direction signals in last 10 bars |
| `recent_stop_out_count` | Stop-outs in last 10 resolved outcomes |
| `recent_false_breakout_rate` | Fraction of recent losses that hit SL |

### Category H: BTC Cross-Asset Context (3 features) — SCAFFOLDED
| Feature | Description |
|---------|-------------|
| `btc_recent_return` | BTC close-to-close return (0 when unavailable) |
| `btc_regime_label` | BTC regime: 0=NEUTRAL, 1=BULLISH, 2=BEARISH (0 when unavailable) |
| `eth_btc_relative_strength` | ETH return minus BTC return (0 when unavailable) |

**Status:** BTC features default to 0 because the repo does not currently ingest BTC data. The `IBtcContextProvider` interface and `NullBtcContextProvider` are ready for when BTC data becomes available.

### Derivatives Context — SCAFFOLDED (no features added yet)
The `DerivativesContext` model and `IDerivativesContextProvider` interface are scaffolded for future use when funding rate, open interest, and related data sources become available. No dummy features are emitted.

---

## 2. Files Touched

### C# — Domain Models
- `src/EthSignal.Domain/Models/MlFeatureVector.cs` — Added 21 new properties, updated `ToFeatureMap()`, `FeatureNames` (59 -> 80)
- `src/EthSignal.Domain/Models/BtcCrossAssetContext.cs` — **NEW** — BTC context record
- `src/EthSignal.Domain/Models/DerivativesContext.cs` — **NEW** — Derivatives context record (scaffold)

### C# — Infrastructure
- `src/EthSignal.Infrastructure/Engine/ML/MlFeatureExtractor.cs` — Added calculations for all 21 features, bumped version to v2.0, new helper methods
- `src/EthSignal.Infrastructure/Engine/ML/IBtcContextProvider.cs` — **NEW** — Interface + null provider
- `src/EthSignal.Infrastructure/Engine/ML/IDerivativesContextProvider.cs` — **NEW** — Interface + null provider

### Python — ML Pipeline
- `ml/train_outcome_predictor.py` — Updated `FEATURE_NAMES` (59 -> 80)
- `ml/train_recalibrator.py` — Updated `FEATURE_NAMES` (59 -> 80)
- `ml/export_features.py` — Updated default `--feature-version` to v2.0

### Tests
- `tests/EthSignal.Tests/Engine/ML/MlFeatureExtractorTests.cs` — Updated count assertions (59 -> 80), added 12 new test methods covering all new categories

### Documentation
- `Notes/Market-Context-Feature-Expansion-Plan.md` — **NEW** — Full implementation plan
- `Notes/Market-Context-Feature-Expansion-Report.md` — **NEW** — This report

---

## 3. Tests / Checks Run

| Test Suite | Result |
|-----------|--------|
| MlFeatureExtractor tests (44 total) | **ALL PASSED** |
| Full test suite (315 total) | **314 passed, 1 pre-existing failure** |

The single pre-existing failure (`LoadActiveModelAsync_Applies_Calibration_Artifact`) is an environmental issue requiring a specific ONNX model file on disk — unrelated to these changes.

### New Tests Added
- `Extract_Produces_80_Features_In_FloatArray` — Updated from 59 to 80
- `Feature_Names_Count_Matches_FloatArray` — Updated from 59 to 80
- `Extract_FeatureVersion_IsV2` — Updated from v1.0 to v2.0
- `Extract_RangePosition_Within_0_1` — Range position is bounded [0,1]
- `Extract_DistanceTo20BarHigh_IsNonPositive` — Close <= high
- `Extract_SessionRangePosition_Defaults_WhenEmpty` — Graceful with no snaps
- `Extract_RealizedVolatility_PositiveWithData` — Vol >= 0
- `Extract_VolatilityFlags_Exclusive` — Can't be both compressed and expanded
- `Extract_AtrPercentileRank_Between_0_And_1` — Bounded [0,1]
- `Extract_SignalSaturation_CountsCorrectly` — Correct directional counting
- `Extract_SignalSaturation_ZeroWithNoSignals` — Safe with null/empty signals
- `Extract_StopOutStats_CountsCorrectly` — Stop-out and false-breakout rate
- `Extract_BtcContext_UsedWhenProvided` — BTC context passthrough
- `Extract_BtcContext_DefaultsToZeroWhenNull` — Safe when no BTC data
- `OldModelsStillWorkWithNewFeatures_MissingFeaturesDefaultToZero` — Backward compat

---

## 4. Backward Compatibility

### Runtime Safety
- **Existing call sites unchanged:** The `Extract()` method's new parameters (`recentSignals`, `btcContext`) are optional with `null` defaults. `LiveTickProcessor.cs` compiles and runs without modification.
- **Old models still work:** `ToFloatArray(orderedFeatureNames)` uses the model's stored feature list, not the new global list. Old models trained on 59 features will receive a 59-element array. New features default to 0 via `TryGetValue` fallback.
- **Feature version v1.0 data preserved:** Old `ml_feature_snapshots` rows with `feature_version='v1.0'` still export correctly. The Python training scripts fill missing features with 0.

### Training Pipeline
- New exports use `--feature-version v2.0` by default
- Old exports can still be used with `--feature-version v1.0`
- The Python scripts fill missing features with 0, so mixed v1.0/v2.0 data trains safely

---

## 5. Remaining Risks / Follow-up Items

| Risk | Severity | Mitigation |
|------|----------|-----------|
| BTC features are all zeros until BTC data ingestion is wired | Low | Features default to 0; models trained on this data will learn to ignore them. Wire `IBtcContextProvider` when BTC data is available. |
| Derivatives features are not emitted yet | Low | Clean scaffolding is ready. Wire `IDerivativesContextProvider` when funding rate / OI data is available. |
| Signal saturation features require passing `recentSignals` to Extract | Medium | Currently null in LiveTickProcessor — wire when signal history query is added to the ML pipeline path. Features default to 0 until then. |
| Feature version bump v1.0 -> v2.0 means new snapshots use v2.0 | Low | Old v1.0 rows still export fine. Retrain models on v2.0 data to use new features. |
| Session structure features use available snapshot history, not full session candles | Low | Approximation is reasonable for 5m bars with 20-snap lookback. Improves with larger snapshot buffer. |

### Recommended Next Steps
1. **Wire signal history** into `LiveTickProcessor` ML pipeline to populate signal saturation features
2. **Register `IBtcContextProvider`** in DI when BTC candle ingestion is added
3. **Retrain models** on v2.0 data to leverage new features
4. **Monitor feature importance** after retraining to validate which new features add value

---

## 6. Rollback Plan

### Quick Rollback (revert feature version, keep code)
1. Change `MlFeatureExtractor.FeatureVersion` back to `"v1.0"`
2. Change `ml/export_features.py` default `--feature-version` back to `"v1.0"`
3. Existing v1.0 models continue to work unchanged (they only read 59 features)

### Full Rollback (revert all code changes)
Revert these files to their pre-change state:

**C# files to revert:**
```
src/EthSignal.Domain/Models/MlFeatureVector.cs
src/EthSignal.Infrastructure/Engine/ML/MlFeatureExtractor.cs
tests/EthSignal.Tests/Engine/ML/MlFeatureExtractorTests.cs
```

**C# files to delete (new):**
```
src/EthSignal.Domain/Models/BtcCrossAssetContext.cs
src/EthSignal.Domain/Models/DerivativesContext.cs
src/EthSignal.Infrastructure/Engine/ML/IBtcContextProvider.cs
src/EthSignal.Infrastructure/Engine/ML/IDerivativesContextProvider.cs
```

**Python files to revert:**
```
ml/train_outcome_predictor.py
ml/train_recalibrator.py
ml/export_features.py
```

### Database Considerations
- No schema changes were made
- Feature snapshots saved with `feature_version='v2.0'` contain the 21 new features in `features_json`
- These rows are harmless to old models (extra JSON keys are ignored)
- To fully clean up, you could delete v2.0 rows: `DELETE FROM "ETH".ml_feature_snapshots WHERE feature_version = 'v2.0'`
- This is optional — old models simply ignore the extra fields

### Model Compatibility
- Old ONNX models trained on v1.0 features continue to work without changes
- The `ToFloatArray(orderedFeatureNames)` method uses the model's stored feature list
- No need to retrain existing models unless you want to leverage new features
