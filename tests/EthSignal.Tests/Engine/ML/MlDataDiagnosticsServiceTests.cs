using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class MlDataDiagnosticsServiceTests
{
    [Fact]
    public async Task GetReportAsync_HealthyData_ReturnsHealthyReport()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();
        repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", "5m", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MlFeatureVersionStats("v3.0", 240, 220, 220, DateTimeOffset.UtcNow)]);

        repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlOutcomeQualityRaw(
                TotalOutcomes: 260,
                Wins: 120,
                Losses: 100,
                Pending: 20,
                Expired: 0,
                Ambiguous: 0,
                InconsistentPnlLabels: 0,
                ConflictingTpSlHits: 0,
                ClosedTimestampMissing: 0,
                TotalFeatureSnapshots: 240,
                LinkedFeatureSnapshots: 240,
                LabeledFeatureSnapshots: 220,
                PendingLinkSnapshots: 0,
                StalePendingLinkSnapshots: 0,
                ExpectedNoSignalSnapshots: 12,
                MlFilteredSnapshots: 5,
                OperationallyBlockedSnapshots: 3));

        repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", "model-v2", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildPredictionSamples(120, 0.62m, 0.41m));
        repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(200, 10d, 0.02d));
        repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(120, 10d, 0.02d));

        modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlModelMetadata
            {
                Id = 1,
                ModelType = "outcome_predictor",
                ModelVersion = "model-v2",
                FilePath = "/tmp/model-v2.onnx",
                FileFormat = "onnx",
                TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-30),
                TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-1),
                TrainingSampleCount = 220,
                FeatureCount = 58,
                FeatureListJson = "[]",
                AucRoc = 0.82m,
                BrierScore = 0.16m,
                ExpectedCalibrationError = 0.04m,
                LogLoss = 0.51m,
                FoldMetricsJson = "[]",
                FeatureImportanceJson = "{}",
                Status = MlModelStatus.Active,
                ActivatedAtUtc = DateTimeOffset.UtcNow.AddHours(-6)
            });

        var service = new MlDataDiagnosticsService(
            repo.Object,
            modelRepo.Object,
            CreateBlockedHistoryService(),
            CreateGeneratedHistoryService(),
            NullLogger<MlDataDiagnosticsService>.Instance);

        var report = await service.GetReportAsync("ETHUSD", "all", 0.55m, CancellationToken.None);

        report.OverallStatus.Should().Be(MlDiagnosticsStatus.Healthy);
        report.ClassBalance.ReadyForTraining.Should().BeTrue();
        report.Calibration.Status.Should().Be(MlDiagnosticsStatus.Healthy);
        report.FeatureDrift.Status.Should().Be(MlDiagnosticsStatus.Healthy);
        report.Model.ExpectedCalibrationError.Should().Be(0.04m);
    }

    [Fact]
    public async Task GetReportAsync_BadLabelsAndDrift_ReturnsCriticalReport()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();
        repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", "5m", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MlFeatureVersionStats("v3.0", 90, 40, 40, DateTimeOffset.UtcNow)]);

        repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlOutcomeQualityRaw(
                TotalOutcomes: 80,
                Wins: 18,
                Losses: 22,
                Pending: 30,
                Expired: 6,
                Ambiguous: 4,
                InconsistentPnlLabels: 3,
                ConflictingTpSlHits: 1,
                ClosedTimestampMissing: 2,
                TotalFeatureSnapshots: 90,
                LinkedFeatureSnapshots: 50,
                LabeledFeatureSnapshots: 40,
                PendingLinkSnapshots: 40,
                StalePendingLinkSnapshots: 18,
                ExpectedNoSignalSnapshots: 0,
                MlFilteredSnapshots: 0,
                OperationallyBlockedSnapshots: 0));

        repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", "model-v1", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildPredictionSamples(60, 0.58m, 0.57m, invertThresholdEdge: true));
        repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(120, 10d, 0.02d));
        repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(90, 16d, 0.4d));

        modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlModelMetadata
            {
                Id = 2,
                ModelType = "outcome_predictor",
                ModelVersion = "model-v1",
                FilePath = "/tmp/model-v1.onnx",
                FileFormat = "onnx",
                TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-90),
                TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-45),
                TrainingSampleCount = 80,
                FeatureCount = 58,
                FeatureListJson = "[]",
                AucRoc = 0.60m,
                BrierScore = 0.24m,
                ExpectedCalibrationError = 0.13m,
                LogLoss = 0.72m,
                FoldMetricsJson = "[]",
                FeatureImportanceJson = "{}",
                Status = MlModelStatus.Active
            });

        var service = new MlDataDiagnosticsService(
            repo.Object,
            modelRepo.Object,
            CreateBlockedHistoryService(),
            CreateGeneratedHistoryService(),
            NullLogger<MlDataDiagnosticsService>.Instance);

        var report = await service.GetReportAsync("ETHUSD", "all", 0.55m, CancellationToken.None);

        report.OverallStatus.Should().Be(MlDiagnosticsStatus.Critical);
        report.LabelQuality.Status.Should().Be(MlDiagnosticsStatus.Critical);
        report.ClassBalance.Status.Should().NotBe(MlDiagnosticsStatus.Healthy);
        report.FeatureDrift.Status.Should().Be(MlDiagnosticsStatus.Critical);
    }

    [Fact]
    public async Task GetReportAsync_LegacyMetaFile_DerivesCalibrationMetricsWithoutRetraining()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();
        var tempDir = Directory.CreateTempSubdirectory("ml-diag-legacy-meta");
        var modelPath = Path.Combine(tempDir.FullName, "outcome_predictor_vlegacy.onnx");
        var metaPath = Path.Combine(tempDir.FullName, "outcome_predictor_vlegacy_meta.json");

        try
        {
            await File.WriteAllTextAsync(modelPath, string.Empty);
            await File.WriteAllTextAsync(metaPath, """
                {
                  "avg_auc_roc": 0.81,
                  "avg_brier_score": 0.17,
                  "fold_metrics": [
                    { "expected_calibration_error": 0.06, "log_loss": 0.48 },
                    { "expected_calibration_error": 0.08, "log_loss": 0.52 }
                  ]
                }
                """);

            repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", "5m", It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MlFeatureVersionStats("v3.0", 0, 0, 0, DateTimeOffset.UtcNow)]);

            repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MlOutcomeQualityRaw(
                    TotalOutcomes: 0,
                    Wins: 0,
                    Losses: 0,
                    Pending: 0,
                    Expired: 0,
                    Ambiguous: 0,
                    InconsistentPnlLabels: 0,
                    ConflictingTpSlHits: 0,
                    ClosedTimestampMissing: 0,
                    TotalFeatureSnapshots: 0,
                    LinkedFeatureSnapshots: 0,
                    LabeledFeatureSnapshots: 0,
                    PendingLinkSnapshots: 0,
                    StalePendingLinkSnapshots: 0,
                    ExpectedNoSignalSnapshots: 0,
                    MlFilteredSnapshots: 0,
                    OperationallyBlockedSnapshots: 0));
            repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlPredictionOutcomeSample>());
            repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());
            repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());

            modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MlModelMetadata
                {
                    Id = 9,
                    ModelType = "outcome_predictor",
                    ModelVersion = "legacy",
                    FilePath = modelPath,
                    FileFormat = "onnx",
                    TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-20),
                    TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-19),
                    TrainingSampleCount = 300,
                    FeatureCount = 58,
                    FeatureListJson = "[]",
                    AucRoc = 0m,
                    BrierScore = 0m,
                    ExpectedCalibrationError = 0m,
                    LogLoss = 0m,
                    FoldMetricsJson = "[]",
                    FeatureImportanceJson = "{}",
                    Status = MlModelStatus.Active
                });

            var service = new MlDataDiagnosticsService(
                repo.Object,
                modelRepo.Object,
                CreateBlockedHistoryService(),
                CreateGeneratedHistoryService(),
                NullLogger<MlDataDiagnosticsService>.Instance);

            var report = await service.GetReportAsync("ETHUSD", "5m", 0.55m, CancellationToken.None);

            report.Model.AucRoc.Should().Be(0.81m);
            report.Model.BrierScore.Should().Be(0.17m);
            report.Model.ExpectedCalibrationError.Should().Be(0.07m);
            report.Model.LogLoss.Should().Be(0.50m);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetReportAsync_ThinActiveCalibrationSamples_UsesFallbackButDoesNotMarkCritical()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();
        repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", "5m", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MlFeatureVersionStats("v3.0", 200, 60, 60, DateTimeOffset.UtcNow)]);

        repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlOutcomeQualityRaw(
                TotalOutcomes: 300,
                Wins: 120,
                Losses: 120,
                Pending: 40,
                Expired: 10,
                Ambiguous: 10,
                InconsistentPnlLabels: 0,
                ConflictingTpSlHits: 0,
                ClosedTimestampMissing: 0,
                TotalFeatureSnapshots: 200,
                LinkedFeatureSnapshots: 70,
                LabeledFeatureSnapshots: 60,
                PendingLinkSnapshots: 2,
                StalePendingLinkSnapshots: 0,
                ExpectedNoSignalSnapshots: 100,
                MlFilteredSnapshots: 20,
                OperationallyBlockedSnapshots: 8));

        repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", "model-v3", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildPredictionSamples(12, 0.74m, 0.35m, invertThresholdEdge: true));
        repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildPredictionSamples(80, 0.74m, 0.35m, invertThresholdEdge: true));
        repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(120, 10d, 0.02d));
        repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(90, 10d, 0.02d));

        modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlModelMetadata
            {
                Id = 3,
                ModelType = "outcome_predictor",
                ModelVersion = "model-v3",
                FilePath = "/tmp/model-v3.onnx",
                FileFormat = "onnx",
                TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-60),
                TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-2),
                TrainingSampleCount = 320,
                FeatureCount = 58,
                FeatureListJson = "[]",
                AucRoc = 0.78m,
                BrierScore = 0.18m,
                ExpectedCalibrationError = 0.07m,
                LogLoss = 0.55m,
                FoldMetricsJson = "[]",
                FeatureImportanceJson = "{}",
                Status = MlModelStatus.Active
            });

        var service = new MlDataDiagnosticsService(
            repo.Object,
            modelRepo.Object,
            CreateBlockedHistoryService(),
            CreateGeneratedHistoryService(),
            NullLogger<MlDataDiagnosticsService>.Instance);

        var report = await service.GetReportAsync("ETHUSD", "all", 0.55m, CancellationToken.None);

        report.Calibration.ActiveModelSampleCount.Should().Be(12);
        report.Calibration.UsesActiveModelOnly.Should().BeFalse();
        report.Calibration.Status.Should().Be(MlDiagnosticsStatus.Warning);
    }

    private static IReadOnlyList<MlPredictionOutcomeSample> BuildPredictionSamples(
        int count,
        decimal passProbability,
        decimal failProbability,
        bool invertThresholdEdge = false)
    {
        var samples = new List<MlPredictionOutcomeSample>(count);
        for (var i = 0; i < count; i++)
        {
            var passed = i % 2 == 0;
            var actualWin = invertThresholdEdge ? !passed : passed;
            samples.Add(new MlPredictionOutcomeSample(
                PredictionTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-i),
                CalibratedWinProbability: passed ? passProbability : failProbability,
                RecommendedThreshold: passed ? 63 : 58,
                ModelVersion: "model-v",
                ActualWin: actualWin));
        }

        return samples;
    }

    [Fact]
    public async Task GetReportAsync_TrainableVsLabeledDivergence_RaisesLabelQualityWarning()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();
        repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", "5m", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MlFeatureVersionStats("v3.0", 500, 414, 131, DateTimeOffset.UtcNow)]);

        // Labeled feature snapshots far exceeds the strict "trainable" count
        // — this is exactly the April 11 situation (414 labeled vs 131 direct
        // linked). The diagnostics service should at minimum raise a WARNING.
        repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MlOutcomeQualityRaw(
                TotalOutcomes: 500,
                Wins: 220,
                Losses: 194,
                Pending: 40,
                Expired: 20,
                Ambiguous: 5,
                InconsistentPnlLabels: 0,
                ConflictingTpSlHits: 0,
                ClosedTimestampMissing: 0,
                TotalFeatureSnapshots: 500,
                LinkedFeatureSnapshots: 500,
                LabeledFeatureSnapshots: 414,
                PendingLinkSnapshots: 0,
                StalePendingLinkSnapshots: 0,
                ExpectedNoSignalSnapshots: 0,
                MlFilteredSnapshots: 0,
                OperationallyBlockedSnapshots: 0,
                TrainableFeatureSnapshots: 131));

        repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MlPredictionOutcomeSample>());
        repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(200, 10d, 0.02d));
        repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", "5m", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFeatureSamples(120, 10d, 0.02d));

        modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MlModelMetadata?)null);

        var service = new MlDataDiagnosticsService(
            repo.Object,
            modelRepo.Object,
            CreateBlockedHistoryService(),
            CreateGeneratedHistoryService(),
            NullLogger<MlDataDiagnosticsService>.Instance);

        var report = await service.GetReportAsync("ETHUSD", "all", 0.55m, CancellationToken.None);

        report.LabelQuality.TrainableFeatureSnapshots.Should().Be(131);
        report.LabelQuality.LabeledFeatureSnapshots.Should().Be(414);
        report.LabelQuality.Status.Should().NotBe(MlDiagnosticsStatus.Healthy);
    }

    [Fact]
    public async Task GetReportAsync_AllTimeframes_IncludesBlockedAndGeneratedHistoryOutcomesInLabeledCounts()
    {
        var repo = new Mock<IMlDataDiagnosticsRepository>();
        var modelRepo = new Mock<IMlModelRepository>();

        foreach (var tf in new[] { "1m", "5m" })
        {
            repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", tf, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MlFeatureVersionStats("v3.0", 100, 10, 10, DateTimeOffset.UtcNow)]);
            repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MlOutcomeQualityRaw(
                    TotalOutcomes: 4,
                    Wins: 1,
                    Losses: 2,
                    Pending: 1,
                    Expired: 0,
                    Ambiguous: 0,
                    InconsistentPnlLabels: 0,
                    ConflictingTpSlHits: 0,
                    ClosedTimestampMissing: 0,
                    TotalFeatureSnapshots: 20,
                    LinkedFeatureSnapshots: 10,
                    LabeledFeatureSnapshots: 3,
                    PendingLinkSnapshots: 1,
                    StalePendingLinkSnapshots: 0,
                    ExpectedNoSignalSnapshots: 0,
                    MlFilteredSnapshots: 0,
                    OperationallyBlockedSnapshots: 0,
                    TrainableFeatureSnapshots: 3));
            repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", tf, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlPredictionOutcomeSample>());
            repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());
            repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());
        }

        foreach (var tf in new[] { "15m", "30m", "1h", "4h" })
        {
            repo.Setup(r => r.GetFeatureVersionStatsAsync("ETHUSD", tf, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureVersionStats>());
            repo.Setup(r => r.GetOutcomeQualityAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MlOutcomeQualityRaw(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            repo.Setup(r => r.GetPredictionOutcomeSamplesAsync("ETHUSD", tf, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlPredictionOutcomeSample>());
            repo.Setup(r => r.GetLabeledFeatureSamplesAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());
            repo.Setup(r => r.GetRecentFeatureSamplesAsync("ETHUSD", tf, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<MlFeatureSnapshotSample>());
        }

        modelRepo.Setup(r => r.GetActiveModelAsync("outcome_predictor", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MlModelMetadata?)null);

        var blockedService = CreateBlockedHistoryService(
            new BlockedSignalHistoryPage
            {
                Signals =
                [
                    MakeBlockedSignal("1m", OutcomeLabel.WIN, 1.4m),
                    MakeBlockedSignal("5m", OutcomeLabel.LOSS, -1.0m),
                    MakeBlockedSignal("15m", OutcomeLabel.EXPIRED, 0.1m)
                ],
                Stats = new PerformanceStats
                {
                    TotalSignals = 3,
                    ResolvedSignals = 3,
                    Wins = 1,
                    Losses = 1,
                    Expired = 1,
                    Ambiguous = 0,
                    WinRate = 50m,
                    AverageR = 0.17m,
                    ProfitFactor = 1.4m,
                    TotalPnlR = 0.5m
                },
                Total = 3,
                Page = 1,
                PageSize = 3
            });

        var generatedService = CreateGeneratedHistoryService(
            new GeneratedSignalHistoryPage
            {
                Signals =
                [
                    MakeGeneratedSignal("1m", OutcomeLabel.WIN, 1.8m),
                    MakeGeneratedSignal("30m", OutcomeLabel.LOSS, -1.0m),
                    MakeGeneratedSignal("1h", OutcomeLabel.PENDING, 0m)
                ],
                Stats = new PerformanceStats
                {
                    TotalSignals = 3,
                    ResolvedSignals = 2,
                    Wins = 1,
                    Losses = 1,
                    Expired = 0,
                    Ambiguous = 0,
                    WinRate = 50m,
                    AverageR = 0.40m,
                    ProfitFactor = 1.8m,
                    TotalPnlR = 0.8m
                },
                Total = 3,
                Page = 1,
                PageSize = 3
            });

        var service = new MlDataDiagnosticsService(
            repo.Object,
            modelRepo.Object,
            blockedService,
            generatedService,
            NullLogger<MlDataDiagnosticsService>.Instance);

        var report = await service.GetReportAsync("ETHUSD", "all", 0.55m, CancellationToken.None);

        report.Timeframe.Should().Be("ALL");
        // "ALL" scope now uses 5m+ timeframes only — 1m signals are excluded from ML labeling.
        // Base quality: only 5m is queried (not 1m) → Wins=1, Losses=2, Pending=1, Trainable=3
        // Blocked 5m+: "5m" LOSS + "15m" EXPIRED (1m WIN excluded) → +1 Loss, +1 Expired
        // Generated 5m+: "30m" LOSS + "1h" PENDING (1m WIN excluded) → +1 Loss, +1 Pending
        report.ClassBalance.LabeledSamples.Should().Be(5);   // 1 win + 4 losses
        report.ClassBalance.Wins.Should().Be(1);
        report.ClassBalance.Losses.Should().Be(4);
        report.LabelQuality.ExpiredOutcomes.Should().Be(1);
        report.LabelQuality.PendingOutcomes.Should().Be(2);
        report.LabelQuality.TrainableFeatureSnapshots.Should().Be(3);
    }

    private static IReadOnlyList<MlFeatureSnapshotSample> BuildFeatureSamples(int count, double baseValue, double shiftedValue)
    {
        var samples = new List<MlFeatureSnapshotSample>(count);
        for (var i = 0; i < count; i++)
        {
            var features = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var feature in MlFeatureVector.FeatureNames)
                features[feature] = feature == "ema20" ? baseValue + shiftedValue * (i % 5) : 1d + (i % 3) * 0.01d;

            samples.Add(new MlFeatureSnapshotSample(
                EvaluationId: Guid.NewGuid(),
                TimestampUtc: DateTimeOffset.UtcNow.AddMinutes(-i),
                Features: features));
        }

        return samples;
    }

    private static IBlockedSignalHistoryService CreateBlockedHistoryService(BlockedSignalHistoryPage? page = null)
    {
        var mock = new Mock<IBlockedSignalHistoryService>();
        mock.Setup(s => s.GetHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page ?? new BlockedSignalHistoryPage
            {
                Signals = [],
                Stats = new PerformanceStats(),
                Total = 0,
                Page = 1,
                PageSize = 1
            });
        return mock.Object;
    }

    private static IGeneratedSignalHistoryService CreateGeneratedHistoryService(GeneratedSignalHistoryPage? page = null)
    {
        var mock = new Mock<IGeneratedSignalHistoryService>();
        mock.Setup(s => s.GetHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page ?? new GeneratedSignalHistoryPage
            {
                Signals = [],
                Stats = new PerformanceStats(),
                Total = 0,
                Page = 1,
                PageSize = 1
            });
        return mock.Object;
    }

    private static BlockedSignalWithOutcome MakeBlockedSignal(string timeframe, OutcomeLabel label, decimal pnlR)
        => new()
        {
            Signal = new BlockedSignalRecommendation
            {
                SignalId = Guid.NewGuid(),
                Symbol = "ETHUSD",
                Timeframe = timeframe,
                SignalTimeUtc = DateTimeOffset.UtcNow,
                DecisionTimeUtc = DateTimeOffset.UtcNow,
                BarTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                Direction = SignalDirection.BUY,
                LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
                BlockReason = "blocked",
                Origin = DecisionOrigin.CLOSED_BAR,
                SourceMode = SourceMode.LIVE,
                Regime = Regime.BULLISH,
                StrategyVersion = "v3.1",
                Reasons = ["blocked"],
                EntryPrice = 100m,
                TpPrice = 101m,
                SlPrice = 99m,
                RiskPercent = 0.5m,
                RiskUsd = 10m,
                ConfidenceScore = 70,
                ExpiryBars = 60,
                ExpiryTimeUtc = DateTimeOffset.UtcNow.AddHours(5)
            },
            Outcome = new SignalOutcome
            {
                SignalId = Guid.NewGuid(),
                BarsObserved = 10,
                TpHit = label == OutcomeLabel.WIN,
                SlHit = label == OutcomeLabel.LOSS,
                OutcomeLabel = label,
                PnlR = pnlR,
                MfePrice = 101m,
                MaePrice = 99m,
                MfeR = 1m,
                MaeR = 1m,
                ClosedAtUtc = label == OutcomeLabel.PENDING ? null : DateTimeOffset.UtcNow.AddMinutes(10)
            }
        };

    private static GeneratedSignalWithOutcome MakeGeneratedSignal(string timeframe, OutcomeLabel label, decimal pnlR)
        => new()
        {
            Signal = new GeneratedSignalRecommendation
            {
                SignalId = Guid.NewGuid(),
                EvaluationId = Guid.NewGuid(),
                Symbol = "ETHUSD",
                Timeframe = timeframe,
                SignalTimeUtc = DateTimeOffset.UtcNow,
                DecisionTimeUtc = DateTimeOffset.UtcNow,
                BarTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                Direction = SignalDirection.SELL,
                LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
                Origin = DecisionOrigin.PARTIAL_RUNNING,
                SourceMode = SourceMode.LIVE,
                Regime = Regime.BEARISH,
                StrategyVersion = "v3.1",
                Reasons = ["generated"],
                EntryPrice = 100m,
                TpPrice = 99m,
                SlPrice = 101m,
                RiskPercent = 0.5m,
                RiskUsd = 10m,
                ConfidenceScore = 74,
                ExpiryBars = 60,
                ExpiryTimeUtc = DateTimeOffset.UtcNow.AddHours(5),
                ExitModel = "STRUCTURE_FULL",
                ExitExplanation = "generated"
            },
            Outcome = new SignalOutcome
            {
                SignalId = Guid.NewGuid(),
                BarsObserved = 10,
                TpHit = label == OutcomeLabel.WIN,
                SlHit = label == OutcomeLabel.LOSS,
                OutcomeLabel = label,
                PnlR = pnlR,
                MfePrice = 101m,
                MaePrice = 99m,
                MfeR = 1m,
                MaeR = 1m,
                ClosedAtUtc = label == OutcomeLabel.PENDING ? null : DateTimeOffset.UtcNow.AddMinutes(10)
            }
        };

    [Fact]
    public async Task GetReportAsync_InvalidScope_ThrowsArgumentException()
    {
        var service = new MlDataDiagnosticsService(
            Mock.Of<IMlDataDiagnosticsRepository>(),
            Mock.Of<IMlModelRepository>(),
            CreateBlockedHistoryService(),
            CreateGeneratedHistoryService(),
            NullLogger<MlDataDiagnosticsService>.Instance);

        var act = () => service.GetReportAsync("ETHUSD", "INVALID", 0.55m, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid scope*");
    }
}
