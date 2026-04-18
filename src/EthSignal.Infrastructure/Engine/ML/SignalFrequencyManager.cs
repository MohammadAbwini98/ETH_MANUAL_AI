using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Dynamically adjusts confidence thresholds based on market conditions and ML predictions.
/// Implements dynamic threshold lookup (Model M3) and neutral-regime ML gating.
/// </summary>
public sealed class SignalFrequencyManager
{
    private readonly ILogger<SignalFrequencyManager> _logger;

    // Threshold lookup table: (regime, adxBucket, session) -> recommended threshold
    private readonly Dictionary<string, int> _thresholdLookup = new();
    private int _lastRecommendedBuyThreshold;
    private int _lastRecommendedSellThreshold;

    public SignalFrequencyManager(ILogger<SignalFrequencyManager> logger)
    {
        _logger = logger;
        InitializeDefaultLookup();
    }

    /// <summary>
    /// Compute the recommended confidence threshold for current market conditions.
    /// Applies max-delta smoothing to prevent threshold whiplash.
    /// </summary>
    public ThresholdRecommendation GetDynamicThreshold(
        Regime regime, decimal adx, decimal atrPct, int hourOfDay,
        decimal recentWinRate, string timeframe, StrategyParameters p)
    {
        if (!p.MlDynamicThresholdsEnabled)
        {
            return new ThresholdRecommendation
            {
                BuyThreshold = p.ConfidenceBuyThreshold,
                SellThreshold = p.ConfidenceSellThreshold,
                Source = "static"
            };
        }

        string adxBucket = adx switch
        {
            >= 30 => "high",
            >= 20 => "mid",
            _ => "low"
        };

        string session = hourOfDay switch
        {
            >= 7 and < 13 => "london",
            >= 13 and < 16 => "overlap",
            >= 16 and < 21 => "ny",
            _ => "asia"
        };

        var normalizedTimeframe = string.IsNullOrWhiteSpace(timeframe) ? "any" : timeframe.Trim().ToLowerInvariant();
        var keyCandidates = new[]
        {
            $"{normalizedTimeframe}_{regime}_{adxBucket}_{session}",
            $"{normalizedTimeframe}_{regime}_{adxBucket}_any",
            $"any_{regime}_{adxBucket}_{session}",
            $"any_{regime}_{adxBucket}_any"
        };
        int baseThreshold = keyCandidates
            .Select(key => _thresholdLookup.TryGetValue(key, out var threshold) ? (int?)threshold : null)
            .FirstOrDefault(threshold => threshold.HasValue)
            ?? 65;

        // Clamp to configured range
        baseThreshold = Math.Clamp(baseThreshold, p.MlDynamicThresholdMin, p.MlDynamicThresholdMax);

        // Smooth with max delta constraint
        int buyThreshold = ApplyMaxDelta(baseThreshold, _lastRecommendedBuyThreshold, p.MlDynamicThresholdMaxDelta);
        int sellThreshold = ApplyMaxDelta(baseThreshold, _lastRecommendedSellThreshold, p.MlDynamicThresholdMaxDelta);

        _lastRecommendedBuyThreshold = buyThreshold;
        _lastRecommendedSellThreshold = sellThreshold;

        return new ThresholdRecommendation
        {
            BuyThreshold = buyThreshold,
            SellThreshold = sellThreshold,
            Source = $"dynamic:{keyCandidates[0]}",
            BaseThreshold = baseThreshold
        };
    }

    /// <summary>
    /// Check if a signal in NEUTRAL regime should be allowed based on ML prediction.
    /// Implements NeutralRegimePolicy.AllowMlGatedEntriesInNeutral.
    /// </summary>
    public bool ShouldAllowNeutralEntry(
        MlPrediction? prediction, RegimeResult regime, StrategyParameters p)
    {
        if (p.NeutralRegimePolicy != NeutralRegimePolicy.AllowMlGatedEntriesInNeutral)
            return false;

        if (prediction == null || p.MlMode == MlMode.DISABLED)
            return false;

        // Require ML P(WIN) > 0.60 and regime score >= 3
        return prediction.CalibratedWinProbability >= 0.60m
            && regime.RegimeScore >= 3;
    }

    /// <summary>
    /// Check if mandatory gate relaxation is allowed for this prediction.
    /// Only when MlOverrideMandatoryGates=true and P(WIN) is significantly high.
    /// </summary>
    public bool CanRelaxMandatoryGates(MlPrediction? prediction, StrategyParameters p)
    {
        if (!p.MlOverrideMandatoryGates || prediction == null || p.MlMode != MlMode.ACTIVE)
            return false;

        // Require P(WIN) > MlMinWinProbability + 0.15
        return prediction.CalibratedWinProbability >= p.MlMinWinProbability + 0.15m;
    }

    /// <summary>Load a trained threshold lookup table from JSON.</summary>
    /// <remarks>
    /// Trained lookup tables may use keys without a timeframe prefix (e.g.
    /// "BULLISH_high_overlap"). Since runtime lookups include the timeframe,
    /// entries without a prefix are replicated across all known timeframes
    /// plus "any", matching the InitializeDefaultLookup pattern.
    /// </remarks>
    public void LoadLookupTable(Dictionary<string, int> lookup)
    {
        _thresholdLookup.Clear();
        var timeframes = new[] { "1m", "5m", "15m", "30m", "1h", "4h", "any" };
        foreach (var kv in lookup)
        {
            // If key already has a timeframe prefix (contains at least 3 underscores
            // or starts with a known tf), store as-is; otherwise replicate.
            bool hasPrefix = timeframes.Any(tf => kv.Key.StartsWith(tf + "_", StringComparison.OrdinalIgnoreCase));
            if (hasPrefix)
            {
                _thresholdLookup[kv.Key] = kv.Value;
            }
            else
            {
                foreach (var tf in timeframes)
                    _thresholdLookup[$"{tf}_{kv.Key}"] = kv.Value;
            }
        }
        _logger.LogInformation("Loaded {Count} threshold lookup entries (expanded to {Total})", lookup.Count, _thresholdLookup.Count);
    }

    public void ResetToDefaults()
    {
        InitializeDefaultLookup();
        _logger.LogInformation("Reset threshold lookup entries to defaults");
    }

    private void InitializeDefaultLookup()
    {
        _thresholdLookup.Clear();
        // Default thresholds based on SRS Section 12.2 analysis.
        // Store them per timeframe and with "any" fallbacks so trained lookup
        // tables can refine by timeframe without breaking older defaults.
        AddDefault("BULLISH", "high", "overlap", 50);
        AddDefault("BULLISH", "high", "london", 50);
        AddDefault("BULLISH", "high", "ny", 55);
        AddDefault("BULLISH", "high", "asia", 60);
        AddDefault("BULLISH", "mid", "any", 60);
        AddDefault("BULLISH", "mid", "overlap", 55);
        AddDefault("BULLISH", "low", "any", 70);

        AddDefault("BEARISH", "high", "overlap", 50);
        AddDefault("BEARISH", "high", "london", 55);
        AddDefault("BEARISH", "high", "ny", 55);
        AddDefault("BEARISH", "high", "asia", 60);
        AddDefault("BEARISH", "mid", "any", 60);
        AddDefault("BEARISH", "mid", "overlap", 55);
        AddDefault("BEARISH", "low", "any", 70);

        AddDefault("NEUTRAL", "high", "any", 70);
        AddDefault("NEUTRAL", "mid", "any", 75);
        AddDefault("NEUTRAL", "low", "any", 80);
        AddDefault("NEUTRAL", "low", "asia", 85);

        _lastRecommendedBuyThreshold = 65;
        _lastRecommendedSellThreshold = 65;
    }

    private void AddDefault(string regime, string adxBucket, string session, int threshold)
    {
        foreach (var timeframe in new[] { "1m", "5m", "15m", "30m", "1h", "4h", "any" })
            _thresholdLookup[$"{timeframe}_{regime}_{adxBucket}_{session}"] = threshold;
    }

    private static int ApplyMaxDelta(int target, int current, int maxDelta)
    {
        if (current == 0) return target; // First call
        int delta = target - current;
        if (Math.Abs(delta) <= maxDelta) return target;
        return current + Math.Sign(delta) * maxDelta;
    }
}

public sealed record ThresholdRecommendation
{
    public int BuyThreshold { get; init; }
    public int SellThreshold { get; init; }
    public required string Source { get; init; }
    public int BaseThreshold { get; init; }
}
