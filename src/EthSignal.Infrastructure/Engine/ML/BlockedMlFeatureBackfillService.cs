using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

public sealed record BlockedMlFeatureBackfillResult
{
    public int Candidates { get; init; }
    public int Backfilled { get; init; }
    public int SkippedMlDisabled { get; init; }
    public int SkippedUnknownDirection { get; init; }
    public int SkippedMissingCandle { get; init; }
    public int SkippedMissingIndicators { get; init; }
}

public interface IBlockedMlFeatureBackfillService
{
    Task<BlockedMlFeatureBackfillResult> BackfillAsync(
        string symbol,
        CancellationToken ct = default);
}

public sealed class BlockedMlFeatureBackfillService : IBlockedMlFeatureBackfillService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDecisionAuditRepository _decisionAuditRepo;
    private readonly ICandleRepository _candleRepo;
    private readonly IIndicatorRepository _indicatorRepo;
    private readonly IRegimeRepository _regimeRepo;
    private readonly ISignalRepository _signalRepo;
    private readonly IMlFeatureRepository _mlFeatureRepo;
    private readonly ILogger<BlockedMlFeatureBackfillService> _logger;

    public BlockedMlFeatureBackfillService(
        IDecisionAuditRepository decisionAuditRepo,
        ICandleRepository candleRepo,
        IIndicatorRepository indicatorRepo,
        IRegimeRepository regimeRepo,
        ISignalRepository signalRepo,
        IMlFeatureRepository mlFeatureRepo,
        ILogger<BlockedMlFeatureBackfillService> logger)
    {
        _decisionAuditRepo = decisionAuditRepo;
        _candleRepo = candleRepo;
        _indicatorRepo = indicatorRepo;
        _regimeRepo = regimeRepo;
        _signalRepo = signalRepo;
        _mlFeatureRepo = mlFeatureRepo;
        _logger = logger;
    }

    public async Task<BlockedMlFeatureBackfillResult> BackfillAsync(
        string symbol,
        CancellationToken ct = default)
    {
        var candidates = await _decisionAuditRepo.GetBlockedMlBackfillCandidatesAsync(symbol, ct);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("[MLTrainer] No historical blocked ML feature gaps found for {Symbol}", symbol);
            return new BlockedMlFeatureBackfillResult();
        }

        var result = new BlockedMlFeatureBackfillResult
        {
            Candidates = candidates.Count
        };

        foreach (var candidate in candidates)
        {
            var parameters = ParseParameters(
                candidate.EffectiveRuntimeParametersJson,
                candidate.ParameterSetId);
            if (parameters.MlMode == MlMode.DISABLED)
            {
                result = result with { SkippedMlDisabled = result.SkippedMlDisabled + 1 };
                continue;
            }

            if (!TryParseDirection(candidate, out var direction))
            {
                result = result with { SkippedUnknownDirection = result.SkippedUnknownDirection + 1 };
                continue;
            }

            var timeframe = Timeframe.ByNameOrDefault(candidate.Timeframe);
            var candle = await LoadCandleAsync(candidate, timeframe, ct);
            if (candle == null)
            {
                result = result with { SkippedMissingCandle = result.SkippedMissingCandle + 1 };
                continue;
            }

            var featureVector = await BuildFeatureVectorAsync(
                candidate,
                parameters,
                timeframe,
                direction,
                candle,
                ct);
            if (featureVector == null)
            {
                result = result with { SkippedMissingIndicators = result.SkippedMissingIndicators + 1 };
                continue;
            }

            await _mlFeatureRepo.InsertAsync(
                featureVector,
                signalId: null,
                featureVersion: MlFeatureExtractor.FeatureVersion,
                linkStatus: MlEvaluationLinkStatus.OperationallyBlocked,
                ct: ct);

            result = result with { Backfilled = result.Backfilled + 1 };
        }

        _logger.LogInformation(
            "[MLTrainer] Historical blocked ML feature backfill | Symbol={Symbol} | Candidates={Candidates} | Backfilled={Backfilled} | " +
            "SkippedMlDisabled={SkippedMlDisabled} | SkippedUnknownDirection={SkippedUnknownDirection} | " +
            "SkippedMissingCandle={SkippedMissingCandle} | SkippedMissingIndicators={SkippedMissingIndicators}",
            symbol,
            result.Candidates,
            result.Backfilled,
            result.SkippedMlDisabled,
            result.SkippedUnknownDirection,
            result.SkippedMissingCandle,
            result.SkippedMissingIndicators);

        return result;
    }

    private async Task<MlFeatureVector?> BuildFeatureVectorAsync(
        DecisionMlBackfillCandidate candidate,
        StrategyParameters parameters,
        Timeframe timeframe,
        SignalDirection direction,
        RichCandle candle,
        CancellationToken ct)
    {
        var lookbackStart = candidate.BarTimeUtc.AddTicks(-timeframe.Duration.Ticks * 300);
        var snapshots = (await _indicatorRepo.GetSnapshotsAsync(
            candidate.Symbol,
            timeframe.Name,
            lookbackStart,
            candidate.BarTimeUtc.Add(timeframe.Duration),
            ct)).ToList();

        var recentSnaps = snapshots
            .Where(s => s.CandleOpenTimeUtc <= candidate.BarTimeUtc)
            .OrderByDescending(s => s.CandleOpenTimeUtc)
            .Take(300)
            .ToList();

        var currentSnap = recentSnaps.FirstOrDefault(s => s.CandleOpenTimeUtc == candidate.BarTimeUtc)
            ?? BuildSnapshotFallback(candidate, candle);
        if (currentSnap == null)
            return null;

        if (recentSnaps.All(s => s.CandleOpenTimeUtc != currentSnap.CandleOpenTimeUtc))
            recentSnaps.Insert(0, currentSnap);

        var prevSnap = recentSnaps.Skip(1).FirstOrDefault();
        var recentSignalsWindowStart = candidate.BarTimeUtc.AddMinutes(-(10 * Math.Max(1, timeframe.Minutes)));
        var recentSignals = await _signalRepo.GetRecentSignalsAsync(
            candidate.Symbol,
            timeframe.Name,
            recentSignalsWindowStart,
            candidate.BarTimeUtc,
            10,
            ct);

        var latestSignal = await _signalRepo.GetLatestSignalBeforeAsync(
            candidate.Symbol,
            candidate.SignalTimeUtc,
            ct);
        var barsSinceLastSignal = latestSignal == null
            ? 0
            : Math.Max(0, (int)((candidate.BarTimeUtc - latestSignal.SignalTimeUtc).TotalMinutes / Math.Max(1, timeframe.Minutes)));

        var recentOutcomes = await _signalRepo.GetOutcomesAsync(
            candidate.Symbol,
            candidate.DecisionTimeUtc.AddDays(-7),
            candidate.DecisionTimeUtc,
            parameters.StrategyVersion,
            ct);
        var last20Outcomes = recentOutcomes
            .OrderByDescending(o => o.EvaluatedAtUtc)
            .Take(20)
            .ToList();

        var regime = await _regimeRepo.GetLatestBeforeAsync(
            candidate.Symbol,
            candidate.DecisionTimeUtc,
            ct)
            ?? BuildFallbackRegime(candidate);

        return MlFeatureExtractor.Extract(
            currentSnap,
            prevSnap,
            recentSnaps,
            candle,
            regime,
            direction,
            candidate.ConfidenceScore,
            last20Outcomes,
            barsSinceLastSignal,
            timeframe,
            recentSignals,
            btcContext: null,
            evaluationId: candidate.EvaluationId);
    }

    private async Task<RichCandle?> LoadCandleAsync(
        DecisionMlBackfillCandidate candidate,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var candles = await _candleRepo.GetClosedCandlesInRangeAsync(
            timeframe,
            candidate.Symbol,
            candidate.BarTimeUtc,
            candidate.BarTimeUtc.Add(timeframe.Duration),
            ct);
        return candles.FirstOrDefault(c => c.OpenTime == candidate.BarTimeUtc);
    }

    private static StrategyParameters ParseParameters(string? json, string? parameterSetId)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StrategyParameters>(json, JsonOpts);
                if (parsed != null)
                    return parsed;
            }
            catch
            {
                // Fall through to defaults.
            }
        }

        return StrategyParameters.Default with
        {
            StrategyVersion = string.IsNullOrWhiteSpace(parameterSetId)
                ? StrategyParameters.Default.StrategyVersion
                : parameterSetId
        };
    }

    private static bool TryParseDirection(
        DecisionMlBackfillCandidate candidate,
        out SignalDirection direction)
    {
        var raw = candidate.CandidateDirectionRaw ?? candidate.DecisionTypeRaw;
        return Enum.TryParse(raw, true, out direction)
            && direction is SignalDirection.BUY or SignalDirection.SELL;
    }

    private static IndicatorSnapshot? BuildSnapshotFallback(
        DecisionMlBackfillCandidate candidate,
        RichCandle candle)
    {
        Dictionary<string, decimal>? indicators;
        try
        {
            indicators = JsonSerializer.Deserialize<Dictionary<string, decimal>>(candidate.IndicatorsJson, JsonOpts);
        }
        catch
        {
            return null;
        }

        if (indicators == null || indicators.Count == 0)
            return null;

        return new IndicatorSnapshot
        {
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            CandleOpenTimeUtc = candidate.BarTimeUtc,
            Ema20 = GetIndicator(indicators, "ema20"),
            Ema50 = GetIndicator(indicators, "ema50"),
            Rsi14 = GetIndicator(indicators, "rsi14"),
            Macd = 0m,
            MacdSignal = 0m,
            MacdHist = GetIndicator(indicators, "macd_hist"),
            Atr14 = GetIndicator(indicators, "atr14"),
            Adx14 = GetIndicator(indicators, "adx14"),
            PlusDi = GetIndicator(indicators, "plus_di"),
            MinusDi = GetIndicator(indicators, "minus_di"),
            VolumeSma20 = GetIndicator(indicators, "volume_sma20"),
            Vwap = GetIndicator(indicators, "vwap"),
            Spread = GetIndicator(indicators, "spread"),
            CloseMid = GetIndicator(indicators, "close_mid"),
            MidHigh = candle.MidHigh,
            MidLow = candle.MidLow,
            IsProvisional = false
        };
    }

    private static RegimeResult BuildFallbackRegime(DecisionMlBackfillCandidate candidate)
    {
        var regime = Enum.TryParse<Regime>(candidate.RegimeRaw, true, out var parsed)
            ? parsed
            : Regime.NEUTRAL;
        return new RegimeResult
        {
            Symbol = candidate.Symbol,
            CandleOpenTimeUtc = candidate.BarTimeUtc,
            Regime = regime,
            RegimeScore = 0,
            TriggeredConditions = [],
            DisqualifyingConditions = []
        };
    }

    private static decimal GetIndicator(IReadOnlyDictionary<string, decimal> indicators, string key)
        => indicators.TryGetValue(key, out var value) ? value : 0m;
}
