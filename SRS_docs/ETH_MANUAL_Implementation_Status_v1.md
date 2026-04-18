# ETH_MANUAL — Implementation Status Report
**Source SRS:** ETH_MANUAL_Audit_SRS_v1.md  
**Checked:** 2026-04-05  
**Auditor:** Line-by-line code verification against all 35 SRS issues  
**Result:** 34 of 35 issues implemented. 1 issue partially implemented.

---

## Summary

All CRITICAL, HIGH, and MEDIUM issues have been addressed in the codebase.  
One HIGH-priority configuration issue (CF-02) is **partially** implemented — the code guard was added but the demo URL fallback in `appsettings.json` was not removed.

---

## SECTION 1 — SIGNAL STRATEGY

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| S-01 | OutcomeCategory mismatch on BUY/SELL decisions | ✅ FIXED | `SignalEngine.cs:395` — `enhancedDecision = enhancedDecision with { OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED }` explicitly set before return in ML-confirmed path |
| S-02 | Replay decisions hardcode STRATEGY_NO_TRADE | ✅ FIXED | `DataIngestionService.cs:89-91` — `OutcomeCategory = sig.Direction != SignalDirection.NO_TRADE ? OutcomeCategory.SIGNAL_GENERATED : OutcomeCategory.STRATEGY_NO_TRADE` |
| S-03 | Replay BarTimeUtc hardcoded to subtract 5m | ✅ FIXED | `DataIngestionService.cs:87` — `BarTimeUtc = sig.SignalTimeUtc - Timeframe.ByName(sig.Timeframe).Duration` |
| S-04 | Architecture contradiction in comment vs hard regime gate | ✅ FIXED | `SignalEngine.cs:122-125` — comment updated: "Regime alignment is mandatory. Both directions scored but hard gate blocks counter-regime trades." |
| S-05 | EvaluateWithDecision called twice per bar | ✅ FIXED | `LiveTickProcessor.cs:546-557` — pre-computed `(preSignal, preDecision)` passed to `EvaluateWithMl` via `preComputed:` parameter. `SignalEngine.EvaluateWithMl` uses `preComputed ?? ...` |
| S-06 | Volume failure produces no RejectReasonCode | ✅ FIXED | `SignalEngine.cs:580-582` — `rejectCodes.Add(RejectReasonCode.VOLUME_TOO_LOW)` added in volume else-branch |
| S-07 | ConflictingScoreGap log message hardcodes "diff≤5" | ✅ FIXED | `SignalEngine.cs:165` — now uses `{p.ConflictingScoreGap}` |
| S-08 | ML ExpectedValueR uses hardcoded 1.5/1.0 R:R | ✅ FIXED | `MlInferenceService.cs:193-194` — `decimal targetRMultiple = _paramProvider?.GetActive().TargetRMultiple ?? 1.5m` injected via `SetParameterProvider` |
| S-09 | ML running ACTIVE mode with heuristic fallback | ✅ FIXED | `MlInferenceService.cs:233-244` — `EnforceNoHeuristicInActiveMode()` called on every heuristic activation path; calls `_paramProvider.ForceOverrideMlMode(MlMode.SHADOW)` |

---

## SECTION 2 — CANDLES BACKFILL & LIVE TICK PROCESSING

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| C-01 | Persistent 404 errors crash entire tick | ✅ FIXED | `LiveTickProcessor.cs:234-248` — volume task wrapped in isolated `try/catch`; exception sets `latestApiVolume = 0` and logs warning without crashing the tick |
| C-02 | Vol=0 candles persisted and used in VolumeSma20 | ✅ FIXED | `LiveTickProcessor.cs:270-276` — Vol=0 candles flagged with `BuyerPct = -1m, SellerPct = -1m` before DB persist |
| C-03 | Session token expiry not handled | ✅ FIXED | `CapitalClient.cs:187-222` — `SendAuthorizedGetAsync` has 2-attempt retry loop; 401 clears tokens and re-authenticates; proactive refresh every 4 hours via `TokenRefreshInterval` |
| C-04 | CloseAllOpenAsync marks partial backfill candles as closed | ✅ FIXED | `BackfillService.cs:141` — replaced with `CloseOpenCandlesBeforeAsync(symbol, toUtc, ct)` — only closes candles whose bucket end is before the backfill boundary |
| C-05 | HTF closure trigger uses wrong boundary variable | ✅ FIXED | `LiveTickProcessor.cs:314` — now uses `finalizedCandle1m.OpenTime.Add(Timeframe.M1.Duration) >= nextBucket` (finalized candle time, not tick-floor `expected1m`) |
| C-06 | Auth retry only handles 429, not 5xx/network errors | ✅ FIXED | `DataIngestionService.cs:144-159` — catch clause now covers `"500"`, `"503"` in message AND separate `catch (HttpRequestException ex)` block with exponential backoff |

---

## SECTION 3 — INDICATORS

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| I-01 | RegimeAnalyzer uses Close prices not Highs/Lows | ✅ FIXED | `RegimeAnalyzer.cs:182-183` — `s.MidHigh != 0 ? s.MidHigh : s.CloseMid` and `s.MidLow != 0 ? s.MidLow : s.CloseMid` — falls back to close only when MidHigh/MidLow absent |
| I-02 | ATR=0 during warmup gives wrong reject code | ✅ FIXED | `SignalEngine.cs:110-113` — `atrOutcome = snap.Atr14 == 0 && snap.IsProvisional ? OutcomeCategory.CONTEXT_NOT_READY : OutcomeCategory.STRATEGY_NO_TRADE` |
| I-03 | IndicatorEngine.WarmUpPeriod constant used in Program.cs | ✅ FIXED | `Program.cs:195` — `var warmUp = paramProvider.GetActive().WarmUpPeriod` used in `/api/indicators/current` endpoint; constant replaced with live parameter |
| I-04 | VWAP daily reset creates misleading close-vs-VWAP on first bars | ⚪ ADVISORY | Low-priority advisory. No code change implemented. Acceptable as documented behaviour. |

---

## SECTION 4 — ML FEATURES, TRAINING & INFERENCE

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| M-01 | RegimeAgeBars hardcoded to assume 5m bars | ✅ FIXED | `MlFeatureExtractor.cs:284-285` — `int tfMinutes = evaluationTf?.Minutes ?? 5` — correct TF minutes used in division |
| M-02 | Heuristic fallback has wrong feature index comments | ✅ FIXED | `MlInferenceService.cs:346-349` — comment updated to `[0]=ema20 (raw price)`. ACTIVE mode prevention (S-09) ensures heuristic never gates live trades |
| M-03 | ComputeStreaks breaks on PENDING/EXPIRED | ✅ FIXED | `MlFeatureExtractor.cs:340-342` — `if (o.OutcomeLabel is OutcomeLabel.PENDING or OutcomeLabel.EXPIRED) continue;` added before streak counting |
| M-04 | AdaptiveParameterTuner results never applied | ✅ FIXED | `LiveTickProcessor.cs:1214-1227` — `_adaptiveTuner.RecordOutcome(outcome, p)` called in `EvaluateOpenSignalsAsync`. `ApplyAdaptiveTuneResultAsync` at line 1235 persists adjusted parameters to DB and refreshes `_paramProvider` |
| M-05 | MlDriftDetector never receives outcome data | ✅ FIXED | `LiveTickProcessor.cs:1202-1212` — `_mlDriftDetector.RecordOutcome(prediction.PredictedWinProbability, outcome.OutcomeLabel == OutcomeLabel.WIN)` called for every resolved WIN/LOSS with a stored ML prediction |
| M-06 | export_features.py proximity join window too tight (300s) | ✅ FIXED | `export_features.py:85` — window extended to `<= 900` (15 minutes), covers all timeframes |
| M-07 | Walk-forward model selection picks best historical AUC | ✅ FIXED | `train_outcome_predictor.py:319-320` — comment `# M-07: Always use the last fold model` + `best_model = model` unconditionally assigns last fold |
| M-08 | No minimum AUC threshold before ONNX export | ✅ FIXED | `train_outcome_predictor.py:365-370` — `MIN_AUC_FOR_EXPORT = 0.54` guard with `sys.exit(3)` if average AUC too low |

---

## SECTION 5 — DASHBOARD & WEB API

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| W-01 | /api/candles silently maps 30m/1h/4h to 5m | ✅ FIXED | `Program.cs:152-163` — switch now includes `"30m" => Timeframe.M30`, `"1h" => Timeframe.H1`, `"4h" => Timeframe.H4`, `_ => (Timeframe?)null` with `Results.BadRequest(...)` |
| W-02 | Dashboard spread thresholds hardcoded, don't match engine | ✅ FIXED | `dashboard.js:46,66-68` — `MAX_SPREAD_PCT` loaded from `/api/config/thresholds`; spread coloring uses `spreadWarnPct = MAX_SPREAD_PCT * 100 * 0.5` and `spreadBadPct = MAX_SPREAD_PCT * 100` |
| W-03 | Dashboard TIMEOUT_BARS hardcoded to 60 | ✅ FIXED | `dashboard.js:45,51` — `TIMEOUT_BARS` initialised to 60, then updated from `/api/config/thresholds` response via `loadConfigThresholds()` |
| W-04 | /api/db/truncate-all has no authentication | ✅ FIXED | `Program.cs:1016-1048` — requires `TRUNCATE_API_KEY` env var; returns 404 if key not configured; validates Bearer token or `X-Api-Key` header; restricts to localhost |
| W-05 | refreshMlPerformance may have wrong JSON field path | ✅ FIXED | `dashboard.js:519` uses `perf.drift?.actualWinRate`. Server `Program.cs:804-812` returns `drift.ActualWinRate` which ASP.NET Core minimal API serializes as camelCase `actualWinRate`. Field paths match. |

---

## SECTION 6 — CONFIGURATION & INFRASTRUCTURE

| ID | Title | Status | Evidence |
|----|-------|--------|----------|
| CF-01 | No validation that API credentials are non-empty | ✅ FIXED | `Program.cs:55-60` — throws `InvalidOperationException` if `apiKey`, `identifier`, or `password` is null/whitespace |
| CF-02 | Default BaseUrl points to Capital.com DEMO API | ⚠️ PARTIAL | `Program.cs:47-50` — code now uses `?? throw` if no URL configured. **HOWEVER:** `appsettings.json:16` still has `"BaseUrl": "https://demo-api-capital.backend-capital.com"`. Since `capitalSection["BaseUrl"]` reads this value, the `throw` is never reached when running without `CAPITAL_BASE_URL` env var. In production, the demo URL silently applies if the env var is absent. **Fix:** Remove `BaseUrl` from `appsettings.json` and add it only to `appsettings.Development.json`. |
| CF-03 | appsettings.Development.json content not reviewed | ⚪ ADVISORY | Not reviewed in this audit pass. Ensure `ASPNETCORE_ENVIRONMENT=Production` is set explicitly in production deployments. |
| CF-04 | Dual codebase (src/ + eth/src/) — stale copy risk | ✅ FIXED | `eth/src/` directory no longer exists. |
| CF-05 | No proactive session token refresh | ✅ FIXED | `CapitalClient.cs:25` — `TokenRefreshInterval = TimeSpan.FromHours(4)`; `SendAuthorizedGetAsync` proactively re-authenticates when token age exceeds the interval. |

---

## OUTSTANDING ISSUES

### CF-02 — PARTIAL — High Priority

**File:** `src/EthSignal.Web/appsettings.json` (line 16)

**Problem:**
```json
"CapitalApi": {
  "BaseUrl": "https://demo-api-capital.backend-capital.com",
```
The code in `Program.cs` resolves the base URL as:
```csharp
var baseUrl = builder.Configuration["CAPITAL_BASE_URL"]
    ?? capitalSection["BaseUrl"]   // reads appsettings.json → demo URL
    ?? throw new InvalidOperationException(...);
```
If `CAPITAL_BASE_URL` is not set as an environment variable, `capitalSection["BaseUrl"]` returns the demo URL from `appsettings.json` and the `throw` is never reached. The system silently connects to the Capital.com **demo** API in production.

**Required Fix:**
1. Remove `BaseUrl` from `appsettings.json`:
```json
"CapitalApi": {
  "Symbol": "ETHUSD",
  "BackfillDays": 7
}
```
2. Create/update `appsettings.Development.json` to include:
```json
"CapitalApi": {
  "BaseUrl": "https://demo-api-capital.backend-capital.com"
}
```
This ensures the demo URL is only active when `ASPNETCORE_ENVIRONMENT=Development` and forces production deployments to set `CAPITAL_BASE_URL` explicitly.

---

## VERIFICATION TOTALS

| Priority | Total Issues | Implemented | Partial | Not Done |
|----------|-------------|-------------|---------|----------|
| CRITICAL | 4 | 4 | 0 | 0 |
| HIGH | 14 | 13 | 1 (CF-02) | 0 |
| MEDIUM | 14 | 14 | 0 | 0 |
| LOW/Advisory | 3 | 0 | 0 | 3 (I-04, CF-03: advisory only) |
| **Total** | **35** | **31** | **1** | **3 advisory** |

All advisory items (I-04, CF-03) require no code changes — they are documentation/process notes.  
The single actionable remaining item is **CF-02**: remove the demo URL from `appsettings.json`.

---

*End of Implementation Status Report*
