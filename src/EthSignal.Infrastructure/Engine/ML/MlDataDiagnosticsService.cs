using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

public interface IMlDataDiagnosticsService
{
    Task<MlDataDiagnosticsReport> GetReportAsync(
        string symbol,
        string timeframe,
        decimal gateThreshold,
        CancellationToken ct = default);
}

public sealed class MlDataDiagnosticsService : IMlDataDiagnosticsService
{
    private const string AllTimeframesScope = "all";
    private const int TrainingSampleLimit = 600;
    private const int LiveSampleLimit = 240;
    private const int LiveWindowHours = 24;
    private const int CalibrationDays = 30;
    private const int CalibrationLimit = 400;
    private const int StaleUnlinkedHours = 6;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ValidScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL",
        "1m",
        "5m",
        "15m",
        "30m",
        "1h",
        "4h"
    };

    private readonly IMlDataDiagnosticsRepository _repo;
    private readonly IMlModelRepository _modelRepo;
    private readonly IBlockedSignalHistoryService _blockedSignalHistory;
    private readonly IGeneratedSignalHistoryService _generatedSignalHistory;
    private readonly ILogger<MlDataDiagnosticsService> _logger;

    // Short-lived cache so concurrent UI polls (health + diagnostics endpoints) don't
    // trigger duplicate DB queries and duplicate [MLDiagnostics] log entries.
    private MlDataDiagnosticsReport? _cachedReport;
    private DateTimeOffset _cacheExpiresAt;

    public MlDataDiagnosticsService(
        IMlDataDiagnosticsRepository repo,
        IMlModelRepository modelRepo,
        IBlockedSignalHistoryService blockedSignalHistory,
        IGeneratedSignalHistoryService generatedSignalHistory,
        ILogger<MlDataDiagnosticsService> logger)
    {
        _repo = repo;
        _modelRepo = modelRepo;
        _blockedSignalHistory = blockedSignalHistory;
        _generatedSignalHistory = generatedSignalHistory;
        _logger = logger;
    }

    public async Task<MlDataDiagnosticsReport> GetReportAsync(
        string symbol,
        string timeframe,
        decimal gateThreshold,
        CancellationToken ct = default)
    {
        var normalizedScope = NormalizeScope(timeframe);
        if (_cachedReport != null
            && string.Equals(_cachedReport.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_cachedReport.Timeframe, normalizedScope, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cachedReport;
        }

        var model = await _modelRepo.GetActiveModelAsync("outcome_predictor", ct);
        var currentFeatureVersion = MlFeatureExtractor.FeatureVersion;
        var timeframes = ResolveTimeframes(normalizedScope);
        var selectedFeatureVersions = new List<string>(timeframes.Count);
        var qualityRaw = EmptyQualityRaw();
        var activeCalibrationSamples = new List<MlPredictionOutcomeSample>();
        var fallbackCalibrationSamples = new List<MlPredictionOutcomeSample>();
        var trainingFeatures = new List<MlFeatureSnapshotSample>();
        var liveFeatures = new List<MlFeatureSnapshotSample>();

        foreach (var tf in timeframes)
        {
            var featureVersionStats = await _repo.GetFeatureVersionStatsAsync(symbol, tf, ct)
                ?? Array.Empty<MlFeatureVersionStats>();
            var selectedFeatureVersion = SelectFeatureVersion(featureVersionStats, currentFeatureVersion);
            selectedFeatureVersions.Add(selectedFeatureVersion);

            var tfQuality = await _repo.GetOutcomeQualityAsync(
                symbol,
                tf,
                selectedFeatureVersion,
                StaleUnlinkedHours,
                ct) ?? EmptyQualityRaw();
            qualityRaw = SumQuality(qualityRaw, tfQuality);

            activeCalibrationSamples.AddRange((await _repo.GetPredictionOutcomeSamplesAsync(
                symbol,
                tf,
                model?.ModelVersion,
                CalibrationDays,
                CalibrationLimit,
                ct)) ?? Array.Empty<MlPredictionOutcomeSample>());

            fallbackCalibrationSamples.AddRange((await _repo.GetPredictionOutcomeSamplesAsync(
                symbol,
                tf,
                modelVersion: null,
                CalibrationDays,
                CalibrationLimit,
                ct)) ?? Array.Empty<MlPredictionOutcomeSample>());

            trainingFeatures.AddRange((await _repo.GetLabeledFeatureSamplesAsync(
                symbol,
                tf,
                selectedFeatureVersion,
                TrainingSampleLimit,
                ct)) ?? Array.Empty<MlFeatureSnapshotSample>());

            liveFeatures.AddRange((await _repo.GetRecentFeatureSamplesAsync(
                symbol,
                tf,
                selectedFeatureVersion,
                LiveWindowHours,
                LiveSampleLimit,
                ct)) ?? Array.Empty<MlFeatureSnapshotSample>());
        }

        var featureVersion = ResolveFeatureVersion(selectedFeatureVersions, currentFeatureVersion);
        var isFeatureVersionFallback = selectedFeatureVersions.Any(v =>
            !string.Equals(v, currentFeatureVersion, StringComparison.OrdinalIgnoreCase));

        var blockedOutcomeTotals = await GetBlockedOutcomeTotalsAsync(symbol, normalizedScope, ct);
        var generatedOutcomeTotals = await GetGeneratedOutcomeTotalsAsync(symbol, normalizedScope, ct);
        qualityRaw = qualityRaw with
        {
            TotalOutcomes = qualityRaw.TotalOutcomes + blockedOutcomeTotals.TotalSignals + generatedOutcomeTotals.TotalSignals,
            Wins = qualityRaw.Wins + blockedOutcomeTotals.Wins + generatedOutcomeTotals.Wins,
            Losses = qualityRaw.Losses + blockedOutcomeTotals.Losses + generatedOutcomeTotals.Losses,
            Pending = qualityRaw.Pending
                + (blockedOutcomeTotals.TotalSignals - blockedOutcomeTotals.ResolvedSignals)
                + (generatedOutcomeTotals.TotalSignals - generatedOutcomeTotals.ResolvedSignals),
            Expired = qualityRaw.Expired + blockedOutcomeTotals.Expired + generatedOutcomeTotals.Expired,
            Ambiguous = qualityRaw.Ambiguous + blockedOutcomeTotals.Ambiguous + generatedOutcomeTotals.Ambiguous
        };

        var normalizedActiveCalibrationSamples = activeCalibrationSamples
            .OrderByDescending(s => s.PredictionTimeUtc)
            .Take(CalibrationLimit)
            .ToList();
        var normalizedFallbackCalibrationSamples = fallbackCalibrationSamples
            .OrderByDescending(s => s.PredictionTimeUtc)
            .Take(CalibrationLimit)
            .ToList();

        var calibrationSamples = normalizedActiveCalibrationSamples;
        var usesActiveModelOnly = true;
        if (normalizedActiveCalibrationSamples.Count < 20)
        {
            usesActiveModelOnly = false;
            calibrationSamples = normalizedFallbackCalibrationSamples;
        }

        var normalizedTrainingFeatures = trainingFeatures
            .OrderByDescending(f => f.TimestampUtc)
            .Take(TrainingSampleLimit)
            .ToList();
        var normalizedLiveFeatures = liveFeatures
            .OrderByDescending(f => f.TimestampUtc)
            .Take(LiveSampleLimit)
            .ToList();

        var modelSummary = BuildModelSummary(model);
        var labelQuality = BuildLabelQuality(qualityRaw);
        var classBalance = BuildClassBalance(qualityRaw);
        var calibration = BuildCalibration(
            calibrationSamples,
            normalizedActiveCalibrationSamples.Count,
            model?.ModelVersion,
            usesActiveModelOnly,
            gateThreshold);
        var featureDrift = BuildFeatureDrift(normalizedTrainingFeatures, normalizedLiveFeatures);

        var overallStatus = MaxStatus(
            labelQuality.Status,
            classBalance.Status,
            calibration.Status,
            featureDrift.Status);

        _logger.LogInformation(
            "[MLDiagnostics] {Symbol} {Timeframe} overall={Status} labeled={Labeled} calibSamples={CalibSamples} featureDrift={FeatureDrift}",
            symbol,
            normalizedScope,
            overallStatus,
            classBalance.LabeledSamples,
            calibration.SampleCount,
            featureDrift.Status);

        var report = new MlDataDiagnosticsReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Symbol = symbol,
            Timeframe = normalizedScope,
            OverallStatus = overallStatus,
            FeatureVersion = featureVersion,
            IsFeatureVersionFallback = isFeatureVersionFallback,
            Model = modelSummary,
            LabelQuality = labelQuality,
            ClassBalance = classBalance,
            Calibration = calibration,
            FeatureDrift = featureDrift
        };

        _cachedReport = report;
        _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);
        return report;
    }

    private static string NormalizeScope(string timeframe)
    {
        var normalized = string.Equals(timeframe, AllTimeframesScope, StringComparison.OrdinalIgnoreCase)
            ? "ALL"
            : timeframe?.Trim() ?? string.Empty;

        if (!ValidScopes.Contains(normalized))
            throw new ArgumentException($"Invalid scope '{timeframe}'", nameof(timeframe));

        return normalized;
    }

    private static IReadOnlyList<string> ResolveTimeframes(string normalizedScope)
        => string.Equals(normalizedScope, "ALL", StringComparison.OrdinalIgnoreCase)
            ? Timeframe.Signal.Where(tf => tf.Minutes >= 5).Select(tf => tf.Name).ToArray()
            : [normalizedScope];

    private static string ResolveFeatureVersion(IReadOnlyList<string> versions, string currentFeatureVersion)
    {
        if (versions.Count == 0)
            return currentFeatureVersion;

        var distinct = versions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
            return currentFeatureVersion;
        if (distinct.Count == 1)
            return distinct[0];
        if (distinct.Any(v => string.Equals(v, currentFeatureVersion, StringComparison.OrdinalIgnoreCase)))
            return currentFeatureVersion;
        return "mixed";
    }

    private static MlOutcomeQualityRaw EmptyQualityRaw()
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static MlOutcomeQualityRaw SumQuality(MlOutcomeQualityRaw left, MlOutcomeQualityRaw right)
        => new(
            left.TotalOutcomes + right.TotalOutcomes,
            left.Wins + right.Wins,
            left.Losses + right.Losses,
            left.Pending + right.Pending,
            left.Expired + right.Expired,
            left.Ambiguous + right.Ambiguous,
            left.InconsistentPnlLabels + right.InconsistentPnlLabels,
            left.ConflictingTpSlHits + right.ConflictingTpSlHits,
            left.ClosedTimestampMissing + right.ClosedTimestampMissing,
            left.TotalFeatureSnapshots + right.TotalFeatureSnapshots,
            left.LinkedFeatureSnapshots + right.LinkedFeatureSnapshots,
            left.LabeledFeatureSnapshots + right.LabeledFeatureSnapshots,
            left.PendingLinkSnapshots + right.PendingLinkSnapshots,
            left.StalePendingLinkSnapshots + right.StalePendingLinkSnapshots,
            left.ExpectedNoSignalSnapshots + right.ExpectedNoSignalSnapshots,
            left.MlFilteredSnapshots + right.MlFilteredSnapshots,
            left.OperationallyBlockedSnapshots + right.OperationallyBlockedSnapshots,
            left.TrainableFeatureSnapshots + right.TrainableFeatureSnapshots);

    private async Task<PerformanceStats> GetBlockedOutcomeTotalsAsync(
        string symbol,
        string normalizedScope,
        CancellationToken ct)
    {
        var firstPage = await _blockedSignalHistory.GetHistoryAsync(symbol, 1, 0, ct);
        if (firstPage.Total <= 0)
            return new PerformanceStats();

        var fullPage = firstPage.Total > firstPage.Signals.Count
            ? await _blockedSignalHistory.GetHistoryAsync(symbol, firstPage.Total, 0, ct)
            : firstPage;

        var items = string.Equals(normalizedScope, "ALL", StringComparison.OrdinalIgnoreCase)
            ? fullPage.Signals.Where(s => (Timeframe.ByNameOrDefault(s.Signal.Timeframe)?.Minutes ?? 0) >= 5).ToList()
            : fullPage.Signals.Where(s => string.Equals(s.Signal.Timeframe, normalizedScope, StringComparison.OrdinalIgnoreCase)).ToList();

        return OutcomeEvaluator.ComputeStats(items.Select(i => i.Outcome).ToList());
    }

    private async Task<PerformanceStats> GetGeneratedOutcomeTotalsAsync(
        string symbol,
        string normalizedScope,
        CancellationToken ct)
    {
        var firstPage = await _generatedSignalHistory.GetHistoryAsync(symbol, 1, 0, null, ct);
        if (firstPage.Total <= 0)
            return new PerformanceStats();

        var fullPage = firstPage.Total > firstPage.Signals.Count
            ? await _generatedSignalHistory.GetHistoryAsync(symbol, firstPage.Total, 0, null, ct)
            : firstPage;

        var items = string.Equals(normalizedScope, "ALL", StringComparison.OrdinalIgnoreCase)
            ? fullPage.Signals.Where(s => (Timeframe.ByNameOrDefault(s.Signal.Timeframe)?.Minutes ?? 0) >= 5).ToList()
            : fullPage.Signals.Where(s => string.Equals(s.Signal.Timeframe, normalizedScope, StringComparison.OrdinalIgnoreCase)).ToList();

        return OutcomeEvaluator.ComputeStats(items.Select(i => i.Outcome).ToList());
    }

    private static string SelectFeatureVersion(
        IReadOnlyList<MlFeatureVersionStats>? versions,
        string preferredVersion)
    {
        if (versions == null || versions.Count == 0)
            return preferredVersion;

        var preferred = versions.FirstOrDefault(v =>
            string.Equals(v.FeatureVersion, preferredVersion, StringComparison.OrdinalIgnoreCase));

        if (preferred != null && (preferred.TrainableFeatureSnapshots >= 50 || preferred.TotalSnapshots >= 100))
            return preferred.FeatureVersion;

        return versions
            .OrderByDescending(v => v.TrainableFeatureSnapshots)
            .ThenByDescending(v => v.LabeledFeatureSnapshots)
            .ThenByDescending(v => v.TotalSnapshots)
            .ThenByDescending(v => v.LatestCreatedAtUtc ?? DateTimeOffset.MinValue)
            .Select(v => v.FeatureVersion)
            .First();
    }

    private static MlLabelQualityDiagnostics BuildLabelQuality(MlOutcomeQualityRaw raw)
    {
        var labeled = raw.Wins + raw.Losses;
        var actionableSnapshots = raw.LinkedFeatureSnapshots + raw.PendingLinkSnapshots;
        var coveragePct = actionableSnapshots <= 0
            ? (raw.TotalFeatureSnapshots > 0 ? 100m : 0m)
            : Math.Round(100m * raw.LinkedFeatureSnapshots / actionableSnapshots, 1);

        if (raw.TotalOutcomes == 0 && raw.TotalFeatureSnapshots == 0)
        {
            return new MlLabelQualityDiagnostics
            {
                Status = MlDiagnosticsStatus.InsufficientData,
                TotalOutcomes = 0,
                LabeledOutcomes = 0,
                PendingOutcomes = 0,
                ExpiredOutcomes = 0,
                AmbiguousOutcomes = 0,
                InconsistentPnlLabels = 0,
                ConflictingTpSlHits = 0,
                ClosedTimestampMissing = 0,
                TotalFeatureSnapshots = 0,
                LinkedFeatureSnapshots = 0,
                LabeledFeatureSnapshots = 0,
                TrainableFeatureSnapshots = 0,
                PendingLinkSnapshots = 0,
                StalePendingLinkSnapshots = 0,
                ExpectedNoSignalSnapshots = 0,
                MlFilteredSnapshots = 0,
                OperationallyBlockedSnapshots = 0,
                LinkCoveragePct = 0m
            };
        }

        var severity = 0;
        if (raw.ConflictingTpSlHits > 0 || raw.InconsistentPnlLabels > 0)
            severity = Math.Max(severity, 3);
        if (raw.ClosedTimestampMissing > 0)
            severity = Math.Max(severity, 2);

        var pendingRatio = actionableSnapshots <= 0
            ? 0m
            : (decimal)raw.PendingLinkSnapshots / actionableSnapshots;
        if (raw.StalePendingLinkSnapshots > 10 || pendingRatio >= 0.25m)
            severity = Math.Max(severity, 2);
        else if (raw.PendingLinkSnapshots > 0 || raw.Ambiguous > 0 || raw.Expired > 0)
            severity = Math.Max(severity, 1);

        // Align diagnostics class-balance signal with the strict export count.
        // If the "trainable" row count (same definition as export_features.py
        // direct-linked query) is materially lower than the looser
        // labeled_feature_snapshots count, surface a warning so users can see
        // diagnostics and export disagree on what's trainable.
        if (raw.TrainableFeatureSnapshots > 0
            && raw.LabeledFeatureSnapshots - raw.TrainableFeatureSnapshots >= 50
            && severity < 1)
        {
            severity = 1;
        }

        return new MlLabelQualityDiagnostics
        {
            Status = SeverityToStatus(severity),
            TotalOutcomes = raw.TotalOutcomes,
            LabeledOutcomes = labeled,
            PendingOutcomes = raw.Pending,
            ExpiredOutcomes = raw.Expired,
            AmbiguousOutcomes = raw.Ambiguous,
            InconsistentPnlLabels = raw.InconsistentPnlLabels,
            ConflictingTpSlHits = raw.ConflictingTpSlHits,
            ClosedTimestampMissing = raw.ClosedTimestampMissing,
            TotalFeatureSnapshots = raw.TotalFeatureSnapshots,
            LinkedFeatureSnapshots = raw.LinkedFeatureSnapshots,
            LabeledFeatureSnapshots = raw.LabeledFeatureSnapshots,
            TrainableFeatureSnapshots = raw.TrainableFeatureSnapshots,
            PendingLinkSnapshots = raw.PendingLinkSnapshots,
            StalePendingLinkSnapshots = raw.StalePendingLinkSnapshots,
            ExpectedNoSignalSnapshots = raw.ExpectedNoSignalSnapshots,
            MlFilteredSnapshots = raw.MlFilteredSnapshots,
            OperationallyBlockedSnapshots = raw.OperationallyBlockedSnapshots,
            LinkCoveragePct = coveragePct
        };
    }

    private static MlClassBalanceDiagnostics BuildClassBalance(MlOutcomeQualityRaw raw)
    {
        var labeled = raw.Wins + raw.Losses;
        if (labeled <= 0)
        {
            return new MlClassBalanceDiagnostics
            {
                Status = MlDiagnosticsStatus.InsufficientData,
                LabeledSamples = 0,
                Wins = raw.Wins,
                Losses = raw.Losses,
                WinRate = 0m,
                LossToWinRatio = 0m,
                ReadyForTraining = false
            };
        }

        var winRate = (decimal)raw.Wins / labeled;
        var lossToWinRatio = raw.Wins > 0
            ? Math.Round((decimal)raw.Losses / raw.Wins, 2)
            : raw.Losses > 0 ? decimal.MaxValue : 0m;
        var ready = labeled >= 200 && raw.Wins >= 30 && raw.Losses >= 30;

        var severity = 0;
        if (!ready)
            severity = labeled < 100 || raw.Wins < 15 || raw.Losses < 15 ? 2 : 1;
        if (raw.Wins > 0 && (lossToWinRatio >= 3.0m || lossToWinRatio <= 0.33m))
            severity = Math.Max(severity, 2);
        else if (raw.Wins > 0 && (lossToWinRatio >= 2.0m || lossToWinRatio <= 0.50m))
            severity = Math.Max(severity, 1);

        return new MlClassBalanceDiagnostics
        {
            Status = SeverityToStatus(severity),
            LabeledSamples = labeled,
            Wins = raw.Wins,
            Losses = raw.Losses,
            WinRate = Math.Round(winRate, 4),
            LossToWinRatio = lossToWinRatio,
            ReadyForTraining = ready
        };
    }

    private static MlCalibrationDiagnostics BuildCalibration(
        IReadOnlyList<MlPredictionOutcomeSample> samples,
        int activeModelSampleCount,
        string? activeModelVersion,
        bool usesActiveModelOnly,
        decimal gateThreshold)
    {
        if (samples.Count < 20)
        {
            return new MlCalibrationDiagnostics
            {
                Status = MlDiagnosticsStatus.InsufficientData,
                SampleCount = samples.Count,
                ActiveModelSampleCount = activeModelSampleCount,
                ModelVersion = activeModelVersion,
                UsesActiveModelOnly = usesActiveModelOnly,
                GateThreshold = gateThreshold
            };
        }

        var predictedMean = samples.Average(s => s.CalibratedWinProbability);
        var actualWinRate = samples.Average(s => s.ActualWin ? 1m : 0m);
        var gap = actualWinRate - predictedMean;
        var brier = samples.Average(s =>
        {
            var actual = s.ActualWin ? 1m : 0m;
            var diff = s.CalibratedWinProbability - actual;
            return diff * diff;
        });
        var thresholdAvg = samples.Average(s => (decimal)s.RecommendedThreshold);

        var passed = samples.Where(s => s.CalibratedWinProbability >= gateThreshold).ToList();
        var failed = samples.Where(s => s.CalibratedWinProbability < gateThreshold).ToList();
        decimal? passWinRate = passed.Count > 0 ? passed.Average(s => s.ActualWin ? 1m : 0m) : null;
        decimal? failWinRate = failed.Count > 0 ? failed.Average(s => s.ActualWin ? 1m : 0m) : null;
        decimal? lift = passWinRate.HasValue && failWinRate.HasValue
            ? passWinRate.Value - failWinRate.Value
            : null;

        var severity = 0;
        if (brier >= 0.25m || Math.Abs(gap) >= 0.15m)
            severity = Math.Max(severity, 3);
        else if (brier >= 0.20m || Math.Abs(gap) >= 0.08m)
            severity = Math.Max(severity, 2);
        else if (brier >= 0.17m || Math.Abs(gap) >= 0.05m)
            severity = Math.Max(severity, 1);

        if (passed.Count >= 10 && failed.Count >= 10)
        {
            if (!lift.HasValue || lift.Value <= 0)
                severity = Math.Max(severity, 3);
            else if (lift.Value < 0.05m)
                severity = Math.Max(severity, 2);
            else if (lift.Value < 0.10m)
                severity = Math.Max(severity, 1);
        }

        if (!usesActiveModelOnly && activeModelSampleCount < 20)
            severity = Math.Max(severity, 1);

        return new MlCalibrationDiagnostics
        {
            Status = !usesActiveModelOnly && activeModelSampleCount < 20
                ? MlDiagnosticsStatus.Warning
                : SeverityToStatus(severity),
            SampleCount = samples.Count,
            ActiveModelSampleCount = activeModelSampleCount,
            ModelVersion = activeModelVersion,
            UsesActiveModelOnly = usesActiveModelOnly,
            GateThreshold = gateThreshold,
            PredictedMeanWin = Math.Round(predictedMean, 4),
            ActualWinRate = Math.Round(actualWinRate, 4),
            CalibrationGap = Math.Round(gap, 4),
            CalibrationGapAbs = Math.Round(Math.Abs(gap), 4),
            BrierScore = Math.Round(brier, 4),
            RecommendedThresholdAvg = Math.Round(thresholdAvg, 1),
            PassCount = passed.Count,
            PassWinRate = passWinRate.HasValue ? Math.Round(passWinRate.Value, 4) : null,
            FailCount = failed.Count,
            FailWinRate = failWinRate.HasValue ? Math.Round(failWinRate.Value, 4) : null,
            ThresholdLift = lift.HasValue ? Math.Round(lift.Value, 4) : null
        };
    }

    private static MlFeatureDriftDiagnostics BuildFeatureDrift(
        IReadOnlyList<MlFeatureSnapshotSample> trainingSamples,
        IReadOnlyList<MlFeatureSnapshotSample> liveSamples)
    {
        if (trainingSamples.Count < 50 || liveSamples.Count < 30)
        {
            return new MlFeatureDriftDiagnostics
            {
                Status = MlDiagnosticsStatus.InsufficientData,
                TrainingSampleCount = trainingSamples.Count,
                LiveSampleCount = liveSamples.Count,
                LiveWindowHours = LiveWindowHours
            };
        }

        var driftItems = new List<MlFeatureDriftItem>();
        foreach (var featureName in MlFeatureVector.FeatureNames)
        {
            var trainingValues = trainingSamples
                .Select(s => s.Features.TryGetValue(featureName, out var value) ? value : 0d)
                .ToArray();
            var liveValues = liveSamples
                .Select(s => s.Features.TryGetValue(featureName, out var value) ? value : 0d)
                .ToArray();

            var trainingMean = trainingValues.Average();
            var liveMean = liveValues.Average();
            var trainingStd = Math.Max(StandardDeviation(trainingValues, trainingMean), 1e-6);
            var meanShiftSigma = Math.Abs(liveMean - trainingMean) / trainingStd;
            var psi = ComputePsi(trainingValues, liveValues);

            driftItems.Add(new MlFeatureDriftItem
            {
                Feature = featureName,
                TrainingMean = Math.Round((decimal)trainingMean, 4),
                LiveMean = Math.Round((decimal)liveMean, 4),
                Psi = Math.Round((decimal)psi, 4),
                MeanShiftSigma = Math.Round((decimal)meanShiftSigma, 4)
            });
        }

        var avgPsi = driftItems.Average(i => i.Psi);
        var maxPsi = driftItems.Max(i => i.Psi);
        var avgShift = driftItems.Average(i => i.MeanShiftSigma);

        var severity = 0;
        if (maxPsi >= 0.25m || avgPsi >= 0.15m)
            severity = 3;
        else if (maxPsi >= 0.10m || avgPsi >= 0.05m)
            severity = 2;
        else if (maxPsi >= 0.05m || avgShift >= 1.0m)
            severity = 1;

        return new MlFeatureDriftDiagnostics
        {
            Status = SeverityToStatus(severity),
            TrainingSampleCount = trainingSamples.Count,
            LiveSampleCount = liveSamples.Count,
            LiveWindowHours = LiveWindowHours,
            AveragePsi = Math.Round(avgPsi, 4),
            MaxPsi = Math.Round(maxPsi, 4),
            AverageMeanShiftSigma = Math.Round(avgShift, 4),
            TopFeatures = driftItems
                .OrderByDescending(i => i.Psi)
                .ThenByDescending(i => i.MeanShiftSigma)
                .Take(5)
                .ToList()
        };
    }

    private static MlModelDiagnosticsSummary BuildModelSummary(MlModelMetadata? model)
    {
        var currentFeatureCount = MlFeatureVector.FeatureNames.Count;
        if (model == null)
        {
            return new MlModelDiagnosticsSummary
            {
                CurrentFeatureCount = currentFeatureCount,
                UsesCurrentFeatureContract = false
            };
        }

        model = MlModelMetadataSidecarReader.Enrich(model);

        return new MlModelDiagnosticsSummary
        {
            ModelVersion = model.ModelVersion,
            TrainingSampleCount = model.TrainingSampleCount,
            FeatureCount = model.FeatureCount,
            CurrentFeatureCount = currentFeatureCount,
            UsesCurrentFeatureContract = model.FeatureCount == currentFeatureCount,
            AucRoc = model.AucRoc > 0 ? model.AucRoc : null,
            BrierScore = model.BrierScore > 0 ? model.BrierScore : null,
            ExpectedCalibrationError = model.ExpectedCalibrationError > 0 ? model.ExpectedCalibrationError : null,
            LogLoss = model.LogLoss > 0 ? model.LogLoss : null,
            ActivatedAtUtc = model.ActivatedAtUtc
        };
    }

    private static double StandardDeviation(double[] values, double mean)
    {
        if (values.Length <= 1) return 0d;
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        return Math.Sqrt(Math.Max(variance, 0d));
    }

    private static double ComputePsi(double[] training, double[] live)
    {
        if (training.Length == 0 || live.Length == 0)
            return 0d;

        var min = Math.Min(training.Min(), live.Min());
        var max = Math.Max(training.Max(), live.Max());
        if (Math.Abs(max - min) < 1e-9)
            return 0d;

        const int bins = 10;
        var width = (max - min) / bins;
        var psi = 0d;

        for (int i = 0; i < bins; i++)
        {
            var lower = min + width * i;
            var upper = i == bins - 1 ? max + 1e-9 : lower + width;

            var trainingPct = Math.Max(1e-6, training.Count(v => v >= lower && v < upper) / (double)training.Length);
            var livePct = Math.Max(1e-6, live.Count(v => v >= lower && v < upper) / (double)live.Length);
            var contribution = (livePct - trainingPct) * Math.Log(livePct / trainingPct);
            if (double.IsNaN(contribution) || double.IsInfinity(contribution))
                continue;

            psi += Math.Clamp(contribution, -5d, 5d);
        }

        if (double.IsNaN(psi) || double.IsInfinity(psi))
            return 0d;

        return psi;
    }

    private static string MaxStatus(params string[] statuses)
    {
        var severity = statuses.Select(StatusSeverity).DefaultIfEmpty(0).Max();
        return SeverityToStatus(severity);
    }

    private static string SeverityToStatus(int severity) => severity switch
    {
        >= 3 => MlDiagnosticsStatus.Critical,
        >= 1 => MlDiagnosticsStatus.Warning,
        _ => MlDiagnosticsStatus.Healthy
    };

    private static int StatusSeverity(string status) => status switch
    {
        MlDiagnosticsStatus.Critical => 3,
        MlDiagnosticsStatus.Warning => 2,
        MlDiagnosticsStatus.InsufficientData => 1,
        _ => 0
    };
}
