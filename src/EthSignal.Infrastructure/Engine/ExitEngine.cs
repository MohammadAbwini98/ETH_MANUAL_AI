using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Computes structure-aware, volatility-adjusted, regime-aware, confidence-scaled
/// TP and SL levels. Replaces the simple ATR×R:R model with a layered exit system.
/// </summary>
public static class ExitEngine
{
    /// <summary>Full exit computation result.</summary>
    public sealed record ExitResult
    {
        public required bool Allowed { get; init; }
        public decimal StopLoss { get; init; }
        public decimal TakeProfit { get; init; }
        public decimal StopDistance { get; init; }

        /// <summary>Multi-target TPs: early partial, structure target, trailing runner anchor.</summary>
        public decimal Tp1 { get; init; }
        public decimal Tp2 { get; init; }
        public decimal Tp3 { get; init; }

        public decimal RiskRewardRatio { get; init; }

        /// <summary>Which exit model produced these values.</summary>
        public required string ExitModel { get; init; }

        /// <summary>Human-readable explanation of exit selection.</summary>
        public required string Explanation { get; init; }

        /// <summary>Rejection reason when Allowed=false.</summary>
        public string? RejectReason { get; init; }
    }

    /// <summary>All inputs needed for a full exit calculation.</summary>
    public sealed record ExitContext
    {
        public required SignalDirection Direction { get; init; }
        public required decimal EntryPrice { get; init; }
        public required decimal Atr { get; init; }
        public required decimal SpreadPct { get; init; }
        public required int ConfidenceScore { get; init; }
        public required Regime Regime { get; init; }
        public required string Timeframe { get; init; }

        /// <summary>Structure levels from StructureAnalyzer.</summary>
        public StructureAnalyzer.StructureLevels? Structure { get; init; }

        /// <summary>Legacy swing extreme from last 5 candles (fallback).</summary>
        public decimal SwingExtreme { get; init; }
    }

    /// <summary>Exit-specific parameters extracted from StrategyParameters.</summary>
    public sealed record ExitPolicy
    {
        // SL
        public decimal AtrMultiplier { get; init; } = 2.0m;
        public decimal SpreadSlippageBufferPct { get; init; } = 0.0005m;
        public decimal MinStopDistancePct { get; init; } = 0.002m;
        public decimal MaxStopDistancePct { get; init; } = 0.05m;

        // TP
        public decimal MinRewardToRisk { get; init; } = 1.2m;
        public decimal DefaultRewardToRisk { get; init; } = 1.5m;

        // Multi-target
        public decimal Tp1RMultiple { get; init; } = 1.0m;
        public decimal Tp2RMultiple { get; init; } = 2.0m;
        public decimal Tp3RMultiple { get; init; } = 3.0m;

        // Regime multipliers (applied to default R:R)
        public decimal TrendingTpMultiplier { get; init; } = 1.3m;
        public decimal TrendingSlMultiplier { get; init; } = 1.1m;

        public decimal RangingTpMultiplier { get; init; } = 0.8m;
        public decimal RangingSlMultiplier { get; init; } = 0.9m;

        // Confidence scaling
        public decimal HighConfidenceTpBoost { get; init; } = 1.2m;
        public decimal LowConfidenceTpReduce { get; init; } = 0.8m;
        public int HighConfidenceThreshold { get; init; } = 80;
        public int LowConfidenceThreshold { get; init; } = 55;

        // ATR-based TP projection limits
        public decimal ScalpMinAtrTpMultiplier { get; init; } = 0.8m;
        public decimal ScalpMaxAtrTpMultiplier { get; init; } = 1.5m;
        public decimal IntradayMinAtrTpMultiplier { get; init; } = 1.5m;
        public decimal IntradayMaxAtrTpMultiplier { get; init; } = 3.0m;
        public decimal StructureBufferAtrMultiplier { get; init; } = 0.15m;

        // Risk
        public decimal AccountBalanceUsd { get; init; } = 50m;
        public decimal RiskPercentPerTrade { get; init; } = 0.5m;
        public decimal HardMaxRiskPercent { get; init; } = 1.0m;

        // Gates
        public decimal MinAtrThreshold { get; init; } = 0.8m;
        public decimal MaxSpreadPct { get; init; } = 0.004m;
    }

    /// <summary>
    /// Compute structure-aware, volatility-adjusted, regime-aware, confidence-scaled exits.
    /// This is the primary exit calculation entry point for all signal paths.
    /// </summary>
    public static ExitResult Compute(ExitContext ctx, ExitPolicy policy)
    {
        // ── Gate checks ──────────────────────────────────────
        if (ctx.Direction == SignalDirection.NO_TRADE)
            return Reject("NO_TRADE direction");

        if (ctx.SpreadPct >= policy.MaxSpreadPct)
            return Reject($"SpreadPct({ctx.SpreadPct:P3}) >= max({policy.MaxSpreadPct:P1})");

        if (ctx.Atr < policy.MinAtrThreshold)
            return Reject($"ATR({ctx.Atr:F4}) < min({policy.MinAtrThreshold})");

        if (policy.RiskPercentPerTrade > policy.HardMaxRiskPercent)
            return Reject($"RiskPercent({policy.RiskPercentPerTrade}%) > HardMax({policy.HardMaxRiskPercent}%)");

        var explanation = new List<string>();
        bool isScalp = ctx.Timeframe == "1m";
        decimal minAtrTpMult = isScalp ? policy.ScalpMinAtrTpMultiplier : policy.IntradayMinAtrTpMultiplier;
        decimal maxAtrTpMult = isScalp ? policy.ScalpMaxAtrTpMultiplier : policy.IntradayMaxAtrTpMultiplier;
        decimal atrTpMin = minAtrTpMult * ctx.Atr;
        decimal atrTpMax = maxAtrTpMult * ctx.Atr;

        // ═══════════════════════════════════════════════════════
        // 1. STOP LOSS — Layered calculation
        // ═══════════════════════════════════════════════════════

        // Layer 1: ATR-based minimum stop distance
        decimal atrStop = policy.AtrMultiplier * ctx.Atr;
        explanation.Add($"ATR stop: {policy.AtrMultiplier}×{ctx.Atr:F4} = {atrStop:F4}");

        // Layer 2: Structure-based invalidation level
        decimal structureStop = 0;
        if (ctx.Structure != null)
        {
            decimal invalidation = StructureAnalyzer.FindInvalidationLevel(
                ctx.Structure, ctx.Direction, ctx.EntryPrice);
            if (invalidation > 0)
            {
                structureStop = Math.Abs(ctx.EntryPrice - invalidation) + ctx.Atr * policy.StructureBufferAtrMultiplier;
                explanation.Add($"Structure invalidation: {invalidation:F2} (dist={structureStop:F4})");
            }
        }

        // Layer 2b: Legacy swing extreme fallback
        decimal swingStop = 0;
        if (ctx.SwingExtreme > 0)
        {
            swingStop = ctx.Direction == SignalDirection.BUY
                ? ctx.EntryPrice - ctx.SwingExtreme
                : ctx.SwingExtreme - ctx.EntryPrice;
            if (swingStop < 0) swingStop = 0;
        }

        // Use the best structure-based stop (prefer proper structure over legacy swing)
        decimal bestStructureStop = structureStop > 0 ? structureStop : swingStop;
        explanation.Add($"Best structure stop: {bestStructureStop:F4}");

        // Layer 3: Take the greater of ATR-based and structure-based
        decimal rawStopDistance = Math.Max(atrStop, bestStructureStop);
        if (rawStopDistance <= 0) rawStopDistance = atrStop;

        // On slower timeframes, a distant structure invalidation can produce a stop so
        // wide that even the maximum ATR-realistic TP band can never satisfy minimum
        // R:R. In that case, fall back to the ATR-viable stop instead of hard-blocking
        // the setup purely because the nearest swing structure is too far away.
        if (!isScalp && bestStructureStop > 0 && policy.MinRewardToRisk > 0)
        {
            decimal maxViableStopFromAtrBand = atrTpMax / policy.MinRewardToRisk;
            if (maxViableStopFromAtrBand > 0
                && bestStructureStop > maxViableStopFromAtrBand
                && atrStop > 0
                && atrStop <= maxViableStopFromAtrBand)
            {
                rawStopDistance = atrStop;
                explanation.Add(
                    $"Structure stop {bestStructureStop:F4} exceeds ATR-viable stop {maxViableStopFromAtrBand:F4}; using ATR stop for {ctx.Timeframe}");
            }
        }

        // Layer 4: Spread/slippage buffer
        decimal executionBuffer = ctx.EntryPrice * policy.SpreadSlippageBufferPct;
        decimal stopWithBuffer = rawStopDistance + executionBuffer;
        explanation.Add($"Execution buffer: {executionBuffer:F4}");

        // Layer 5: Regime adjustment to SL
        decimal regimeSlMultiplier = ctx.Regime switch
        {
            Regime.BULLISH when ctx.Direction == SignalDirection.BUY => policy.TrendingSlMultiplier,
            Regime.BEARISH when ctx.Direction == SignalDirection.SELL => policy.TrendingSlMultiplier,
            Regime.NEUTRAL => policy.RangingSlMultiplier,
            _ => 1.0m // Counter-regime trades get no special adjustment
        };
        decimal adjustedStop = stopWithBuffer * regimeSlMultiplier;
        explanation.Add($"Regime SL multiplier: {regimeSlMultiplier}");

        // Layer 6: Floor and ceiling
        decimal minStop = ctx.EntryPrice * policy.MinStopDistancePct;
        decimal maxStop = ctx.EntryPrice * policy.MaxStopDistancePct;
        decimal finalStopDistance = Math.Clamp(adjustedStop, minStop, maxStop);
        explanation.Add($"Final stop distance: {finalStopDistance:F4} (range [{minStop:F4}–{maxStop:F4}])");

        // Block ultra-tight stops
        if (ctx.EntryPrice > 0 && finalStopDistance / ctx.EntryPrice < 0.0015m)
            return Reject($"StopDistance({finalStopDistance:F4})/Entry({ctx.EntryPrice:F2}) = {finalStopDistance / ctx.EntryPrice:P3} < 0.15%");

        // Compute SL price
        decimal sl = ctx.Direction == SignalDirection.BUY
            ? ctx.EntryPrice - finalStopDistance
            : ctx.EntryPrice + finalStopDistance;

        // ═══════════════════════════════════════════════════════
        // 2. TAKE PROFIT — Layered calculation
        // ═══════════════════════════════════════════════════════

        // Layer 1: ATR-based TP projection
        decimal atrTpDistance = policy.DefaultRewardToRisk * finalStopDistance;
        explanation.Add($"ATR TP range: [{atrTpMin:F4}–{atrTpMax:F4}]");

        // Start with the R:R target, but keep it inside the ATR-realistic band.
        decimal baseTpDistance = Math.Clamp(atrTpDistance, atrTpMin, atrTpMax);
        if (baseTpDistance != atrTpDistance)
            explanation.Add($"ATR TP band applied: {atrTpDistance:F4} → {baseTpDistance:F4}");

        // Layer 2: Structure-based TP target
        decimal structureTarget = 0;
        if (ctx.Structure != null)
        {
            structureTarget = StructureAnalyzer.FindStructureTarget(
                ctx.Structure, ctx.Direction, ctx.EntryPrice);
        }

        decimal structureTargetDistance = structureTarget > 0
            ? Math.Abs(structureTarget - ctx.EntryPrice)
            : 0;
        decimal selectedStructureTarget = 0m;

        if (structureTarget > 0)
            explanation.Add($"Structure TP target: {structureTarget:F2} (dist={structureTargetDistance:F4})");

        // Layer 3: Choose the best TP distance
        // If structure target exists and is within the ATR-realistic range, prefer it.
        if (structureTargetDistance > 0)
        {
            if (structureTargetDistance >= atrTpMin && structureTargetDistance <= atrTpMax)
            {
                // Structure target is realistic — use it.
                baseTpDistance = structureTargetDistance;
                selectedStructureTarget = structureTarget;
                explanation.Add("Using structure-based TP (within ATR range)");
            }
            else if (structureTargetDistance < atrTpMin)
            {
                // A very near structure level is often just local noise on HTFs.
                // Try the next meaningful structure before rejecting the setup.
                decimal rr = structureTargetDistance / finalStopDistance;
                if (rr >= policy.MinRewardToRisk)
                {
                    baseTpDistance = structureTargetDistance;
                    selectedStructureTarget = structureTarget;
                    explanation.Add($"Using conservative structure TP (R:R={rr:F2} acceptable)");
                }
                else
                {
                    var alternateStructureTarget = ctx.Structure != null
                        ? StructureAnalyzer.FindStructureTarget(ctx.Structure, ctx.Direction, ctx.EntryPrice, atrTpMin)
                        : 0m;
                    var alternateStructureDistance = alternateStructureTarget > 0
                        ? Math.Abs(alternateStructureTarget - ctx.EntryPrice)
                        : 0m;

                    if (alternateStructureDistance >= atrTpMin && alternateStructureDistance <= atrTpMax)
                    {
                        baseTpDistance = alternateStructureDistance;
                        selectedStructureTarget = alternateStructureTarget;
                        explanation.Add(
                            $"Nearest structure too close (R:R={rr:F2}); using next structure target {alternateStructureTarget:F2}");
                    }
                    else if (!isScalp)
                    {
                        explanation.Add(
                            $"Nearest structure too close (R:R={rr:F2}); falling back to ATR-based TP for {ctx.Timeframe}");
                    }
                    else
                    {
                        return Reject($"Structure target too close: dist={structureTargetDistance:F4}, " +
                            $"R:R={rr:F2} < min({policy.MinRewardToRisk}), structure blocks realistic target");
                    }
                }
            }
            else
            {
                explanation.Add("Structure target beyond ATR TP cap; using ATR-based TP band");
            }
        }

        // Layer 4: Regime adjustment to TP
        decimal regimeTpMultiplier = ctx.Regime switch
        {
            Regime.BULLISH when ctx.Direction == SignalDirection.BUY => policy.TrendingTpMultiplier,
            Regime.BEARISH when ctx.Direction == SignalDirection.SELL => policy.TrendingTpMultiplier,
            Regime.NEUTRAL => policy.RangingTpMultiplier,
            _ => 1.0m
        };
        decimal regimeAdjustedTp = baseTpDistance * regimeTpMultiplier;
        explanation.Add($"Regime TP multiplier: {regimeTpMultiplier}");

        // Layer 5: Confidence-based TP scaling
        decimal confidenceMultiplier = 1.0m;
        if (ctx.ConfidenceScore >= policy.HighConfidenceThreshold)
        {
            confidenceMultiplier = policy.HighConfidenceTpBoost;
            explanation.Add($"High confidence ({ctx.ConfidenceScore}) TP boost: ×{confidenceMultiplier}");
        }
        else if (ctx.ConfidenceScore <= policy.LowConfidenceThreshold)
        {
            confidenceMultiplier = policy.LowConfidenceTpReduce;
            explanation.Add($"Low confidence ({ctx.ConfidenceScore}) TP reduce: ×{confidenceMultiplier}");
        }
        decimal finalTpDistance = Math.Clamp(regimeAdjustedTp * confidenceMultiplier, atrTpMin, atrTpMax);

        // Layer 6: Enforce minimum R:R
        decimal actualRR = finalStopDistance > 0 ? finalTpDistance / finalStopDistance : 0;
        if (actualRR < policy.MinRewardToRisk)
        {
            // Try to push TP to minimum acceptable R:R
            decimal minTpDistance = finalStopDistance * policy.MinRewardToRisk;
            // But only if it's within ATR-realistic range
            if (minTpDistance <= atrTpMax)
            {
                finalTpDistance = minTpDistance;
                actualRR = policy.MinRewardToRisk;
                explanation.Add($"TP pushed to minimum R:R {policy.MinRewardToRisk}");
            }
            else
            {
                return Reject($"R:R({actualRR:F2}) < min({policy.MinRewardToRisk}) and ATR cannot support minimum TP");
            }
        }

        // Compute TP price
        decimal tp = ctx.Direction == SignalDirection.BUY
            ? ctx.EntryPrice + finalTpDistance
            : ctx.EntryPrice - finalTpDistance;

        // ═══════════════════════════════════════════════════════
        // 3. MULTI-TARGET TPs
        // ═══════════════════════════════════════════════════════
        decimal tp1Distance = finalStopDistance * policy.Tp1RMultiple;
        decimal tp2Distance = finalStopDistance * policy.Tp2RMultiple;
        decimal tp3Distance = finalStopDistance * policy.Tp3RMultiple;

        decimal tp1, tp2, tp3;
        if (ctx.Direction == SignalDirection.BUY)
        {
            tp1 = ctx.EntryPrice + tp1Distance;
            tp2 = ctx.EntryPrice + tp2Distance;
            tp3 = ctx.EntryPrice + tp3Distance;
        }
        else
        {
            tp1 = ctx.EntryPrice - tp1Distance;
            tp2 = ctx.EntryPrice - tp2Distance;
            tp3 = ctx.EntryPrice - tp3Distance;
        }

        // If structure target is significant, align TP2 with it
        if (selectedStructureTarget > 0)
        {
            decimal structDist = Math.Abs(selectedStructureTarget - ctx.EntryPrice);
            if (structDist > tp1Distance && structDist < tp3Distance)
            {
                tp2 = selectedStructureTarget;
                explanation.Add($"TP2 aligned with structure target: {tp2:F2}");
            }
        }

        // Determine exit model used
        string exitModel = structureStop > 0
            ? (selectedStructureTarget > 0 ? "STRUCTURE_FULL" : "STRUCTURE_SL_ONLY")
            : "ATR_BASED";

        return new ExitResult
        {
            Allowed = true,
            StopLoss = sl,
            TakeProfit = tp,
            StopDistance = finalStopDistance,
            Tp1 = tp1,
            Tp2 = tp2,
            Tp3 = tp3,
            RiskRewardRatio = actualRR,
            ExitModel = exitModel,
            Explanation = string.Join(" | ", explanation),
            RejectReason = null
        };
    }

    /// <summary>Build an ExitPolicy from StrategyParameters.</summary>
    public static ExitPolicy BuildPolicy(StrategyParameters p)
    {
        var effectiveMinRewardToRisk = ResolveMinRewardToRisk(p.MinRiskRewardAfterRounding, p.TargetRMultiple);
        return new ExitPolicy
        {
            AtrMultiplier = p.StopAtrMultiplier,
            SpreadSlippageBufferPct = p.LiveEntrySlippageBufferPct,
            MinStopDistancePct = p.MinStopDistancePct,
            MaxStopDistancePct = p.ExitMaxStopDistancePct,
            MinRewardToRisk = effectiveMinRewardToRisk,
            DefaultRewardToRisk = p.TargetRMultiple,
            Tp1RMultiple = p.ExitTp1RMultiple,
            Tp2RMultiple = p.ExitTp2RMultiple,
            Tp3RMultiple = p.ExitTp3RMultiple,
            TrendingTpMultiplier = p.ExitTrendingTpMultiplier,
            TrendingSlMultiplier = p.ExitTrendingSlMultiplier,
            RangingTpMultiplier = p.ExitRangingTpMultiplier,
            RangingSlMultiplier = p.ExitRangingSlMultiplier,
            HighConfidenceTpBoost = p.ExitHighConfidenceTpBoost,
            LowConfidenceTpReduce = p.ExitLowConfidenceTpReduce,
            HighConfidenceThreshold = p.ExitHighConfidenceThreshold,
            LowConfidenceThreshold = p.ExitLowConfidenceThreshold,
            ScalpMinAtrTpMultiplier = p.ExitScalpMinAtrTpMultiplier,
            ScalpMaxAtrTpMultiplier = p.ExitScalpMaxAtrTpMultiplier,
            IntradayMinAtrTpMultiplier = p.ExitIntradayMinAtrTpMultiplier,
            IntradayMaxAtrTpMultiplier = p.ExitIntradayMaxAtrTpMultiplier,
            StructureBufferAtrMultiplier = p.ExitStructureBufferAtrMultiplier,
            AccountBalanceUsd = p.AccountBalanceUsd,
            RiskPercentPerTrade = p.RiskPerTradePercent,
            HardMaxRiskPercent = p.HardMaxRiskPercent,
            MinAtrThreshold = p.MinAtrThreshold,
            MaxSpreadPct = p.MaxSpreadPct
        };
    }

    public static ExitPolicy BuildPolicy(StrategyParameters p, string timeframe)
    {
        var resolved = p.ResolveForTimeframe(timeframe);
        var bucket = resolved.ResolveTimeframeProfileBucket(timeframe);
        var intradayMinAtrTpMultiplier = bucket == TimeframeProfileBucket.Long
            ? resolved.ExitHigherTfMinAtrTpMultiplier
            : resolved.ExitIntradayMinAtrTpMultiplier;
        var intradayMaxAtrTpMultiplier = bucket == TimeframeProfileBucket.Long
            ? resolved.ExitHigherTfMaxAtrTpMultiplier
            : resolved.ExitIntradayMaxAtrTpMultiplier;
        var effectiveMinRewardToRisk = ResolveMinRewardToRisk(
            resolved.MinRiskRewardAfterRounding,
            resolved.TargetRMultiple);

        return new ExitPolicy
        {
            AtrMultiplier = resolved.StopAtrMultiplier,
            SpreadSlippageBufferPct = resolved.LiveEntrySlippageBufferPct,
            MinStopDistancePct = resolved.MinStopDistancePct,
            MaxStopDistancePct = resolved.ExitMaxStopDistancePct,
            MinRewardToRisk = effectiveMinRewardToRisk,
            DefaultRewardToRisk = resolved.TargetRMultiple,
            Tp1RMultiple = resolved.ExitTp1RMultiple,
            Tp2RMultiple = resolved.ExitTp2RMultiple,
            Tp3RMultiple = resolved.ExitTp3RMultiple,
            TrendingTpMultiplier = resolved.ExitTrendingTpMultiplier,
            TrendingSlMultiplier = resolved.ExitTrendingSlMultiplier,
            RangingTpMultiplier = resolved.ExitRangingTpMultiplier,
            RangingSlMultiplier = resolved.ExitRangingSlMultiplier,
            HighConfidenceTpBoost = resolved.ExitHighConfidenceTpBoost,
            LowConfidenceTpReduce = resolved.ExitLowConfidenceTpReduce,
            HighConfidenceThreshold = resolved.ExitHighConfidenceThreshold,
            LowConfidenceThreshold = resolved.ExitLowConfidenceThreshold,
            ScalpMinAtrTpMultiplier = resolved.ExitScalpMinAtrTpMultiplier,
            ScalpMaxAtrTpMultiplier = resolved.ExitScalpMaxAtrTpMultiplier,
            IntradayMinAtrTpMultiplier = intradayMinAtrTpMultiplier,
            IntradayMaxAtrTpMultiplier = intradayMaxAtrTpMultiplier,
            StructureBufferAtrMultiplier = resolved.ExitStructureBufferAtrMultiplier,
            AccountBalanceUsd = resolved.AccountBalanceUsd,
            RiskPercentPerTrade = resolved.RiskPerTradePercent,
            HardMaxRiskPercent = resolved.HardMaxRiskPercent,
            MinAtrThreshold = resolved.MinAtrThreshold,
            MaxSpreadPct = resolved.MaxSpreadPct
        };
    }

    /// <summary>Build a scalp-specific ExitPolicy.</summary>
    public static ExitPolicy BuildScalpPolicy(StrategyParameters p)
    {
        var resolved = p.ResolveForTimeframe(Timeframe.M1.Name);
        var policy = BuildPolicy(resolved, Timeframe.M1.Name);
        return policy with
        {
            AtrMultiplier = resolved.ScalpStopAtrMultiplier,
            MinRewardToRisk = ResolveMinRewardToRisk(policy.MinRewardToRisk, resolved.ScalpTargetRMultiple),
            DefaultRewardToRisk = resolved.ScalpTargetRMultiple,
            MinAtrThreshold = resolved.ScalpMinAtr,
            // Tighter TPs for scalps
            Tp1RMultiple = resolved.ExitScalpTp1RMultiple,
            Tp2RMultiple = resolved.ExitScalpTp2RMultiple,
            Tp3RMultiple = resolved.ExitScalpTp3RMultiple,
            ScalpMinAtrTpMultiplier = resolved.ExitScalpMinAtrTpMultiplier,
            ScalpMaxAtrTpMultiplier = resolved.ExitScalpMaxAtrTpMultiplier
        };
    }

    private static ExitResult Reject(string reason) => new()
    {
        Allowed = false,
        ExitModel = "REJECTED",
        Explanation = reason,
        RejectReason = reason
    };

    private static decimal ResolveMinRewardToRisk(decimal desiredMinRewardToRisk, decimal targetRewardToRisk)
    {
        if (targetRewardToRisk <= 0)
            return desiredMinRewardToRisk;

        return Math.Min(desiredMinRewardToRisk, targetRewardToRisk);
    }
}
