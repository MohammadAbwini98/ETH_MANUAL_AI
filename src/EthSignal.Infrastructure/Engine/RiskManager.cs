using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Phase 5: Converts raw signal into a full recommendation with Entry, TP, SL, risk sizing.
/// </summary>
public static class RiskManager
{
    public record RiskResult(
        bool Allowed,
        decimal EntryPrice,
        decimal StopLoss,
        decimal TakeProfit,
        decimal StopDistance,
        decimal RiskUsd,
        decimal RiskPercent,
        string? BlockReason);

    public static RiskResult ComputeRisk(
        SignalDirection direction,
        decimal entryPrice,
        decimal atr,
        decimal swingExtreme,
        decimal spreadPct,
        RiskPolicy policy)
    {
        // Safeguard checks
        if (direction == SignalDirection.NO_TRADE)
            return Blocked("NO_TRADE direction");

        if (spreadPct >= policy.MaxSpreadPct)
            return Blocked($"SpreadPct({spreadPct:P3}) >= max({policy.MaxSpreadPct:P1})");

        if (atr < policy.MinAtrThreshold)
            return Blocked($"ATR({atr:F4}) below min threshold({policy.MinAtrThreshold})");

        // B-09: Enforce hard max risk percent
        if (policy.RiskPercentPerTrade > policy.HardMaxRiskPercent)
            return Blocked($"RiskPercent({policy.RiskPercentPerTrade}%) > HardMax({policy.HardMaxRiskPercent}%)");

        // Risk amount in USD
        decimal riskUsd = policy.AccountBalanceUsd * policy.RiskPercentPerTrade / 100m;

        // Stop distance
        decimal atrStop = policy.AtrMultiplier * atr;
        decimal swingStop = direction == SignalDirection.BUY
            ? entryPrice - swingExtreme
            : swingExtreme - entryPrice;
        if (swingStop < 0) swingStop = 0;
        decimal stopDistance = Math.Max(atrStop, swingStop);
        if (stopDistance <= 0) stopDistance = atrStop;

        // Minimum stop distance floor: ensure stop is meaningful relative to price
        decimal minStopDistance = entryPrice * 0.002m;
        stopDistance = Math.Max(stopDistance, minStopDistance);

        // Block ultra-tight stops
        if (entryPrice > 0 && stopDistance / entryPrice < 0.0015m)
            return Blocked($"StopDistance({stopDistance:F4}) / EntryPrice({entryPrice:F2}) = {stopDistance / entryPrice:P3} < minimum 0.15%");

        // SL and TP
        decimal sl, tp;
        if (direction == SignalDirection.BUY)
        {
            sl = entryPrice - stopDistance;
            tp = entryPrice + policy.RewardToRisk * stopDistance;
        }
        else
        {
            sl = entryPrice + stopDistance;
            tp = entryPrice - policy.RewardToRisk * stopDistance;
        }

        // Verify risk/reward after rounding
        decimal tpDistance = Math.Abs(tp - entryPrice);
        decimal actualRR = stopDistance > 0 ? tpDistance / stopDistance : 0;
        if (actualRR < policy.MinRiskRewardAfterRounding)
            return Blocked($"R:R({actualRR:F2}) < min({policy.MinRiskRewardAfterRounding})");

        return new RiskResult(
            Allowed: true,
            EntryPrice: entryPrice,
            StopLoss: sl,
            TakeProfit: tp,
            StopDistance: stopDistance,
            RiskUsd: riskUsd,
            RiskPercent: policy.RiskPercentPerTrade,
            BlockReason: null);
    }

    public static decimal EstimateLiveFillPrice(
        SignalDirection direction,
        decimal referencePrice,
        decimal spreadPct,
        decimal entryBufferPct)
        => EstimateLiveFillPrice(
            direction,
            referencePrice,
            spreadPct,
            StrategyParameters.Default with { LiveEntrySlippageBufferPct = entryBufferPct },
            Timeframe.M5.Name,
            confidenceScore: 0,
            atr: 0m);

    public static decimal EstimateLiveFillPrice(
        SignalDirection direction,
        decimal referencePrice,
        decimal spreadPct,
        StrategyParameters parameters,
        string timeframe,
        int confidenceScore,
        decimal atr)
    {
        if (direction == SignalDirection.NO_TRADE || referencePrice <= 0)
            return referencePrice;

        var resolvedParameters = parameters.ResolveForTimeframe(timeframe);
        var effectiveBufferPct = Math.Max(0m, Math.Max(resolvedParameters.LiveEntrySlippageBufferPct, spreadPct / 2m));
        var atrBufferMultiplier = resolvedParameters.ResolveTimeframeProfileBucket(timeframe) switch
        {
            TimeframeProfileBucket.Fast => resolvedParameters.FastTimeframeEntryAtrMultiplier,
            TimeframeProfileBucket.Long => resolvedParameters.LongTimeframeEntryAtrMultiplier,
            _ => resolvedParameters.MidTimeframeEntryAtrMultiplier
        };
        var cappedAtrBuffer = atr > 0
            ? Math.Min(atr * atrBufferMultiplier, referencePrice * resolvedParameters.EntryAtrBufferCapPct)
            : 0m;
        var confidenceMultiplier = confidenceScore >= resolvedParameters.ExitHighConfidenceThreshold
            ? resolvedParameters.HighConfidenceEntryBufferMultiplier
            : confidenceScore <= resolvedParameters.ExitLowConfidenceThreshold
                ? resolvedParameters.LowConfidenceEntryBufferMultiplier
                : 1m;
        var absoluteBuffer = (referencePrice * effectiveBufferPct + cappedAtrBuffer) * confidenceMultiplier;
        var multiplier = direction == SignalDirection.BUY
            ? 1m + absoluteBuffer / referencePrice
            : 1m - absoluteBuffer / referencePrice;

        return referencePrice * multiplier;
    }

    public static SignalRecommendation ReanchorToFilledEntry(
        SignalRecommendation signal,
        decimal filledEntryPrice)
    {
        if (filledEntryPrice <= 0 || signal.EntryPrice <= 0 || filledEntryPrice == signal.EntryPrice)
            return signal;

        var delta = filledEntryPrice - signal.EntryPrice;
        return signal with
        {
            EntryPrice = filledEntryPrice,
            TpPrice = signal.TpPrice + delta,
            SlPrice = signal.SlPrice + delta
        };
    }

    /// <summary>
    /// P5-01: Checks session-level risk limits before allowing a new trade.
    /// Returns null if all limits pass, or a block reason string if any limit is breached.
    /// <param name="isScalp">
    /// When true, scalp-specific thresholds (ScalpMaxConsecutiveLossesPerDay,
    /// ScalpDailyMaxDrawdownPercent) are used instead of the shared HTF limits —
    /// provided they are non-zero. This prevents HTF losses from blocking 1m scalp
    /// slots and vice-versa.
    /// </param>
    /// </summary>
    public static string? CheckSessionLimits(
        RiskPolicy policy,
        int openPositionCount,
        IReadOnlyList<SignalOutcome> todayOutcomes,
        bool isScalp = false)
    {
        // Max open positions — still global (scoped capacity is checked separately)
        if (openPositionCount >= policy.MaxOpenPositions)
            return $"MaxOpenPositions({policy.MaxOpenPositions}) reached ({openPositionCount} open)";

        // B-08 FIX: Daily drawdown uses realized losses only (not net PnL)
        decimal dailyLossR = 0m;
        int consecutiveLosses = 0;

        foreach (var o in todayOutcomes)
        {
            if (o.PnlR < 0) dailyLossR += o.PnlR; // Sum only negative PnlR
        }

        // Count consecutive losses from the most recent outcome backwards
        for (int i = todayOutcomes.Count - 1; i >= 0; i--)
        {
            if (todayOutcomes[i].OutcomeLabel == OutcomeLabel.LOSS)
                consecutiveLosses++;
            else
                break;
        }

        // Resolve effective limits — scalp gets its own thresholds when configured (non-zero).
        // This prevents 4 HTF losses from shutting down scalp entries for the rest of the day.
        int effectiveMaxConsecutive = isScalp && policy.ScalpMaxConsecutiveLossesPerDay > 0
            ? policy.ScalpMaxConsecutiveLossesPerDay
            : policy.MaxConsecutiveLossesPerDay;

        decimal effectiveMaxDailyDD = isScalp && policy.ScalpDailyMaxDrawdownPercent > 0
            ? policy.ScalpDailyMaxDrawdownPercent
            : policy.DailyMaxDrawdownPercent;

        // Daily drawdown check (PnlR is in risk units; convert to percent)
        decimal dailyDrawdownPct = -dailyLossR * policy.RiskPercentPerTrade;
        if (effectiveMaxDailyDD < decimal.MaxValue && dailyDrawdownPct >= effectiveMaxDailyDD)
            return $"DailyDrawdown({dailyDrawdownPct:F2}%) >= max({effectiveMaxDailyDD}%)";

        // Max consecutive losses
        if (consecutiveLosses >= effectiveMaxConsecutive)
            return $"ConsecutiveLosses({consecutiveLosses}) >= max({effectiveMaxConsecutive})";

        return null;
    }

    /// <summary>
    /// FR-2: Check scoped position capacity. Returns null if allowed, or a RejectReasonCode if blocked.
    /// Checks per-timeframe, per-direction, and global limits.
    /// </summary>
    public static (RejectReasonCode? Code, string? Reason) CheckScopedCapacity(
        StrategyParameters p,
        IReadOnlyList<SignalRecommendation> openSignals,
        string signalTimeframe,
        SignalDirection signalDirection)
    {
        var riskPolicy = p.ToRiskPolicy();

        // Global capacity check
        if (openSignals.Count >= riskPolicy.MaxOpenPositions)
            return (RejectReasonCode.SLOT_CAPACITY_REACHED,
                $"MaxOpenPositions({riskPolicy.MaxOpenPositions}) reached ({openSignals.Count} open)");

        // Per-timeframe capacity check
        if (p.MaxOpenPerTimeframe > 0)
        {
            int openOnTf = openSignals.Count(s => s.Timeframe == signalTimeframe);
            if (openOnTf >= p.MaxOpenPerTimeframe)
                return (RejectReasonCode.MAX_OPEN_PER_TIMEFRAME,
                    $"MaxOpenPerTimeframe({p.MaxOpenPerTimeframe}) reached for {signalTimeframe} ({openOnTf} open)");
        }

        // Per-direction capacity check
        if (p.MaxOpenPerDirection > 0)
        {
            int openInDir = openSignals.Count(s => s.Direction == signalDirection);
            if (openInDir >= p.MaxOpenPerDirection)
                return (RejectReasonCode.MAX_OPEN_PER_DIRECTION,
                    $"MaxOpenPerDirection({p.MaxOpenPerDirection}) reached for {signalDirection} ({openInDir} open)");
        }

        return (null, null);
    }

    private static RiskResult Blocked(string reason) => new(
        Allowed: false, EntryPrice: 0, StopLoss: 0, TakeProfit: 0,
        StopDistance: 0, RiskUsd: 0, RiskPercent: 0, BlockReason: reason);
}
