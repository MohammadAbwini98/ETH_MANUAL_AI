using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine.ML;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Multi-timeframe Entry Signal Engine.
/// Generates BUY/SELL/NO_TRADE on any timeframe based on regime + pullback-and-reclaim model.
///
/// Entry model:
///   - Signal is confirmed on candle CLOSE of the evaluation timeframe
///   - SignalTimeUtc = candle close time (open + timeframe duration)
///   - EntryPrice is set by RiskManager based on close price
///
/// Evaluates on: 5m, 15m, 30m, 1h, 4h — each closed candle triggers independent evaluation.
/// TF-1: Produces SignalDecision structured results for audit/persistence.
/// TF-3: Neutral regime handling is policy-driven via NeutralRegimePolicy.
/// </summary>
public static class SignalEngine
{
    /// <summary>Strategy version — bump when changing thresholds.</summary>
    public const string StrategyVersion = "v3.1";

    /// <summary>Evaluate using default parameters (backward compat).</summary>
    public static SignalRecommendation Evaluate(
        string symbol, RegimeResult regime, IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap, RichCandle candle)
        => Evaluate(symbol, regime, snap, prevSnap, candle, StrategyParameters.Default);

    /// <summary>Evaluate using the given parameter set (backward compat).</summary>
    public static SignalRecommendation Evaluate(
        string symbol, RegimeResult regime, IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap, RichCandle candle, StrategyParameters p)
    {
        var (recommendation, _) = EvaluateWithDecision(symbol, regime, snap, prevSnap, candle, p);
        return recommendation;
    }

    /// <summary>
    /// Full evaluation returning both SignalRecommendation and SignalDecision.
    /// Timeframe-aware: evaluates on any TF (5m, 15m, 30m, 1h, 4h).
    /// </summary>
    public static (SignalRecommendation Recommendation, SignalDecision Decision) EvaluateWithDecision(
        string symbol, RegimeResult regime, IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap, RichCandle candle, StrategyParameters p,
        SourceMode sourceMode = SourceMode.LIVE, string? parameterSetId = null,
        Timeframe? evaluationTf = null)
    {
        var tf = evaluationTf ?? Timeframe.M5;
        var tfName = tf.Name;
        p = p.ResolveForTimeframe(tfName);
        // Read all thresholds from parameters
        int wRegime = p.WeightRegime;
        int wPullback = p.WeightPullback;
        int wRsi = p.WeightRsi;
        int wMacd = p.WeightMacd;
        int wAdx = p.WeightAdx;
        int wVolume = p.WeightVolume;
        int wSpread = p.WeightSpread;
        int wBody = p.WeightBody;
        decimal spreadMaxPct = p.MaxSpreadPct;
        decimal volumeRatioThreshold = p.VolumeMultiplierMin;
        decimal bodyRatioThreshold = p.BodyRatioMin;
        decimal pullbackZonePct = p.PullbackZonePct;

        var reasons = new List<string>();
        var reasonCodes = new List<RejectReasonCode>();
        // Signal time = candle close time (open + TF duration)
        var signalTime = snap.CandleOpenTimeUtc.Add(tf.Duration);
        var now = DateTimeOffset.UtcNow;

        // Build indicator snapshot dict for persistence (DB-4)
        var indicatorDict = BuildIndicatorSnapshot(snap, candle);

        // ─── CONTEXT CHECK ────────────────────────────────
        // TF-4: Check for missing higher-timeframe context
        if (regime == null!)
        {
            reasonCodes.Add(RejectReasonCode.MISSING_HTF_CONTEXT);
            reasons.Add("Missing regime context → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, Regime.NEUTRAL, 0, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.CONTEXT_NOT_READY, null, null,
                sourceMode, parameterSetId, 0);
            return (rec, dec);
        }

        // ─── PROVISIONAL INDICATOR GATE ─────────────────
        // Block signals when indicators are computed on incomplete history (< WarmUpPeriod bars).
        // Provisional snapshots have unreliable RSI, MACD, EMA, ATR values — emitting a signal
        // on them will produce a corrupted confidence score and likely a losing trade.
        // Note: ATR==0 with IsProvisional is already caught by the ATR gate below as CONTEXT_NOT_READY;
        // this gate covers all other provisional cases (ATR>0 but other indicators still warming up).
        if (snap.IsProvisional)
        {
            reasonCodes.Add(RejectReasonCode.MISSING_HTF_CONTEXT);
            reasons.Add($"Provisional snapshot (insufficient indicator history for {tfName}) → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, regime.Regime, 0, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.CONTEXT_NOT_READY, regime.Regime,
                regime.CandleOpenTimeUtc, sourceMode, parameterSetId, 0);
            return (rec, dec);
        }

        // ─── NEUTRAL REGIME POLICY (TF-3 / FR-7) ────────
        if (regime.Regime == Regime.NEUTRAL)
        {
            if (p.NeutralRegimePolicy == NeutralRegimePolicy.BlockAllEntriesInNeutral)
            {
                reasonCodes.Add(RejectReasonCode.REGIME_NEUTRAL);
                reasons.Add("Regime=NEUTRAL → NO_TRADE (policy: BlockAllEntriesInNeutral)");
                var rec = NoTrade(symbol, signalTime, regime.Regime, 0, reasons, tfName);
                var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                    indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                    regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, 0);
                return (rec, dec);
            }
            reasons.Add($"Regime=NEUTRAL (policy: {p.NeutralRegimePolicy} — proceeding with evaluation)");
        }

        // ─── Minimum ATR gate (mandatory) ────────────────────────────────────
        // In near-zero-volatility / choppy markets, ATR is tiny and the TP cannot
        // be reached before noise hits the SL.  Block signals when ATR is below
        // the configured minimum so the engine stays flat in flat markets.
        // 1m timeframe uses ScalpMinAtr (smaller) because 1m ATR is naturally
        // an order of magnitude smaller than HTF ATR; using the HTF threshold
        // here would reject virtually every 1m evaluation.
        decimal minAtrForTf = tf == Timeframe.M1 ? p.ScalpMinAtr : p.MinAtrThreshold;
        if (snap.Atr14 < minAtrForTf)
        {
            // I-02: If ATR is 0 during warmup, use CONTEXT_NOT_READY rather than STRATEGY_NO_TRADE
            var atrOutcome = snap.Atr14 == 0 && snap.IsProvisional
                ? OutcomeCategory.CONTEXT_NOT_READY
                : OutcomeCategory.STRATEGY_NO_TRADE;
            reasonCodes.Add(RejectReasonCode.ATR_TOO_LOW);
            reasons.Add($"ATR({snap.Atr14:F3}) < MinAtr({minAtrForTf}) for {tfName} → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, regime.Regime, 0, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, atrOutcome,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, 0);
            return (rec, dec);
        }

        // ─── Evaluate BOTH directions independently ─────────────
        // Both directions are scored, but a hard regime alignment gate (below) blocks
        // counter-regime trades. The dual scoring identifies the stronger direction
        // and detects ambiguous (conflicting) conditions. Regime alignment is mandatory.
        var buyResult = ScoreDirection(true, regime, snap, prevSnap, candle, p,
            wRegime, wPullback, wRsi, wMacd, wAdx, wVolume, wBody, pullbackZonePct,
            volumeRatioThreshold, bodyRatioThreshold);
        var sellResult = ScoreDirection(false, regime, snap, prevSnap, candle, p,
            wRegime, wPullback, wRsi, wMacd, wAdx, wVolume, wBody, pullbackZonePct,
            volumeRatioThreshold, bodyRatioThreshold);

        // ─── Spread quality (mandatory gate — direction-independent) ────
        decimal spreadPct = snap.CloseMid != 0 ? snap.Spread / snap.CloseMid : 1m;
        bool spreadPassed = spreadPct < spreadMaxPct;
        int spreadBonus = 0;
        if (spreadPassed)
        {
            spreadBonus = wSpread;
            reasons.Add($"Spread({spreadPct:P3}) ok (+{wSpread})");
        }
        else
        {
            reasonCodes.Add(RejectReasonCode.SPREAD_TOO_WIDE);
            reasons.Add($"Spread({spreadPct:P3}) too wide (mandatory fail)");
            var rec = NoTrade(symbol, signalTime, regime.Regime, 0, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, 0);
            return (rec, dec);
        }

        // Pick the direction with the higher score (spread bonus added to both equally)
        int buyTotal = buyResult.Score + spreadBonus;
        int sellTotal = sellResult.Score + spreadBonus;

        // Conflict filter: if both directions are within ConflictingScoreGap points AND both exceed
        // threshold, the market is ambiguous — emit NO_TRADE rather than an arbitrary flip.
        int dirThresholdBuy = p.ConfidenceBuyThreshold;
        int dirThresholdSell = p.ConfidenceSellThreshold;
        if (buyTotal >= dirThresholdBuy && sellTotal >= dirThresholdSell
            && Math.Abs(buyTotal - sellTotal) <= p.ConflictingScoreGap)
        {
            reasonCodes.Add(RejectReasonCode.CONFLICTING_SIGNALS);
            reasons.Add($"Ambiguous: BUY={buyTotal} vs SELL={sellTotal} (diff≤{p.ConflictingScoreGap}, both above threshold) → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, regime.Regime, 0, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, 0);
            return (rec, dec);
        }

        bool isBuy;
        DirectionScore chosen;
        DirectionScore other;
        if (buyTotal > sellTotal)
        {
            isBuy = true;
            chosen = buyResult;
            other = sellResult;
        }
        else
        {
            isBuy = false;
            chosen = sellResult;
            other = buyResult;
        }

        int score = chosen.Score + spreadBonus;
        int dirThreshold = isBuy ? p.ConfidenceBuyThreshold : p.ConfidenceSellThreshold;

        // Regime alignment hard gate: block counter-regime trades
        bool chosenAlignedWithRegime =
            (isBuy && regime.Regime == Regime.BULLISH) ||
            (!isBuy && regime.Regime == Regime.BEARISH);

        if (!chosenAlignedWithRegime)
        {
            reasonCodes.Add(isBuy
                ? RejectReasonCode.REGIME_BULLISH_REQUIRED
                : RejectReasonCode.REGIME_BEARISH_REQUIRED);

            reasons.AddRange(chosen.Reasons);
            reasons.Add($"Chosen direction {(isBuy ? "BUY" : "SELL")} is not aligned with regime {regime.Regime} → NO_TRADE");

            var rec = NoTrade(symbol, signalTime, regime.Regime, score, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, score);
            return (rec, dec);
        }

        // Merge chosen direction's reasons into the main list
        reasons.AddRange(chosen.Reasons);
        reasonCodes.AddRange(chosen.RejectCodes);
        if (other.Score > 0)
            reasons.Add($"(Other direction scored {other.Score + spreadBonus})");

        // Pullback is mandatory; at least one momentum confirmation (RSI or MACD) must also pass
        if (!chosen.HasEntryCondition)
        {
            reasonCodes.Add(RejectReasonCode.NO_ENTRY_CONDITION);
            reasons.Add("No entry condition met (need pullback AND RSI or MACD) → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, regime.Regime, score, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, score);
            return (rec, dec);
        }

        // Check score threshold
        if (score < dirThreshold)
        {
            reasonCodes.Add(RejectReasonCode.SCORE_BELOW_THRESHOLD);
            reasons.Add($"Score {score} < {dirThreshold} → NO_TRADE");
            var rec = NoTrade(symbol, signalTime, regime.Regime, score, reasons, tfName);
            var dec = BuildDecision(rec, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
                indicatorDict, OutcomeCategory.STRATEGY_NO_TRADE,
                regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, score);
            return (rec, dec);
        }

        reasons.Add($"Score={score} → {(isBuy ? "BUY" : "SELL")} [{tfName}]");

        var signal = new SignalRecommendation
        {
            Symbol = symbol,
            Timeframe = tfName,
            SignalTimeUtc = signalTime,
            Direction = isBuy ? SignalDirection.BUY : SignalDirection.SELL,
            EntryPrice = 0m,
            ConfidenceScore = score,
            Regime = regime.Regime,
            StrategyVersion = p.StrategyVersion,
            Reasons = reasons
        };

        var decision = BuildDecision(signal, now, snap.CandleOpenTimeUtc, reasonCodes, reasons,
            indicatorDict, OutcomeCategory.SIGNAL_GENERATED,
            regime.Regime, regime.CandleOpenTimeUtc, sourceMode, parameterSetId, score);

        return (signal, decision);
    }

    /// <summary>
    /// ML-enhanced evaluation. Runs the rule-based engine first, then applies ML overlay.
    /// When MlMode=DISABLED or inference unavailable, behaves identically to EvaluateWithDecision.
    /// </summary>
    public static (SignalRecommendation Recommendation, SignalDecision Decision) EvaluateWithMl(
        string symbol, RegimeResult regime, IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap, RichCandle candle, StrategyParameters p,
        MlPrediction? mlPrediction, SignalFrequencyManager? frequencyManager,
        SourceMode sourceMode = SourceMode.LIVE, string? parameterSetId = null,
        Timeframe? evaluationTf = null,
        (SignalRecommendation Rec, SignalDecision Dec)? preComputed = null)
    {
        p = p.ResolveForTimeframe((evaluationTf ?? Timeframe.M5).Name);
        // Step 1-5: Use pre-computed result if available, otherwise run the full rule-based engine
        var (rec, dec) = preComputed ?? EvaluateWithDecision(symbol, regime, snap, prevSnap, candle, p,
            sourceMode, parameterSetId, evaluationTf);

        // If ML is disabled or no prediction, return rule-based result unchanged
        if (p.MlMode == MlMode.DISABLED || mlPrediction == null)
            return (rec, dec);

        // Step 6: ML Enhancement
        int ruleScore = dec.ConfidenceScore;
        int blendedConfidence = ComputeBlendedConfidence(ruleScore, mlPrediction, p);
        int effectiveThreshold = ComputeEffectiveThreshold(rec, mlPrediction, frequencyManager, regime, snap, p);

        // Attach ML data to decision
        var enhancedDecision = dec with
        {
            MlPrediction = mlPrediction,
            BlendedConfidence = blendedConfidence,
            EffectiveThreshold = effectiveThreshold
        };

        // Step 7: Apply ML-enhanced decision logic
        if (p.MlMode == MlMode.SHADOW)
        {
            // Shadow mode: log ML prediction but use original rule-based decision unchanged
            return (rec, enhancedDecision);
        }

        // ACTIVE mode
        if (rec.Direction == SignalDirection.NO_TRADE)
        {
            // Check if ML can rescue a blocked signal
            // Case 1: NEUTRAL regime blocking — check if ML-gated entry is allowed
            if (dec.ReasonCodes.Contains(RejectReasonCode.REGIME_NEUTRAL)
                && frequencyManager?.ShouldAllowNeutralEntry(mlPrediction, regime, p) == true)
            {
                // ML allows this neutral entry — re-evaluate without neutral blocking
                var altParams = p with { NeutralRegimePolicy = NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral };
                var (altRec, altDec) = EvaluateWithDecision(symbol, regime, snap, prevSnap, candle, altParams,
                    sourceMode, parameterSetId, evaluationTf);

                if (altRec.Direction != SignalDirection.NO_TRADE)
                {
                    // ML-gated neutral entry
                    var mlDec = altDec with
                    {
                        MlPrediction = mlPrediction,
                        BlendedConfidence = ComputeBlendedConfidence(altDec.ConfidenceScore, mlPrediction, p),
                        EffectiveThreshold = effectiveThreshold
                    };
                    return (altRec, mlDec);
                }
            }

            // Disabled: score-rescue path is intentionally off because
            // the original direction is not reliably available after a NO_TRADE result.
            // Keep ML in annotation/filter mode only until a proper directional
            // candidate structure is introduced.

            return (rec, enhancedDecision);
        }

        // rec.Direction is BUY or SELL — apply ML win probability gate.
        // In accuracy-first mode we raise the gate (base) and bump it further
        // in weak-context setups (NEUTRAL regime, low ADX, weak Asia session).
        decimal effectiveMinWinProb = ComputeEffectiveMinWinProbability(p, regime, snap, out var gateReason);
        if (mlPrediction.CalibratedWinProbability < effectiveMinWinProb)
        {
            var filteredReasons = rec.Reasons.Concat(new[]
            {
                $"ML blocked signal: calibrated win probability {mlPrediction.CalibratedWinProbability:P2} < minimum {effectiveMinWinProb:P2} ({gateReason})"
            }).ToList();

            var filtered = rec with
            {
                Direction = SignalDirection.NO_TRADE,
                Reasons = filteredReasons
            };

            var filteredDec = enhancedDecision with
            {
                DecisionType = SignalDirection.NO_TRADE,
                OutcomeCategory = OutcomeCategory.STRATEGY_NO_TRADE,
                LifecycleState = SignalLifecycleState.ML_FILTERED,
                FinalBlockReason = $"ML gate failed: calibrated win probability {mlPrediction.CalibratedWinProbability:P2} < {effectiveMinWinProb:P2} ({gateReason})",
                ReasonCodes = enhancedDecision.ReasonCodes.Concat(new[] { RejectReasonCode.ML_GATE_FAILED }).Distinct().ToList(),
                ReasonDetails = enhancedDecision.ReasonDetails.Concat(new[]
                {
                    $"ML gate failed: calibrated win probability {mlPrediction.CalibratedWinProbability:P2} < {effectiveMinWinProb:P2} ({gateReason})"
                }).ToList()
            };

            return (filtered, filteredDec);
        }

        // Check blended confidence against effective threshold
        if (blendedConfidence < effectiveThreshold)
        {
            var filteredReasons = rec.Reasons.Concat(new[]
            {
                $"ML blocked signal: blended confidence {blendedConfidence} < threshold {effectiveThreshold}"
            }).ToList();

            var filtered = rec with
            {
                Direction = SignalDirection.NO_TRADE,
                Reasons = filteredReasons
            };

            var filteredDec = enhancedDecision with
            {
                DecisionType = SignalDirection.NO_TRADE,
                OutcomeCategory = OutcomeCategory.STRATEGY_NO_TRADE,
                LifecycleState = SignalLifecycleState.ML_FILTERED,
                FinalBlockReason = $"ML confidence gate failed: blended confidence {blendedConfidence} < threshold {effectiveThreshold}",
                ReasonCodes = enhancedDecision.ReasonCodes.Concat(new[] { RejectReasonCode.ML_GATE_FAILED }).Distinct().ToList(),
                ReasonDetails = enhancedDecision.ReasonDetails.Concat(new[]
                {
                    $"ML confidence gate failed: blended confidence {blendedConfidence} < threshold {effectiveThreshold}"
                }).ToList()
            };

            return (filtered, filteredDec);
        }

        // ML confirms — return the signal with ML annotation
        enhancedDecision = enhancedDecision with { OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED };
        return (rec, enhancedDecision);
    }

    /// <summary>
    /// Accuracy-first effective ML win-probability gate. When accuracy-first
    /// mode is enabled, use <see cref="StrategyParameters.MlAccuracyFirstMinWinProbability"/>
    /// as the base and bump it further in "weak context" setups:
    /// NEUTRAL regime, low ADX, or weak Asia session. Returns the legacy
    /// <see cref="StrategyParameters.MlMinWinProbability"/> otherwise.
    /// </summary>
    public static decimal ComputeEffectiveMinWinProbability(
        StrategyParameters p, RegimeResult regime, IndicatorSnapshot snap, out string reason)
    {
        if (!p.MlAccuracyFirstMode)
        {
            reason = "legacy gate";
            return p.MlMinWinProbability;
        }

        decimal baseProb = p.MlAccuracyFirstMinWinProbability;
        bool weakContext = false;
        var contextReasons = new List<string>();

        if (regime?.Regime == Regime.NEUTRAL)
        {
            weakContext = true;
            contextReasons.Add("NEUTRAL regime");
        }
        if (snap.Adx14 > 0 && snap.Adx14 < p.AccuracyFirstLowAdxThreshold)
        {
            weakContext = true;
            contextReasons.Add($"ADX {snap.Adx14:F1} < {p.AccuracyFirstLowAdxThreshold}");
        }
        if (IsWeakAsiaSession(snap.CandleOpenTimeUtc))
        {
            weakContext = true;
            contextReasons.Add("weak Asia session");
        }

        if (weakContext)
        {
            decimal bumped = Math.Min(0.95m, baseProb + p.MlWeakContextMinWinProbabilityBump);
            reason = "accuracy-first + weak context: " + string.Join(", ", contextReasons);
            return bumped;
        }

        reason = "accuracy-first base";
        return baseProb;
    }

    /// <summary>
    /// Weak Asia session window — the low-liquidity overnight period that tends
    /// to produce choppy, mean-reverting price action. Defaults to 22:00–04:00 UTC,
    /// which sits in the Asian-only liquidity gap and avoids the London/NY overlap.
    /// </summary>
    private static bool IsWeakAsiaSession(DateTimeOffset candleOpenTimeUtc)
    {
        int hour = candleOpenTimeUtc.UtcDateTime.Hour;
        return hour >= 22 || hour < 4;
    }

    private static int ComputeBlendedConfidence(int ruleBasedScore, MlPrediction mlPrediction, StrategyParameters p)
    {
        decimal weight = p.MlConfidenceBlendWeight;
        decimal directionalMlScore = mlPrediction.CalibratedWinProbability * 100m;
        decimal blended = (1m - weight) * ruleBasedScore + weight * directionalMlScore;
        return Math.Clamp((int)Math.Round(blended), 0, 100);
    }

    private static int ComputeEffectiveThreshold(
        SignalRecommendation rec, MlPrediction mlPrediction,
        SignalFrequencyManager? frequencyManager,
        RegimeResult regime, IndicatorSnapshot snap, StrategyParameters p)
    {
        return mlPrediction.RecommendedThreshold;
    }

    // ─── Per-direction scoring ──────────────────────────────────────────
    private record DirectionScore(
        int Score,
        bool HasEntryCondition,
        List<string> Reasons,
        List<RejectReasonCode> RejectCodes);

    private static DirectionScore ScoreDirection(
        bool isBuy, RegimeResult regime, IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap, RichCandle candle, StrategyParameters p,
        int wRegime, int wPullback, int wRsi, int wMacd, int wAdx, int wVolume, int wBody,
        decimal pullbackZonePct, decimal volumeRatioThreshold, decimal bodyRatioThreshold)
    {
        int score = 0;
        var reasons = new List<string>();
        var rejectCodes = new List<RejectReasonCode>();
        string dir = isBuy ? "BUY" : "SELL";

        // 1. Regime alignment — bonus when direction matches regime
        bool regimeAligned = (isBuy && regime.Regime == Regime.BULLISH)
                          || (!isBuy && regime.Regime == Regime.BEARISH);
        if (regimeAligned)
        {
            score += wRegime;
            reasons.Add($"[{dir}] Regime={regime.Regime} aligned (+{wRegime})");
        }
        else
        {
            reasons.Add($"[{dir}] Regime={regime.Regime} not aligned (no regime bonus)");
        }

        // 2. Pullback-and-reclaim
        // For BUY: the candle's low must have dipped INTO the EMA20/VWAP zone from above
        //   (low <= support * (1 + zone)) AND (low >= support * (1 - zone))
        //   meaning it touched near the support, then closed above it.
        // Previous code used only the upper bound check (low <= EMA * 1.005),
        // which is always true when price trades near or below EMA — causing the
        // pullback to fire on every bar in a flat/choppy market.
        bool pullbackPassed = false;
        if (isBuy)
        {
            bool touchedEmaZone = candle.MidLow <= snap.Ema20 * (1m + pullbackZonePct)
                               && candle.MidLow >= snap.Ema20 * (1m - pullbackZonePct);
            bool touchedVwapZone = candle.MidLow <= snap.Vwap * (1m + pullbackZonePct)
                               && candle.MidLow >= snap.Vwap * (1m - pullbackZonePct);
            bool closedAbove = snap.CloseMid > snap.Ema20;
            pullbackPassed = (touchedEmaZone || touchedVwapZone) && closedAbove;

            if (!pullbackPassed && prevSnap != null)
            {
                bool prevBelow = prevSnap.CloseMid <= prevSnap.Ema20;
                pullbackPassed = prevBelow && closedAbove;
            }
        }
        else
        {
            bool touchedEmaZone = candle.MidHigh >= snap.Ema20 * (1m - pullbackZonePct)
                               && candle.MidHigh <= snap.Ema20 * (1m + pullbackZonePct);
            bool touchedVwapZone = candle.MidHigh >= snap.Vwap * (1m - pullbackZonePct)
                               && candle.MidHigh <= snap.Vwap * (1m + pullbackZonePct);
            bool closedBelow = snap.CloseMid < snap.Ema20;
            pullbackPassed = (touchedEmaZone || touchedVwapZone) && closedBelow;

            if (!pullbackPassed && prevSnap != null)
            {
                bool prevAbove = prevSnap.CloseMid >= prevSnap.Ema20;
                pullbackPassed = prevAbove && closedBelow;
            }
        }

        if (pullbackPassed)
        {
            score += wPullback;
            reasons.Add($"[{dir}] Pullback-and-reclaim confirmed (+{wPullback})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.PULLBACK_NOT_VALID);
            reasons.Add($"[{dir}] No pullback-and-reclaim pattern");
        }

        // 3. RSI condition
        bool rsiPassed;
        if (isBuy)
        {
            bool rsiRising = prevSnap != null && snap.Rsi14 > prevSnap.Rsi14;
            rsiPassed = snap.Rsi14 >= p.RsiBuyMin && snap.Rsi14 <= p.RsiBuyMax
                     && (rsiRising || snap.Rsi14 >= p.RsiBuyFallback);
        }
        else
        {
            bool rsiFalling = prevSnap != null && snap.Rsi14 < prevSnap.Rsi14;
            rsiPassed = snap.Rsi14 <= p.RsiSellMax && snap.Rsi14 >= p.RsiSellMin
                     && (rsiFalling || snap.Rsi14 <= p.RsiSellFallback);
        }

        if (rsiPassed)
        {
            score += wRsi;
            reasons.Add($"[{dir}] RSI({snap.Rsi14:F1}) in zone (+{wRsi})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.RSI_OUT_OF_RANGE);
            reasons.Add($"[{dir}] RSI({snap.Rsi14:F1}) condition not met");
        }

        // 4. MACD condition — crossover from opposite side, OR in territory and strengthening
        bool macdPassed;
        bool macdCrossover;
        if (isBuy)
        {
            macdCrossover = snap.MacdHist > 0 && prevSnap != null && prevSnap.MacdHist <= 0;
            // Also pass if histogram is positive and not declining (momentum holding or growing)
            macdPassed = snap.MacdHist > 0
                && (prevSnap == null || prevSnap.MacdHist <= 0 || snap.MacdHist >= prevSnap.MacdHist);
        }
        else
        {
            macdCrossover = snap.MacdHist < 0 && prevSnap != null && prevSnap.MacdHist >= 0;
            macdPassed = snap.MacdHist < 0
                && (prevSnap == null || prevSnap.MacdHist >= 0 || snap.MacdHist <= prevSnap.MacdHist);
        }

        if (macdPassed)
        {
            score += wMacd;
            string macdTag = macdCrossover ? "crossover" : "momentum";
            reasons.Add($"[{dir}] MACD hist={snap.MacdHist:F4} {macdTag} (+{wMacd})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.MACD_CONFIRMATION_FAILED);
            reasons.Add($"[{dir}] MACD hist={snap.MacdHist:F4} no confirmation");
        }

        // 5. ADX strength
        if (snap.Adx14 >= p.AdxTrendThreshold)
        {
            score += wAdx;
            reasons.Add($"[{dir}] ADX({snap.Adx14:F1}) >= {p.AdxTrendThreshold} (+{wAdx})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.ADX_TOO_LOW);
            reasons.Add($"[{dir}] ADX({snap.Adx14:F1}) < {p.AdxTrendThreshold}");
        }

        // 6. Volume confirmation
        bool volumePassed = snap.VolumeSma20 > 0 &&
            candle.Volume > volumeRatioThreshold * snap.VolumeSma20;
        if (volumePassed)
        {
            score += wVolume;
            reasons.Add($"[{dir}] Volume ok (+{wVolume})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.VOLUME_TOO_LOW);
            reasons.Add($"[{dir}] Volume too low");
        }

        // 7. Body ratio (directional candle confirmation)
        decimal bodyRatio = ComputeBodyRatio(candle);
        bool bodyPassed = bodyRatio >= bodyRatioThreshold;
        bool bodyDirectional = isBuy ? candle.MidClose > candle.MidOpen : candle.MidClose < candle.MidOpen;
        if (bodyPassed && bodyDirectional)
        {
            score += wBody;
            reasons.Add($"[{dir}] Body ratio({bodyRatio:F2}) directional (+{wBody})");
        }
        else
        {
            rejectCodes.Add(RejectReasonCode.BODY_RATIO_TOO_SMALL);
            reasons.Add($"[{dir}] Body ratio({bodyRatio:F2}) or direction not confirmed");
        }

        bool hasEntry = pullbackPassed && (rsiPassed || macdPassed);
        return new DirectionScore(score, hasEntry, reasons, rejectCodes);
    }

    public static decimal ComputeBodyRatio(RichCandle candle)
    {
        decimal range = candle.MidHigh - candle.MidLow;
        if (range == 0) return 0;
        decimal body = Math.Abs(candle.MidClose - candle.MidOpen);
        return body / range;
    }

    private static SignalRecommendation NoTrade(
        string symbol, DateTimeOffset time, Regime regime, int score, List<string> reasons, string tfName = "5m") =>
        new()
        {
            Symbol = symbol,
            Timeframe = tfName,
            SignalTimeUtc = time,
            Direction = SignalDirection.NO_TRADE,
            ConfidenceScore = score,
            Regime = regime,
            StrategyVersion = StrategyVersion,
            Reasons = reasons
        };

    private static SignalDecision BuildDecision(
        SignalRecommendation rec, DateTimeOffset decisionTime, DateTimeOffset barTime,
        List<RejectReasonCode> reasonCodes, List<string> reasonDetails,
        IReadOnlyDictionary<string, decimal> indicators, OutcomeCategory outcomeCategory,
        Regime? regime, DateTimeOffset? regimeTimestamp,
        SourceMode sourceMode, string? paramSetId, int score)
    {
        // FR-1: Set lifecycle state based on engine outcome.
        // CANDIDATE_CREATED when the engine produced a directional signal (BUY/SELL);
        // EVALUATED for all NO_TRADE outcomes (strategy rejection, context-not-ready, etc.).
        // Downstream gates (ML, risk, session) may further advance the state.
        var lifecycleState = outcomeCategory == OutcomeCategory.SIGNAL_GENERATED
            ? SignalLifecycleState.CANDIDATE_CREATED
            : SignalLifecycleState.EVALUATED;

        return new SignalDecision
        {
            Symbol = rec.Symbol,
            Timeframe = rec.Timeframe,
            DecisionTimeUtc = decisionTime,
            BarTimeUtc = barTime,
            DecisionType = rec.Direction,
            OutcomeCategory = outcomeCategory,
            LifecycleState = lifecycleState,
            UsedRegime = regime,
            UsedRegimeTimestamp = regimeTimestamp,
            ReasonCodes = reasonCodes,
            ReasonDetails = reasonDetails,
            ConfidenceScore = score,
            IndicatorSnapshot = indicators,
            ParameterSetId = paramSetId,
            SourceMode = sourceMode,
            CandidateDirection = rec.Direction != SignalDirection.NO_TRADE ? rec.Direction : null
        };
    }

    private static Dictionary<string, decimal> BuildIndicatorSnapshot(IndicatorSnapshot snap, RichCandle candle)
    {
        return new Dictionary<string, decimal>
        {
            ["ema20"] = snap.Ema20,
            ["ema50"] = snap.Ema50,
            ["rsi14"] = snap.Rsi14,
            ["macd_hist"] = snap.MacdHist,
            ["adx14"] = snap.Adx14,
            ["plus_di"] = snap.PlusDi,
            ["minus_di"] = snap.MinusDi,
            ["atr14"] = snap.Atr14,
            ["vwap"] = snap.Vwap,
            ["volume_sma20"] = snap.VolumeSma20,
            ["spread"] = snap.Spread,
            ["close_mid"] = snap.CloseMid,
            ["volume"] = candle.Volume,
            ["body_ratio"] = ComputeBodyRatio(candle)
        };
    }
}
