================================================================================
  ETH_MANUAL — Technical Audit SRS
  Scope  : Signal Strategy · Candles Backfill · Indicators · ML Pipeline · Dashboard
  Date   : 2026-04-05
  Status : FOR IMPLEMENTATION
================================================================================

TABLE OF CONTENTS
  1. Signal Strategy
  2. Candles Backfill & Live Tick Processing
  3. Indicators
  4. ML Features, Training & Inference
  5. Dashboard & Web API
  6. Configuration & Infrastructure
  7. Priority Summary

================================================================================
SECTION 1 — SIGNAL STRATEGY
================================================================================

ISSUE S-01 [BUG / CRITICAL]
Title   : OutcomeCategory mismatch — BUY/SELL decisions persisted as STRATEGY_NO_TRADE
File    : src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs  (line 537–543)
         src/EthSignal.Infrastructure/Engine/SignalEngine.cs         (line 285–390)
Evidence: Log 2026-04-05: "[1h] SignalDecision ... decision="BUY" outcome="STRATEGY_NO_TRADE" score=85"
          followed immediately by "[1h] Signal generated: "BUY" @ 2062.595"
Description:
  When a signal is generated (Direction = BUY or SELL) and the ML gate passes in
  ACTIVE mode, the returned SignalDecision record has OutcomeCategory =
  STRATEGY_NO_TRADE instead of SIGNAL_GENERATED. The log confirms both fields co-exist
  in the same object. This means:
    - The decision_audit table records all real generated signals as "no trades"
    - Dashboard stats (generated count vs no-trade count) are wrong
    - The /api/decisions/summary endpoint under-counts SIGNAL_GENERATED
    - The "top reject reasons" panel incorrectly includes legitimate entries
Root Cause:
  In EvaluateWithMl (SignalEngine.cs), when ML mode is ACTIVE and the signal passes
  both ML gates, the return path is "return (rec, enhancedDecision)". The enhancedDecision
  is built via "dec with { MlPrediction=..., BlendedConfidence=..., EffectiveThreshold=... }".
  The "dec" itself comes from the inner call to EvaluateWithDecision, which should set
  OutcomeCategory = SIGNAL_GENERATED. However, the first (pre-signal) call to
  EvaluateWithDecision is discarded ("var (preSignal, _) = ..."), and the second call
  inside EvaluateWithMl re-runs evaluation. A subtle difference between the two evaluation
  contexts (e.g., timing of DateTimeOffset.UtcNow or parameter snapshot) may produce a
  different OutcomeCategory, or the "dec" record type "with" expression isn't correctly
  propagating SIGNAL_GENERATED before ML annotation is applied.
Fix:
  In EvaluateWithMl, after confirming that ML passes and the signal is valid, explicitly
  enforce: "enhancedDecision = enhancedDecision with { OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED }"
  before returning. Also add an assertion/unit test that ensures DecisionType == BUY/SELL
  implies OutcomeCategory == SIGNAL_GENERATED.
  Remove the duplicate pre-signal EvaluateWithDecision call (see S-05).

------------------------------------------------------------------------

ISSUE S-02 [BUG / HIGH]
Title   : Replay signal decisions hardcode OutcomeCategory.STRATEGY_NO_TRADE for all outcomes
File    : src/EthSignal.Web/BackgroundServices/DataIngestionService.cs  (line 89)
Description:
  When BackfillReplaySignals is enabled, all replay signals are inserted into the
  decision_audit table with a hardcoded OutcomeCategory = STRATEGY_NO_TRADE, regardless
  of the signal's direction (BUY/SELL).
  Code: "OutcomeCategory = OutcomeCategory.STRATEGY_NO_TRADE,"
  Generated BUY/SELL signals from historical replay are labelled identically to genuine
  no-trade decisions, poisoning decision audit statistics.
Fix:
  Set OutcomeCategory = sig.Direction != SignalDirection.NO_TRADE
      ? OutcomeCategory.SIGNAL_GENERATED
      : OutcomeCategory.STRATEGY_NO_TRADE

------------------------------------------------------------------------

ISSUE S-03 [BUG / HIGH]
Title   : Replay BarTimeUtc hardcoded to subtract 5 minutes, wrong for HTF signals
File    : src/EthSignal.Web/BackgroundServices/DataIngestionService.cs  (line 87)
Description:
  Code: "BarTimeUtc = sig.SignalTimeUtc.AddMinutes(-5)"
  This assumes all replay signals are on 5m timeframe. For 15m, 30m, 1h, 4h signals,
  the bar time is off by up to 235 minutes (for 4h). The BarTimeUtc in the audit table
  is used for deduplication (ExistsForBarAsync) and timeline reconstruction.
Fix:
  Parse the signal timeframe and subtract the correct duration:
  BarTimeUtc = sig.SignalTimeUtc - Timeframe.Parse(sig.Timeframe).Duration

------------------------------------------------------------------------

ISSUE S-04 [LOGICAL / HIGH]
Title   : Architecture contradiction — "both directions evaluated" but regime hard-gate always blocks non-aligned direction
File    : src/EthSignal.Infrastructure/Engine/SignalEngine.cs  (lines 119–207)
Description:
  The comment at line 119-120 states: "Regime gives a score bonus when aligned, not a
  hard lock on direction. This lets the strategy short during a BULLISH regime if all
  lower-TF indicators point down."
  However, lines 188-207 implement a hard regime alignment gate that BLOCKS counter-regime
  signals unconditionally (RejectReasonCode.REGIME_BULLISH_REQUIRED / REGIME_BEARISH_REQUIRED).
  The comment describes a bidirectional system; the code implements a unidirectional one.
  The dual-direction ScoreDirection computation (wasted CPU) produces a "chosen" direction
  that is then immediately hard-blocked if counter-regime. The comment creates false
  expectations for anyone reading the code.
Fix:
  Either: (a) Update the comment to reflect the actual policy (regime alignment is mandatory),
  or (b) Remove the hard gate and implement the "bonus only" model as described, making the
  counter-regime block configurable via StrategyParameters (e.g., RequireRegimeAlignment = true).

------------------------------------------------------------------------

ISSUE S-05 [PERFORMANCE / MEDIUM]
Title   : EvaluateWithDecision called twice per bar in TryGenerateSignal (duplicate computation)
File    : src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs  (lines 521–531)
Description:
  TryGenerateSignal calls EvaluateWithDecision for a "pre-signal" pass to extract direction
  for ML feature building, then immediately calls EvaluateWithMl which internally calls
  EvaluateWithDecision again with identical inputs. The first call's decision "(_)" is
  discarded. This doubles the signal engine computation on every bar evaluation.
Fix:
  Pass the pre-signal result directly into a modified EvaluateWithMl signature, or fold
  both passes into one. Alternatively, extract feature building as a standalone step that
  takes direction/score as inputs without re-running the full engine.

------------------------------------------------------------------------

ISSUE S-06 [BUG / MEDIUM]
Title   : Volume scoring failure produces no RejectReasonCode in audit trail
File    : src/EthSignal.Infrastructure/Engine/SignalEngine.cs  (lines 566–572)
Description:
  All scoring conditions (RSI, MACD, ADX, Pullback, Body) add a RejectReasonCode when
  they fail. The volume check (lines 566-572) increments the score on pass but adds
  nothing to rejectCodes on failure. Volume failures are invisible in the decision audit
  table's reason_codes column and the dashboard's top-reject-reasons panel.
Fix:
  Add: "else { rejectCodes.Add(RejectReasonCode.VOLUME_TOO_LOW); reasons.Add(...); }"
  after the volume check's positive branch.
  RejectReasonCode.VOLUME_TOO_LOW must also be added to the enum if not present.

------------------------------------------------------------------------

ISSUE S-07 [BUG / MEDIUM]
Title   : ConflictingScoreGap log message hardcodes "diff≤5" instead of reading the parameter
File    : src/EthSignal.Infrastructure/Engine/SignalEngine.cs  (line 161)
Description:
  Code: "reasons.Add($"Ambiguous: BUY={buyTotal} vs SELL={sellTotal} (diff≤5, both above threshold) → NO_TRADE")"
  The log message hardcodes "diff≤5" but the actual check uses p.ConflictingScoreGap.
  If the parameter is changed from 3 (default), the message is misleading.
Fix:
  Change to: "$"Ambiguous: BUY={buyTotal} vs SELL={sellTotal} (diff≤{p.ConflictingScoreGap}) → NO_TRADE""

------------------------------------------------------------------------

ISSUE S-08 [BUG / MEDIUM]
Title   : ML ExpectedValueR uses hardcoded R:R (1.5 win / 1.0 loss) ignoring StrategyParameters.TargetRMultiple
File    : src/EthSignal.Infrastructure/Engine/ML/MlInferenceService.cs  (line 189)
Description:
  Code: "decimal expectedValueR = (decimal)winProb * 1.5m - (1m - (decimal)winProb) * 1.0m;"
  This hardcodes reward = 1.5R and risk = 1.0R. The actual TargetRMultiple defaults to 2.0
  and StopAtrMultiplier to 1.5. The EV calculation is systematically incorrect and will
  mislead any future logic that uses ExpectedValueR for filtering.
Fix:
  Pass StrategyParameters into Predict() (or inject the R:R values) and compute:
  "expectedValueR = (decimal)winProb * p.TargetRMultiple - (1m - (decimal)winProb) * 1.0m"

------------------------------------------------------------------------

ISSUE S-09 [CRITICAL / OPERATIONAL]
Title   : ML runs in ACTIVE mode with only a heuristic fallback (no real trained model)
File    : logs, MlInferenceService.cs, StrategyParameters.cs
Description:
  The production logs show: "ModelVersion=heuristic-v1" with "Mode=ACTIVE". This means
  the system is making live trade-gating decisions based on the heuristic fallback function
  in CreateFallbackInference(), which is a hand-crafted scoring rule that was explicitly
  designed for SHADOW (annotation-only) mode before a real model is trained.
  The heuristic was designed to produce "plausible shadow predictions", not to make live
  gating decisions. A real ONNX model must be trained and registered before running ACTIVE mode.
Fix:
  Add a startup guard in MlInferenceService.LoadActiveModelAsync: if the loaded model is
  the heuristic sentinel AND MlMode = ACTIVE in parameters, either:
    (a) Downgrade to SHADOW mode with a warning, or
    (b) Refuse to start and throw an exception
  Ensure train_pipeline.sh is run and a real model is registered before enabling ACTIVE mode.

================================================================================
SECTION 2 — CANDLES BACKFILL & LIVE TICK PROCESSING
================================================================================

ISSUE C-01 [BUG / CRITICAL]
Title   : Persistent 404 errors on volume refresh — every 15 ticks, full tick fails
File    : src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs  (lines 213–232)
         src/EthSignal.Infrastructure/Apis/CapitalClient.cs          (lines 96–120)
Evidence: Log 2026-04-05: "Request failed (404) for 'prices/ETHUSD?resolution=MINUTE&from=...'"
          repeating continuously every ~15 seconds.
Description:
  Every VolumeRefreshEveryTicks=15 ticks, a GetCandlesAsync call is made inside the main
  tick loop with a 2-minute recent time range to get current-bar volume. The Capital.com
  demo API returns 404 for this "prices" endpoint on very recent intraday ranges. The 404
  is thrown as an InvalidOperationException which is caught by the outer tick error handler,
  crashing the ENTIRE tick. This causes:
    1. The 1m open candle is not updated (loses the tick)
    2. The "1m closed" log shows Vol=0 for the affected candle
    3. 1m candles with Vol=0 are persisted to the DB and used in VolumeSma20
    4. The error repeats every 15 seconds indefinitely
Fix:
  (a) Wrap the volume task separately and catch 404 gracefully:
      "if (volumeTask != null) { try { ... } catch { latestApiVolume = 0; } }"
  (b) Better: move volume tracking to accumulate from tick data instead of
      querying the API for candles mid-tick. Track volume as a field on _openCandle1m
      and update it from the spot price metadata if available.
  (c) Add a circuit-breaker: if volume refresh fails N consecutive times, disable it
      for MinutesBetweenRetry minutes.

------------------------------------------------------------------------

ISSUE C-02 [BUG / HIGH]
Title   : 1m candles with Vol=0 inserted into DB, biasing VolumeSma20 downward
File    : src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs  (lines 265–277)
Evidence: Log: "1m closed: ... Vol=0" on consecutive candles
Description:
  When the volume API call fails (see C-01), the fallback is "_openCandle1m.Volume" which
  was never updated from API data that session. The candle closes with Vol=0. These
  zero-volume candles are:
    1. Persisted to candles_1m
    2. Used to aggregate 5m/15m candles (aggregated volume will be lower than real)
    3. Included in VolumeSma20 calculations, pushing the moving average down
    4. Can cause volume gate failures in SignalEngine when real bars have normal volume
Fix:
  Before persisting a closed 1m candle, check if Volume == 0 and flag it as suspect.
  Consider using the PREVIOUS bar's volume as a fallback rather than 0.
  Alternatively, mark Vol=0 candles in the database (e.g., set buyer_pct=seller_pct=-1)
  so they can be excluded from SMA calculations.

------------------------------------------------------------------------

ISSUE C-03 [BUG / HIGH]
Title   : Session token expiry not handled — API calls will fail after token TTL
File    : src/EthSignal.Infrastructure/Apis/CapitalClient.cs
Description:
  Capital.com CST/X-SECURITY-TOKEN tokens expire (typically ~6 hours). After expiry, all
  API calls return 401. The CapitalClient never retries authentication on 401 responses.
  SendAuthorizedGetAsync throws an InvalidOperationException that is caught by the tick
  error handler, which logs it and continues. This means:
    - After token expiry, ALL tick data (spot price) fails
    - The live processor continues running but no data is updated
    - The system silently stops processing real market data with no alert
Fix:
  In SendAuthorizedGetAsync, detect 401 responses and re-authenticate:
    if (response.StatusCode == 401) {
        await AuthenticateAsync(ct);
        // retry the original request once
    }
  Also add a staleness alarm: if LatestSpot hasn't been updated for >120 seconds,
  the health endpoint should return "status: error" not "status: stale".

------------------------------------------------------------------------

ISSUE C-04 [BUG / MEDIUM]
Title   : CloseAllOpenAsync at end of backfill marks partial open candles as closed
File    : src/EthSignal.Infrastructure/Engine/BackfillService.cs  (line 140)
Description:
  After backfill completes, "await _repo.CloseAllOpenAsync(symbol, ct)" closes all
  open candles regardless of whether they have complete data. The live tick processor
  warm-start then reads these candles as fully closed historical data. For HTF candles
  (15m, 1h, 4h) that were open at the time of backfill start, they may have less than
  full 1m-bar coverage but are now flagged as IsClosed=true.
Fix:
  Remove CloseAllOpenAsync from backfill. The live tick processor already starts a fresh
  1m open candle from the current tick. Alternatively, only close candles whose bucket
  end time is before the backfill 'toUtc' boundary.

------------------------------------------------------------------------

ISSUE C-05 [BUG / MEDIUM]
Title   : Higher-TF candle pipeline only checks nextBucket boundary, not whether 1m closed inside that bucket
File    : src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs  (lines 283–337)
Description:
  The HTF candle closure check uses "expected1m >= nextBucket" to detect when a new TF
  bucket starts. However, "expected1m" is the floor of the CURRENT tick time (after
  closing the previous 1m), not the time of the JUST-CLOSED 1m candle. If a tick arrives
  a few seconds after the 1m boundary (e.g., due to network delay), expected1m could skip
  ahead by one minute. This causes the closure trigger to fire one 1m bar "late", meaning
  the closed HTF candle may be built from one fewer 1m bar than expected.
Fix:
  Use "finalizedCandle1m.OpenTime.Add(Timeframe.M1.Duration) >= nextBucket" to detect
  the bucket transition, not "expected1m >= nextBucket".

------------------------------------------------------------------------

ISSUE C-06 [MISSING / MEDIUM]
Title   : No authentication retry on startup if Capital.com returns 503 (service unavailable)
File    : src/EthSignal.Web/BackgroundServices/DataIngestionService.cs  (lines 128–149)
Description:
  AuthenticateWithRetryAsync catches 429 (rate-limit) but not 500/503 (server errors),
  connection timeouts, or DNS failures. If Capital.com is temporarily unavailable at
  startup, the service fails immediately after maxRetries attempts, each 10s apart
  (max 80s). The process then crashes and does not restart unless the hosting environment
  restarts it.
Fix:
  Extend the retry logic to cover transient HTTP errors (5xx) and network exceptions
  (HttpRequestException). Use exponential backoff for these as well.
  Consider treating startup authentication failure as a recoverable condition and retrying
  indefinitely with a longer maximum delay (e.g., 5 minutes).

================================================================================
SECTION 3 — INDICATORS
================================================================================

ISSUE I-01 [BUG / HIGH]
Title   : RegimeAnalyzer market structure uses Close prices, not actual Highs/Lows
File    : src/EthSignal.Infrastructure/Engine/RegimeAnalyzer.cs  (lines 174–210)
Description:
  EvaluateMarketStructure builds its swing-high/swing-low list from CloseMid prices
  (via IndicatorSnapshot.CloseMid), not from actual candle High/Low. Market structure
  (Higher Highs, Higher Lows for bull; Lower Highs, Lower Lows for bear) is defined by
  OHLC extremes, not closes. Using closes means:
    - Spiky rejections (long wicks) don't create swing highs/lows
    - A HH/HL pattern may not be detected correctly in volatile candles
    - The regime signal is less sensitive to real price structure
Fix:
  IndicatorSnapshot should carry MidHigh and MidLow (already in RichCandle but not in
  IndicatorSnapshot). Add them to the snapshot model, populate them in IndicatorEngine,
  and use them in EvaluateMarketStructure for swing detection.

------------------------------------------------------------------------

ISSUE I-02 [BUG / MEDIUM]
Title   : AtrCalculator initial value starts at index 'period', indices 1–(period-1) are 0
File    : src/EthSignal.Infrastructure/Engine/Indicators/AtrCalculator.cs  (lines 31–34)
Description:
  The initial ATR sum runs from i=1 to i=period, storing the first ATR at result[period].
  result[0] through result[period-1] remain 0. The WarmUpPeriod=50 gate in IndicatorEngine
  prevents these from being used in signal generation (IsProvisional=true). However:
    - IndicatorEngine.ComputeLatest called from the API (/api/indicators/current) may
      return the last snapshot even if insufficient candles exist and WarmUpPeriod < period.
    - Any direct ATR lookup at indices < 14 produces 0.
    - ATR=0 triggers the MinAtrThreshold block in SignalEngine, producing spurious NO_TRADE
      decisions during warmup instead of CONTEXT_NOT_READY.
Fix:
  The WarmUpPeriod guard covers this in practice. Document explicitly that ATR[0..13] = 0.
  Add a guard in SignalEngine: if ATR==0 AND snap.IsProvisional, set outcome =
  CONTEXT_NOT_READY rather than STRATEGY_NO_TRADE (different reject reason code).

------------------------------------------------------------------------

ISSUE I-03 [BUG / MEDIUM]
Title   : IndicatorEngine.WarmUpPeriod constant (50) diverges from configured p.WarmUpPeriod
File    : src/EthSignal.Infrastructure/Engine/IndicatorEngine.cs  (line 15)
         src/EthSignal.Web/Program.cs  (line 186)
Description:
  Program.cs requests "IndicatorEngine.WarmUpPeriod + 10" (= 60) closed candles from the
  repository for the /api/indicators/current endpoint. If a DB parameter set is active with
  WarmUpPeriod = 100 (e.g., for extended EMA periods), only 60 candles are fetched and
  indicators are computed with insufficient history, producing wrong (IsProvisional) values
  that are returned to the dashboard as if valid.
  The constant is marked "Legacy — use StrategyParameters.WarmUpPeriod instead" but is
  still used in production code.
Fix:
  In Program.cs (and anywhere the constant is used), replace "IndicatorEngine.WarmUpPeriod"
  with "paramProvider.GetActive().WarmUpPeriod".

------------------------------------------------------------------------

ISSUE I-04 [LOGICAL / LOW]
Title   : VWAP resets at UTC midnight — first bar of each day starts with VWAP = single bar close
File    : src/EthSignal.Infrastructure/Engine/Indicators/VwapCalculator.cs  (lines 30–42)
Description:
  VWAP resets daily at 00:00 UTC. The first bar's VWAP = (H+L+C)/3 of that one bar.
  The RegimeAnalyzer and SignalEngine compare CloseMid vs Vwap as a scored condition.
  For the first ~5 bars of each UTC day, VWAP is nearly equal to Close (very small
  cumulative volume), making the "Close vs VWAP" condition almost always pass for the
  first few bars regardless of actual price position.
  This is standard behavior for exchange-level VWAP but should be noted.
Fix (advisory):
  Consider adding a minimum cumulative volume threshold before the VWAP comparison is
  scored. E.g., skip VWAP scoring when cumVol < p.VolumeSma20 * 5 (i.e., fewer than
  5 bars of average volume accumulated for the day).

================================================================================
SECTION 4 — ML FEATURES, TRAINING & INFERENCE
================================================================================

ISSUE M-01 [BUG / HIGH]
Title   : MlFeatureExtractor.ComputeRegimeAgeBars always divides by 5 (hardcoded 5m assumption)
File    : src/EthSignal.Infrastructure/Engine/ML/MlFeatureExtractor.cs  (line 284)
Description:
  Code: "return (int)((latestTime - regimeTime).TotalMinutes / 5);"
  This assumes all evaluations use 5m bars. For 1h signals, a regime from 12 bars ago
  (12 hours) returns RegimeAgeBars = 144 instead of 12. For 4h signals, the error is
  48×. The RegimeAgeBars feature is a key input to the ML model and directly affects
  whether the model treats a regime as "fresh" or "stale". An inflated value will
  cause the model to treat all HTF signal evaluations as using very stale regimes.
Fix:
  Pass the evaluation Timeframe into MlFeatureExtractor.Extract() and use the correct
  TF period for the division:
  "return (int)((latestTime - regimeTime).TotalMinutes / tf.Minutes);"

------------------------------------------------------------------------

ISSUE M-02 [BUG / HIGH]
Title   : Heuristic fallback uses wrong feature indices — ema20 price used as ruleScore proxy
File    : src/EthSignal.Infrastructure/Engine/ML/MlInferenceService.cs  (lines 323–332)
Description:
  CreateFallbackInference() comment says:
    "[0]=ema20_distance_pct, [1]=ema_cross_gap_pct, [2]=rsi14, [3]=macd_hist, [4]=adx14 ... [35]=rule_based_score"
  But MlFeatureVector.ToFloatArray() actual order (FeatureVector.cs lines 83–109) is:
    [0]=ema20 (absolute price ~1800-2100), [1]=ema50, [2]=rsi14, [3]=macd_hist, [4]=adx14 ... [35]=rule_based_score
  Index [0] is the raw EMA20 value (~2062), not a percentage. The heuristic reads
  "float ruleScore = features.Length > 35 ? features[35] / 100f : 0.5f" which is correct
  for rule_based_score. But the heuristic is never actually using index [0] for
  ema20_distance_pct — it only uses indices 2, 3, 4, and 35.
  The comment is misleading. With MlMode=ACTIVE, the heuristic IS making real gating
  decisions and it does so purely from ruleScore, adx, macd, and rsi. No pullback depth,
  regime, or lookback features are used, making it a poor substitute for a real model.
Fix:
  This is acceptable ONLY in SHADOW mode for annotation. Fix per S-09: prevent ACTIVE
  mode from using the heuristic. If the heuristic must be used, update it to use
  the correct derived percentage features (indices 25-31 are ema-distance-pct,
  vwap-distance-pct, etc.) for a more meaningful heuristic.

------------------------------------------------------------------------

ISSUE M-03 [BUG / HIGH]
Title   : ComputeStreaks breaks on first PENDING/EXPIRED outcome instead of skipping it
File    : src/EthSignal.Infrastructure/Engine/ML/MlFeatureExtractor.cs  (lines 334–351)
Description:
  The streak calculation iterates recentOutcomes (newest first). The loop contains:
    "if (o.OutcomeLabel == WIN) { if (losses > 0) break; wins++; }"
    "else if (o.OutcomeLabel == LOSS) { if (wins > 0) break; losses++; }"
  PENDING and EXPIRED outcomes are not WIN or LOSS, so they fall through both conditions
  without incrementing or breaking. However the LOGIC is:
    "break when direction changes" — this is fine for PENDING/EXPIRED since they pass
    through silently. BUT if the most recent outcome is PENDING and the one before is WIN,
    the function correctly counts 0 wins (because PENDING is neither WIN nor LOSS).
  The actual issue: if outcomes = [PENDING, WIN, WIN, LOSS, ...], consecutiveWins = 0
  because the PENDING outcome is seen first and neither condition triggers. The function
  SHOULD skip PENDING/EXPIRED and start counting from the first resolved outcome.
Fix:
  Add "else if (o.OutcomeLabel is OutcomeLabel.PENDING or OutcomeLabel.EXPIRED) continue;"
  before the break conditions to skip unresolved outcomes.

------------------------------------------------------------------------

ISSUE M-04 [BUG / MEDIUM]
Title   : AdaptiveParameterTuner results are never applied — dead code path
File    : src/EthSignal.Infrastructure/Engine/ML/AdaptiveParameterTuner.cs
         src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs
Description:
  AdaptiveParameterTuner is injected in Program.cs (line 84) and registered as a singleton.
  However, no code in LiveTickProcessor.cs (or any other runtime class) calls
  _adaptiveParameterTuner.RecordOutcome(...). The tuner computes adjustments but they are
  never persisted to the DB nor fed back to _paramProvider. The adaptive tuning feature
  is entirely non-functional in the current codebase.
Fix:
  In EvaluateOpenSignalsAsync (or the outcome-evaluation path), after an outcome is resolved:
    var tuneResult = _adaptiveTuner.RecordOutcome(outcome, _paramProvider.GetActive());
    if (tuneResult?.Action == TuneAction.Adjust && tuneResult.AdjustedParameters != null)
        await _paramRepo.SaveAdaptiveAdjustmentAsync(tuneResult.AdjustedParameters, ...);
  Also wire TuneAction.Revert to load the previous parameter version from DB.

------------------------------------------------------------------------

ISSUE M-05 [BUG / MEDIUM]
Title   : MlDriftDetector never receives outcome data — drift detection is permanently disabled
File    : src/EthSignal.Infrastructure/Engine/ML/MlDriftDetector.cs
         src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs
Description:
  MlDriftDetector is injected as a singleton in Program.cs (line 83). Its RecordOutcome()
  method is never called from LiveTickProcessor.cs. The _rollingPredictions queue always
  has 0 entries, so CheckDrift() always returns DriftDetected=false (because count < 30).
  The drift metrics shown on the dashboard (AUC, Brier) are always 0/null.
  The health endpoint misleadingly shows "NO DRIFT" when it has no data to detect drift from.
Fix:
  In EvaluateOpenSignalsAsync, after resolving an outcome for a signal that had an ML
  prediction:
    _mlDriftDetector.RecordOutcome(mlPrediction.PredictedWinProbability, outcome.TpHit);
  Link this via the prediction stored on the signal (requires fetching the stored prediction
  or carrying it in memory alongside the signal).

------------------------------------------------------------------------

ISSUE M-06 [MEDIUM / TRAINING]
Title   : export_features.py proximity join uses 5-minute window, too tight for HTF signals
File    : ml/export_features.py  (lines 84–86)
Description:
  The proximity fallback query uses:
    "ABS(EXTRACT(EPOCH FROM (s2.signal_time_utc - f.created_at_utc))) <= 300"
  This 300-second (5-minute) window is appropriate for 5m signals but too tight for
  15m, 30m, 1h, and 4h signals where the feature snapshot may be created several minutes
  before the signal is committed to the DB. Training data for HTF signals may be silently
  dropped when signal_id is null on older feature rows.
Fix:
  Extend the proximity window to 900 seconds (15 minutes) to cover all timeframes.
  Alternatively, use a per-timeframe window:
    "EXTRACT(EPOCH FROM ...) <= CASE timeframe WHEN '1h' THEN 3600 WHEN '4h' THEN 14400 ELSE 300 END"

------------------------------------------------------------------------

ISSUE M-07 [MEDIUM / TRAINING]
Title   : Walk-forward model selection uses highest AUC across all folds, not most-recent fold
File    : ml/train_outcome_predictor.py  (lines 316–319)
Description:
  Code: "if metrics['auc_roc'] > best_auc: best_auc = metrics['auc_roc']; best_model = model"
  Walk-forward cross-validation's purpose is to test generalization to FUTURE unseen data.
  The "best" model for deployment should be the one trained on the most recent training
  window (last fold), not the one with the highest historical AUC. Selecting the highest
  AUC across all folds biases toward older market regimes.
Fix:
  In production, always use the model from the LAST fold (trained on the most recent data).
  Keep best_auc-based selection only for metric reporting, not model selection:
    best_model = model  # always use last fold
    if metrics['auc_roc'] > best_auc: best_auc = metrics['auc_roc']  # only for display

------------------------------------------------------------------------

ISSUE M-08 [MISSING / MEDIUM]
Title   : No minimum AUC threshold before ONNX export and model registration
File    : ml/train_outcome_predictor.py  (lines 374–403)
         ml/register_model.py
Description:
  A model with AUC = 0.50 (random) is exported and registered without any quality gate.
  With datasets of 200+ samples (the minimum), a poorly-converged model may achieve AUC
  close to random. After registration, this model is loaded in ACTIVE mode and gates
  live trades. An AUC < 0.52 (drift threshold from StrategyParameters) indicates the
  model is no worse than random and should not be used in ACTIVE mode.
Fix:
  In train_outcome_predictor.py, before export:
    "if avg_auc < 0.54: print('WARNING: AUC too low for deployment'); sys.exit(3)"
  register_model.py should check avg_auc_roc from metadata and refuse registration below threshold.

================================================================================
SECTION 5 — DASHBOARD & WEB API
================================================================================

ISSUE W-01 [BUG / HIGH]
Title   : /api/candles endpoint silently maps 30m, 1h, 4h to 5m
File    : src/EthSignal.Web/Program.cs  (lines 143–150)
Description:
  The switch statement:
    "1m" => Timeframe.M1
    "15m" => Timeframe.M15
    _ => Timeframe.M5   // catch-all
  Routes any unrecognized timeframe (including "30m", "1h", "4h") to 5m candles.
  A request to /api/candles?timeframe=4h returns the last 200 five-minute candles
  instead of 4-hour candles. The dashboard currently only queries 5m and 15m, so this
  is not visible today, but it is a latent correctness bug in the public API.
Fix:
  Add all supported timeframes to the switch:
    "30m" => Timeframe.M30
    "1h" => Timeframe.H1
    "4h" => Timeframe.H4
  Return a 400 Bad Request for unrecognized timeframe strings.

------------------------------------------------------------------------

ISSUE W-02 [BUG / MEDIUM]
Title   : Dashboard spread thresholds hardcoded in JS don't match StrategyParameters.MaxSpreadPct
File    : src/EthSignal.Web/wwwroot/js/dashboard.js  (lines 57–58)
Description:
  The spread color coding uses hardcoded 0.1% (ok) / 0.3% (warn) JavaScript thresholds:
    "spreadPct < 0.1 ? 'spread-ok' : spreadPct < 0.3 ? 'spread-warn' : 'spread-bad'"
  StrategyParameters.MaxSpreadPct defaults to 0.004 (0.4%). The spread gate in
  SignalEngine blocks at 0.4%, but the dashboard turns red at 0.3%. The dashboard will
  show "spread-bad" while the engine is still accepting signals, causing confusion.
Fix:
  Expose MaxSpreadPct via an /api/config/thresholds endpoint and update the JS to use
  the server-side value rather than hardcoding it.

------------------------------------------------------------------------

ISSUE W-03 [BUG / MEDIUM]
Title   : Dashboard TIMEOUT_BARS hardcoded to 60, not from server parameters
File    : src/EthSignal.Web/wwwroot/js/dashboard.js  (line 45)
Description:
  "const TIMEOUT_BARS = 60;" is hardcoded. If StrategyParameters.OutcomeTimeoutBars
  is changed in the DB (e.g., to 30 or 120), the expiry countdown timers in the
  signal history table will show incorrect remaining times.
Fix:
  Include OutcomeTimeoutBars in the /api/config or /api/performance/summary response
  and read it in JavaScript on initialization.

------------------------------------------------------------------------

ISSUE W-04 [SECURITY / HIGH]
Title   : /api/db/truncate-all endpoint has no authentication — accessible to anyone with network access
File    : src/EthSignal.Web/Program.cs  (implied by dashboard.js lines 455–460)
Description:
  The truncate endpoint only requires an HTTP header "X-Confirm-Truncate: YES", which
  any code can supply. There is no:
    - Authentication/authorization check
    - API key requirement
    - CSRF token
    - IP allowlist
  If the dashboard is accessible on a LAN or is accidentally exposed to the internet,
  any client can wipe the entire database with a single HTTP POST.
Fix:
  Add a configurable API key or bearer token requirement to the truncate endpoint.
  Restrict it to 127.0.0.1/::1 in production via middleware or a configurable flag.
  Alternatively, remove it from production builds and only keep it for development.

------------------------------------------------------------------------

ISSUE W-05 [BUG / LOW]
Title   : refreshMlPerformance accesses perf.drift.actualWinRate — field path may not match server response
File    : src/EthSignal.Web/wwwroot/js/dashboard.js  (lines 505–507)
Description:
  Code: "perf.drift?.actualWinRate" and "perf.drift?.predictedMeanWin"
  This assumes the /api/ml/performance endpoint returns an object with a .drift sub-object.
  The actual endpoint structure needs to be verified. If the JSON path is wrong,
  ml-actual-wr and ml-pred-wr always display "--".
Fix:
  Verify the /api/ml/performance endpoint JSON structure and update the JS path accordingly.
  Consider adding a schema contract test that validates the shape of API responses.

=============================================================================================
SECTION 6 — CONFIGURATION & INFRASTRUCTURE
================================================================================

ISSUE CF-01 [CRITICAL / SECURITY]
Title   : No startup validation that API credentials are non-empty
File    : src/EthSignal.Web/Program.cs  (lines 50–52)
Description:
  Code: "var apiKey = ... ?? "";"
  If CAPITAL_API_KEY environment variable is not set, apiKey = "" and the CapitalClient
  is constructed silently. The first authentication attempt will fail with a 401 error,
  which the retry logic interprets as "not a 429" and doesn't retry. The app starts but
  immediately fails authentication on first run with a confusing error.
Fix:
  Add startup validation:
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("CAPITAL_API_KEY environment variable is required");
  Same for identifier and password.

------------------------------------------------------------------------

ISSUE CF-02 [HIGH]
Title   : Default BaseUrl points to Capital.com DEMO API — production may silently use demo
File    : src/EthSignal.Web/Program.cs  (line 48)
         src/EthSignal.Web/appsettings.json  (line 16)
Description:
  Both the code default and appsettings.json point to:
    "https://demo-api-capital.backend-capital.com"
  If CAPITAL_BASE_URL is not set in the production environment, the system connects to the
  demo API, producing demo-quality market data with no price integrity. This could go
  undetected if the ETH price happens to be in a similar range.
Fix:
  Remove the hardcoded demo URL default from Program.cs. If CAPITAL_BASE_URL is not set,
  throw a startup error rather than defaulting to demo. Mark the demo URL in appsettings.json
  as for development only (move to appsettings.Development.json).

------------------------------------------------------------------------

ISSUE CF-03 [MEDIUM]
Title   : appsettings.Development.json content unknown — may override production values silently
File    : src/EthSignal.Web/appsettings.Development.json
Description:
  This file exists but was not reviewed in this audit. In .NET, appsettings.Development.json
  is automatically loaded when ASPNETCORE_ENVIRONMENT=Development. If this environment
  variable is accidentally set in a production shell, development overrides apply silently.
Fix:
  Review appsettings.Development.json and ensure it only contains safe development-specific
  values (local DB connection, demo API URL). Ensure ASPNETCORE_ENVIRONMENT=Production
  is set explicitly in production deployment scripts.

------------------------------------------------------------------------

ISSUE CF-04 [MEDIUM]
Title   : Dual codebase — src/ and eth/src/ both exist with potentially different code versions
File    : /Users/mohammadabwini/Desktop/ETH_MANUAL/src/  (active)
         /Users/mohammadabwini/Desktop/ETH_MANUAL/eth/src/  (older copy)
Description:
  The project contains two copies of the full C# source tree: the root src/ folder and
  eth/src/. The ETH_MANUAL.sln solution file (root) references src/. However, the eth/src/
  folder also contains a complete copy of all source files. If a developer edits eth/src/
  thinking it is the active codebase, changes are silently ignored by the build.
  The ml/ folder also has a duplicate: both ml/ and eth/ml/ exist.
Fix:
  Delete eth/src/ and eth/ml/ (they are stale copies from an earlier version archive).
  Keep only the versions referenced by ETH_MANUAL.sln.

------------------------------------------------------------------------

ISSUE CF-05 [LOW]
Title   : No token refresh for Capital.com API — hardcoded session approach only
File    : src/EthSignal.Infrastructure/Apis/CapitalClient.cs
Description:
  (Supplements C-03) The Capital.com session tokens (CST / X-SECURITY-TOKEN) are valid
  for a limited period. The CapitalClient has no background job or periodic re-auth.
  The SemaphoreSlim _authLock protects concurrent auth but is never used proactively.
Fix:
  Add a periodic re-authentication timer or detect 401 errors in SendAuthorizedGetAsync
  as described in C-03.

=============================================================================================
SECTION 7 — PRIORITY SUMMARY
================================================================================

CRITICAL (Fix before next live run)
  S-09  ML running ACTIVE mode with heuristic fallback — fake model making live decisions
  C-01  Persistent 404 volume errors crashing every 15 ticks — Vol=0 data corrupting DB
  C-03  Session token expiry not handled — live data stops silently after a few hours
  W-04  /api/db/truncate-all has no authentication

HIGH (Fix within 1–2 development sessions)
  S-01  OutcomeCategory mismatch — BUY signals stored as STRATEGY_NO_TRADE in audit
  S-02  Replay decisions hardcode STRATEGY_NO_TRADE for generated signals
  S-03  Replay BarTimeUtc hardcoded to subtract 5m regardless of TF
  S-08  ML ExpectedValueR uses hardcoded 1.5/1.0 R:R
  C-02  Vol=0 candles stored and used in VolumeSma20
  I-01  Regime market structure uses closes not highs/lows
  I-03  IndicatorEngine.WarmUpPeriod constant not replaced with live parameter
  M-01  RegimeAgeBars hardcoded to assume 5m bars — inflated 12-48x for HTF signals
  M-02  Heuristic fallback uses wrong feature indices (misleading comments)
  M-05  MlDriftDetector never fed data — drift detection disabled
  W-01  /api/candles silently maps 30m/1h/4h to 5m
  CF-01 No validation that API credentials are non-empty at startup
  CF-02 Default BaseUrl is demo API

MEDIUM (Fix within next sprint)
  S-04  Architecture contradiction: "both directions" comment vs hard regime gate
  S-05  EvaluateWithDecision called twice per bar (redundant)
  S-06  Volume scoring failure produces no RejectReasonCode
  S-07  ConflictingScoreGap log message hardcodes "diff≤5"
  C-04  CloseAllOpenAsync closes partial candles at end of backfill
  C-05  HTF candle closure trigger uses wrong boundary variable
  C-06  Auth retry only handles 429, not 5xx / network errors
  I-02  ATR warmup produces 0 values; wrong reject code during warmup
  M-03  ComputeStreaks breaks on PENDING/EXPIRED instead of skipping
  M-04  AdaptiveParameterTuner results never applied (dead code)
  M-06  Proximity join window (5m) too tight for HTF signal matching
  M-07  Walk-forward model selection picks best historical AUC, not most recent
  M-08  No minimum AUC threshold before model registration
  W-02  Dashboard spread thresholds hardcoded, don't match engine parameters
  W-03  Dashboard TIMEOUT_BARS hardcoded to 60
  W-05  refreshMlPerformance may have wrong JSON field path
  CF-03 appsettings.Development.json not reviewed — may override production
  CF-04 Dual codebase (src/ + eth/src/) — stale copy risk

LOW (Advisory)
  I-04  VWAP daily reset creates misleading close-vs-VWAP signal at midnight bars
  CF-05 No proactive session token refresh

================================================================================
END OF DOCUMENT
================================================================================
