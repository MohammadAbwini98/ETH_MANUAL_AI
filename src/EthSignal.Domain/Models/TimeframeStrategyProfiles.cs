namespace EthSignal.Domain.Models;

public enum TimeframeProfileBucket
{
    Fast,
    Mid,
    Long
}

public sealed record TimeframeStrategyProfile
{
    public bool? AdaptiveParametersEnabled { get; init; }
    public bool? AdaptiveRetrospectiveEnabled { get; init; }
    public int? AdaptiveRetrospectiveMinOutcomes { get; init; }
    public int? AdaptiveRetrospectiveWindowSize { get; init; }
    public int? ConfidenceBuyThreshold { get; init; }
    public int? ConfidenceSellThreshold { get; init; }
    public int? ConflictingScoreGap { get; init; }
    public int? MaxRecoveredRegimeAgeBars { get; init; }
    public decimal? PullbackZonePct { get; init; }
    public decimal? MinAtrThreshold { get; init; }
    public decimal? StopAtrMultiplier { get; init; }
    public decimal? TargetRMultiple { get; init; }
    public decimal? LiveEntrySlippageBufferPct { get; init; }
    public decimal? MinStopDistancePct { get; init; }
    public decimal? ExitMaxStopDistancePct { get; init; }
    public decimal? ExitIntradayMinAtrTpMultiplier { get; init; }
    public decimal? ExitIntradayMaxAtrTpMultiplier { get; init; }
    public decimal? ExitHigherTfMinAtrTpMultiplier { get; init; }
    public decimal? ExitHigherTfMaxAtrTpMultiplier { get; init; }
    public decimal? FastTimeframeEntryAtrMultiplier { get; init; }
    public decimal? MidTimeframeEntryAtrMultiplier { get; init; }
    public decimal? LongTimeframeEntryAtrMultiplier { get; init; }
    public decimal? EntryAtrBufferCapPct { get; init; }
    public decimal? HighConfidenceEntryBufferMultiplier { get; init; }
    public decimal? LowConfidenceEntryBufferMultiplier { get; init; }
    public decimal? ExitStructureBufferAtrMultiplier { get; init; }
    public decimal? AdaptiveOverlayIntensity { get; init; }
    public decimal? MlMinWinProbability { get; init; }
    public NeutralRegimePolicy? NeutralRegimePolicy { get; init; }
    public int? ScalpConfidenceThreshold { get; init; }
    public decimal? ScalpMinAtr { get; init; }
    public decimal? ScalpStopAtrMultiplier { get; init; }
    public decimal? ScalpTargetRMultiple { get; init; }
    public int? ScalpCooldownBars { get; init; }

    public TimeframeStrategyProfile Merge(TimeframeStrategyProfile fallback) => new()
    {
        AdaptiveParametersEnabled = AdaptiveParametersEnabled ?? fallback.AdaptiveParametersEnabled,
        AdaptiveRetrospectiveEnabled = AdaptiveRetrospectiveEnabled ?? fallback.AdaptiveRetrospectiveEnabled,
        AdaptiveRetrospectiveMinOutcomes = AdaptiveRetrospectiveMinOutcomes ?? fallback.AdaptiveRetrospectiveMinOutcomes,
        AdaptiveRetrospectiveWindowSize = AdaptiveRetrospectiveWindowSize ?? fallback.AdaptiveRetrospectiveWindowSize,
        ConfidenceBuyThreshold = ConfidenceBuyThreshold ?? fallback.ConfidenceBuyThreshold,
        ConfidenceSellThreshold = ConfidenceSellThreshold ?? fallback.ConfidenceSellThreshold,
        ConflictingScoreGap = ConflictingScoreGap ?? fallback.ConflictingScoreGap,
        MaxRecoveredRegimeAgeBars = MaxRecoveredRegimeAgeBars ?? fallback.MaxRecoveredRegimeAgeBars,
        PullbackZonePct = PullbackZonePct ?? fallback.PullbackZonePct,
        MinAtrThreshold = MinAtrThreshold ?? fallback.MinAtrThreshold,
        StopAtrMultiplier = StopAtrMultiplier ?? fallback.StopAtrMultiplier,
        TargetRMultiple = TargetRMultiple ?? fallback.TargetRMultiple,
        LiveEntrySlippageBufferPct = LiveEntrySlippageBufferPct ?? fallback.LiveEntrySlippageBufferPct,
        MinStopDistancePct = MinStopDistancePct ?? fallback.MinStopDistancePct,
        ExitMaxStopDistancePct = ExitMaxStopDistancePct ?? fallback.ExitMaxStopDistancePct,
        ExitIntradayMinAtrTpMultiplier = ExitIntradayMinAtrTpMultiplier ?? fallback.ExitIntradayMinAtrTpMultiplier,
        ExitIntradayMaxAtrTpMultiplier = ExitIntradayMaxAtrTpMultiplier ?? fallback.ExitIntradayMaxAtrTpMultiplier,
        ExitHigherTfMinAtrTpMultiplier = ExitHigherTfMinAtrTpMultiplier ?? fallback.ExitHigherTfMinAtrTpMultiplier,
        ExitHigherTfMaxAtrTpMultiplier = ExitHigherTfMaxAtrTpMultiplier ?? fallback.ExitHigherTfMaxAtrTpMultiplier,
        FastTimeframeEntryAtrMultiplier = FastTimeframeEntryAtrMultiplier ?? fallback.FastTimeframeEntryAtrMultiplier,
        MidTimeframeEntryAtrMultiplier = MidTimeframeEntryAtrMultiplier ?? fallback.MidTimeframeEntryAtrMultiplier,
        LongTimeframeEntryAtrMultiplier = LongTimeframeEntryAtrMultiplier ?? fallback.LongTimeframeEntryAtrMultiplier,
        EntryAtrBufferCapPct = EntryAtrBufferCapPct ?? fallback.EntryAtrBufferCapPct,
        HighConfidenceEntryBufferMultiplier = HighConfidenceEntryBufferMultiplier ?? fallback.HighConfidenceEntryBufferMultiplier,
        LowConfidenceEntryBufferMultiplier = LowConfidenceEntryBufferMultiplier ?? fallback.LowConfidenceEntryBufferMultiplier,
        ExitStructureBufferAtrMultiplier = ExitStructureBufferAtrMultiplier ?? fallback.ExitStructureBufferAtrMultiplier,
        AdaptiveOverlayIntensity = AdaptiveOverlayIntensity ?? fallback.AdaptiveOverlayIntensity,
        MlMinWinProbability = MlMinWinProbability ?? fallback.MlMinWinProbability,
        NeutralRegimePolicy = NeutralRegimePolicy ?? fallback.NeutralRegimePolicy,
        ScalpConfidenceThreshold = ScalpConfidenceThreshold ?? fallback.ScalpConfidenceThreshold,
        ScalpMinAtr = ScalpMinAtr ?? fallback.ScalpMinAtr,
        ScalpStopAtrMultiplier = ScalpStopAtrMultiplier ?? fallback.ScalpStopAtrMultiplier,
        ScalpTargetRMultiple = ScalpTargetRMultiple ?? fallback.ScalpTargetRMultiple,
        ScalpCooldownBars = ScalpCooldownBars ?? fallback.ScalpCooldownBars
    };

    public StrategyParameters ApplyTo(StrategyParameters parameters) => parameters with
    {
        AdaptiveParametersEnabled = AdaptiveParametersEnabled ?? parameters.AdaptiveParametersEnabled,
        AdaptiveRetrospectiveEnabled = AdaptiveRetrospectiveEnabled ?? parameters.AdaptiveRetrospectiveEnabled,
        AdaptiveRetrospectiveMinOutcomes = AdaptiveRetrospectiveMinOutcomes ?? parameters.AdaptiveRetrospectiveMinOutcomes,
        AdaptiveRetrospectiveWindowSize = AdaptiveRetrospectiveWindowSize ?? parameters.AdaptiveRetrospectiveWindowSize,
        ConfidenceBuyThreshold = ConfidenceBuyThreshold ?? parameters.ConfidenceBuyThreshold,
        ConfidenceSellThreshold = ConfidenceSellThreshold ?? parameters.ConfidenceSellThreshold,
        ConflictingScoreGap = ConflictingScoreGap ?? parameters.ConflictingScoreGap,
        MaxRecoveredRegimeAgeBars = MaxRecoveredRegimeAgeBars ?? parameters.MaxRecoveredRegimeAgeBars,
        PullbackZonePct = PullbackZonePct ?? parameters.PullbackZonePct,
        MinAtrThreshold = MinAtrThreshold ?? parameters.MinAtrThreshold,
        StopAtrMultiplier = StopAtrMultiplier ?? parameters.StopAtrMultiplier,
        TargetRMultiple = TargetRMultiple ?? parameters.TargetRMultiple,
        LiveEntrySlippageBufferPct = LiveEntrySlippageBufferPct ?? parameters.LiveEntrySlippageBufferPct,
        MinStopDistancePct = MinStopDistancePct ?? parameters.MinStopDistancePct,
        ExitMaxStopDistancePct = ExitMaxStopDistancePct ?? parameters.ExitMaxStopDistancePct,
        ExitIntradayMinAtrTpMultiplier = ExitIntradayMinAtrTpMultiplier ?? parameters.ExitIntradayMinAtrTpMultiplier,
        ExitIntradayMaxAtrTpMultiplier = ExitIntradayMaxAtrTpMultiplier ?? parameters.ExitIntradayMaxAtrTpMultiplier,
        ExitHigherTfMinAtrTpMultiplier = ExitHigherTfMinAtrTpMultiplier ?? parameters.ExitHigherTfMinAtrTpMultiplier,
        ExitHigherTfMaxAtrTpMultiplier = ExitHigherTfMaxAtrTpMultiplier ?? parameters.ExitHigherTfMaxAtrTpMultiplier,
        FastTimeframeEntryAtrMultiplier = FastTimeframeEntryAtrMultiplier ?? parameters.FastTimeframeEntryAtrMultiplier,
        MidTimeframeEntryAtrMultiplier = MidTimeframeEntryAtrMultiplier ?? parameters.MidTimeframeEntryAtrMultiplier,
        LongTimeframeEntryAtrMultiplier = LongTimeframeEntryAtrMultiplier ?? parameters.LongTimeframeEntryAtrMultiplier,
        EntryAtrBufferCapPct = EntryAtrBufferCapPct ?? parameters.EntryAtrBufferCapPct,
        HighConfidenceEntryBufferMultiplier = HighConfidenceEntryBufferMultiplier ?? parameters.HighConfidenceEntryBufferMultiplier,
        LowConfidenceEntryBufferMultiplier = LowConfidenceEntryBufferMultiplier ?? parameters.LowConfidenceEntryBufferMultiplier,
        ExitStructureBufferAtrMultiplier = ExitStructureBufferAtrMultiplier ?? parameters.ExitStructureBufferAtrMultiplier,
        AdaptiveOverlayIntensity = AdaptiveOverlayIntensity ?? parameters.AdaptiveOverlayIntensity,
        MlMinWinProbability = MlMinWinProbability ?? parameters.MlMinWinProbability,
        NeutralRegimePolicy = NeutralRegimePolicy ?? parameters.NeutralRegimePolicy,
        ScalpConfidenceThreshold = ScalpConfidenceThreshold ?? parameters.ScalpConfidenceThreshold,
        ScalpMinAtr = ScalpMinAtr ?? parameters.ScalpMinAtr,
        ScalpStopAtrMultiplier = ScalpStopAtrMultiplier ?? parameters.ScalpStopAtrMultiplier,
        ScalpTargetRMultiple = ScalpTargetRMultiple ?? parameters.ScalpTargetRMultiple,
        ScalpCooldownBars = ScalpCooldownBars ?? parameters.ScalpCooldownBars
    };
}

public sealed record TimeframeStrategyProfileSet
{
    public TimeframeStrategyProfile M1 { get; init; } = new();
    public TimeframeStrategyProfile M5 { get; init; } = new();
    public TimeframeStrategyProfile M15 { get; init; } = new();
    public TimeframeStrategyProfile M30 { get; init; } = new();
    public TimeframeStrategyProfile H1 { get; init; } = new();
    public TimeframeStrategyProfile H4 { get; init; } = new();

    public TimeframeStrategyProfile Fast { get; init; } = new();

    public TimeframeStrategyProfile Mid { get; init; } = new();

    public TimeframeStrategyProfile Long { get; init; } = new();

    public static readonly TimeframeStrategyProfileSet Default = new();

    /// <summary>
    /// Optional preset that enables differentiated fast/mid/long behavior.
    /// This is not applied automatically so existing global parameter tuning
    /// remains the safe default until profiles are explicitly configured.
    /// </summary>
    public static readonly TimeframeStrategyProfileSet Recommended = new()
    {
        M1 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 52,
            ConfidenceSellThreshold = 52,
            PullbackZonePct = 0.0032m,
            MinAtrThreshold = 0.6m,
            StopAtrMultiplier = 1.8m,
            TargetRMultiple = 1.3m,
            LiveEntrySlippageBufferPct = 0.0024m,
            MinStopDistancePct = 0.0023m,
            EntryAtrBufferCapPct = 0.0026m,
            HighConfidenceEntryBufferMultiplier = 0.92m,
            LowConfidenceEntryBufferMultiplier = 1.14m,
            ExitStructureBufferAtrMultiplier = 0.18m,
            ScalpConfidenceThreshold = 62,
            ScalpMinAtr = 0.35m,
            ScalpStopAtrMultiplier = 1.1m,
            ScalpTargetRMultiple = 1.35m,
            ScalpCooldownBars = 6
        },
        M5 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 48,
            ConfidenceSellThreshold = 48,
            PullbackZonePct = 0.0038m,
            MinAtrThreshold = 0.7m,
            StopAtrMultiplier = 1.9m,
            TargetRMultiple = 1.4m,
            LiveEntrySlippageBufferPct = 0.0021m,
            MinStopDistancePct = 0.0025m,
            ExitIntradayMinAtrTpMultiplier = 1.2m,
            ExitIntradayMaxAtrTpMultiplier = 2.4m
        },
        M15 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 46,
            ConfidenceSellThreshold = 46,
            PullbackZonePct = 0.0041m,
            MinAtrThreshold = 0.85m,
            StopAtrMultiplier = 2.0m,
            TargetRMultiple = 1.55m
        },
        M30 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 45,
            ConfidenceSellThreshold = 45,
            PullbackZonePct = 0.0044m,
            MinAtrThreshold = 0.95m,
            StopAtrMultiplier = 2.1m,
            TargetRMultiple = 1.65m,
            ExitIntradayMinAtrTpMultiplier = 1.6m,
            ExitIntradayMaxAtrTpMultiplier = 3.2m
        },
        H1 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 43,
            ConfidenceSellThreshold = 43,
            PullbackZonePct = 0.005m,
            MinAtrThreshold = 1.1m,
            StopAtrMultiplier = 2.3m,
            TargetRMultiple = 1.8m,
            LiveEntrySlippageBufferPct = 0.0013m,
            ExitHigherTfMinAtrTpMultiplier = 2.2m,
            ExitHigherTfMaxAtrTpMultiplier = 4.0m
        },
        H4 = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 41,
            ConfidenceSellThreshold = 41,
            PullbackZonePct = 0.0056m,
            MinAtrThreshold = 1.2m,
            StopAtrMultiplier = 2.45m,
            TargetRMultiple = 1.95m,
            LiveEntrySlippageBufferPct = 0.0011m,
            MinStopDistancePct = 0.003m,
            ExitHigherTfMinAtrTpMultiplier = 2.4m,
            ExitHigherTfMaxAtrTpMultiplier = 4.4m,
            AdaptiveOverlayIntensity = 0.72m
        },
        Fast = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 50,
            ConfidenceSellThreshold = 50,
            PullbackZonePct = 0.0035m,
            MinAtrThreshold = 0.65m,
            StopAtrMultiplier = 1.85m,
            TargetRMultiple = 1.35m,
            LiveEntrySlippageBufferPct = 0.0022m,
            MinStopDistancePct = 0.0024m,
            ExitIntradayMinAtrTpMultiplier = 1.15m,
            ExitIntradayMaxAtrTpMultiplier = 2.35m,
            FastTimeframeEntryAtrMultiplier = 0.08m,
            EntryAtrBufferCapPct = 0.0024m,
            HighConfidenceEntryBufferMultiplier = 0.92m,
            LowConfidenceEntryBufferMultiplier = 1.12m,
            ExitStructureBufferAtrMultiplier = 0.18m,
            AdaptiveOverlayIntensity = 0.9m,
            ScalpConfidenceThreshold = 62,
            ScalpMinAtr = 0.35m,
            ScalpStopAtrMultiplier = 1.1m,
            ScalpTargetRMultiple = 1.35m,
            ScalpCooldownBars = 6
        },
        Mid = new TimeframeStrategyProfile
        {
            StopAtrMultiplier = 2.0m,
            TargetRMultiple = 1.55m,
            LiveEntrySlippageBufferPct = 0.0018m,
            ExitIntradayMinAtrTpMultiplier = 1.45m,
            ExitIntradayMaxAtrTpMultiplier = 3.0m,
            MidTimeframeEntryAtrMultiplier = 0.05m,
            EntryAtrBufferCapPct = 0.0022m,
            ExitStructureBufferAtrMultiplier = 0.14m
        },
        Long = new TimeframeStrategyProfile
        {
            ConfidenceBuyThreshold = 43,
            ConfidenceSellThreshold = 43,
            PullbackZonePct = 0.005m,
            MinAtrThreshold = 1.1m,
            StopAtrMultiplier = 2.35m,
            TargetRMultiple = 1.8m,
            LiveEntrySlippageBufferPct = 0.0012m,
            MinStopDistancePct = 0.0028m,
            ExitHigherTfMinAtrTpMultiplier = 2.2m,
            ExitHigherTfMaxAtrTpMultiplier = 4.2m,
            LongTimeframeEntryAtrMultiplier = 0.03m,
            EntryAtrBufferCapPct = 0.0020m,
            ExitStructureBufferAtrMultiplier = 0.12m,
            AdaptiveOverlayIntensity = 0.75m
        }
    };

    public TimeframeStrategyProfile GetProfile(TimeframeProfileBucket bucket) => bucket switch
    {
        TimeframeProfileBucket.Fast => Fast,
        TimeframeProfileBucket.Mid => Mid,
        TimeframeProfileBucket.Long => Long,
        _ => Mid
    };

    public TimeframeStrategyProfile GetProfile(string timeframe)
    {
        var bucketProfile = GetProfile(ResolveBucket(timeframe));
        var exactProfile = timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" => M1,
            "5m" => M5,
            "15m" => M15,
            "30m" => M30,
            "1h" => H1,
            "4h" => H4,
            _ => new TimeframeStrategyProfile()
        };

        return exactProfile.Merge(bucketProfile);
    }

    public static TimeframeProfileBucket ResolveBucket(string timeframe)
        => timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" or "5m" => TimeframeProfileBucket.Fast,
            "15m" or "30m" => TimeframeProfileBucket.Mid,
            "1h" or "4h" => TimeframeProfileBucket.Long,
            _ => TimeframeProfileBucket.Mid
        };
}
