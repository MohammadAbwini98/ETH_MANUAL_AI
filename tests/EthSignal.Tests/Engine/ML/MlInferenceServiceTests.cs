using System.Globalization;
using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class MlInferenceServiceTests
{
    private static MlInferenceService CreateService(IMlModelRepository? repo = null)
    {
        var mockRepo = repo ?? new Mock<IMlModelRepository>().Object;
        var logger = new Mock<ILogger<MlInferenceService>>().Object;
        return new MlInferenceService(mockRepo, logger);
    }

    private static MlFeatureVector MakeFeatures(int ruleBasedScore = 70) => new()
    {
        EvaluationId = Guid.NewGuid(),
        Timestamp = DateTimeOffset.UtcNow,
        Symbol = "ETHUSD",
        Timeframe = "5m",
        RuleBasedScore = ruleBasedScore,
        Adx14 = 25m,
        Rsi14 = 55m
    };

    private static MlModelMetadata MakeModelMetadata(string filePath, string version = "v1.0") => new()
    {
        Id = 1,
        ModelType = "outcome_predictor",
        ModelVersion = version,
        FilePath = filePath,
        FileFormat = "onnx",
        TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-7),
        TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-1),
        TrainingSampleCount = 1000,
        FeatureCount = 58,
        FeatureListJson = "[]",
        FoldMetricsJson = "[]",
        FeatureImportanceJson = "{}",
        Status = MlModelStatus.Active
    };

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string GetModelPath(string fileName)
    {
        return Path.Combine(GetRepoRoot(), "ml", "models", fileName);
    }

    [Fact]
    public void IsReady_False_Initially()
    {
        var svc = CreateService();
        svc.IsReady.Should().BeFalse();
    }

    [Fact]
    public void ActiveModelVersion_Null_Initially()
    {
        var svc = CreateService();
        svc.ActiveModelVersion.Should().BeNull();
    }

    [Fact]
    public async Task Predict_Returns_HeuristicResult_When_NoModel()
    {
        // When no DB model is registered, heuristic fallback activates — predictions still flow
        var mockRepo = new Mock<IMlModelRepository>();
        mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MlModelMetadata?)null);

        var svc = CreateService(mockRepo.Object);
        await svc.LoadActiveModelAsync();

        // Heuristic fallback is active — IsReady=true and Predict returns a result
        svc.IsReady.Should().BeTrue();
        svc.ActiveModelVersion.Should().Be("heuristic-v1");
        var result = svc.Predict(MakeFeatures(), MlMode.SHADOW);
        result.Should().NotBeNull();
        result.RawWinProbability.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task LoadActiveModelAsync_NoModel_ActivatesHeuristicFallback()
    {
        var mockRepo = new Mock<IMlModelRepository>();
        mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MlModelMetadata?)null);

        var svc = CreateService(mockRepo.Object);
        await svc.LoadActiveModelAsync();

        // Heuristic fallback keeps IsReady=true so SHADOW mode always produces predictions
        svc.IsReady.Should().BeTrue();
        svc.ActiveModelVersion.Should().Be("heuristic-v1");
    }

    [Fact]
    public async Task LoadActiveModelAsync_MissingFile_ActivatesHeuristicFallback()
    {
        var mockRepo = new Mock<IMlModelRepository>();
        mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeModelMetadata("/nonexistent/model.onnx"));

        var svc = CreateService(mockRepo.Object);
        await svc.LoadActiveModelAsync();

        // Missing file triggers heuristic fallback, not a dead state
        svc.IsReady.Should().BeTrue();
        svc.ActiveModelVersion.Should().Be("heuristic-v1");
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var svc = CreateService();
        var act = () => svc.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task LoadActiveModelAsync_FallbackInference_WorksWithOnnxFile()
    {
        // Create a temp file to simulate model existence
        var tmpFile = Path.GetTempFileName();
        try
        {
            var mockRepo = new Mock<IMlModelRepository>();
            mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(tmpFile, "v1.0-test"));

            var svc = CreateService(mockRepo.Object);
            await svc.LoadActiveModelAsync();

            // With fallback inference (no ONNX runtime installed), it should still be ready
            svc.IsReady.Should().BeTrue();
            svc.ActiveModelVersion.Should().Be("v1.0-test");

            // Predict should work via fallback heuristic
            var prediction = svc.Predict(MakeFeatures(ruleBasedScore: 80), MlMode.SHADOW);
            prediction.Should().NotBeNull();
            prediction!.RawWinProbability.Should().BeInRange(0, 1);
            prediction.ModelVersion.Should().Be("v1.0-test");
            prediction.Mode.Should().Be(MlMode.SHADOW);
            prediction.InferenceLatencyUs.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Predict_Shadow_Sets_IsActive_False()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var mockRepo = new Mock<IMlModelRepository>();
            mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(tmpFile));

            var svc = CreateService(mockRepo.Object);
            await svc.LoadActiveModelAsync();

            var prediction = svc.Predict(MakeFeatures(), MlMode.SHADOW);

            prediction.Should().NotBeNull();
            prediction!.IsActive.Should().BeFalse();
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Predict_Active_Sets_IsActive_True()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var mockRepo = new Mock<IMlModelRepository>();
            mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(tmpFile));

            var svc = CreateService(mockRepo.Object);
            await svc.LoadActiveModelAsync();

            var prediction = svc.Predict(MakeFeatures(), MlMode.ACTIVE);

            prediction.Should().NotBeNull();
            prediction!.IsActive.Should().BeTrue();
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Predict_ClampsValues_ToValidRanges()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var mockRepo = new Mock<IMlModelRepository>();
            mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(tmpFile));

            var svc = CreateService(mockRepo.Object);
            await svc.LoadActiveModelAsync();

            var prediction = svc.Predict(MakeFeatures(), MlMode.ACTIVE);

            prediction.Should().NotBeNull();
            prediction!.RawWinProbability.Should().BeInRange(0, 1);
            prediction.PredictionConfidence.Should().BeInRange(0, 100);
            prediction.RecommendedThreshold.Should().BeInRange(40, 90);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Predict_Computes_ExpectedValueR()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var mockRepo = new Mock<IMlModelRepository>();
            mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(tmpFile));

            var svc = CreateService(mockRepo.Object);
            await svc.LoadActiveModelAsync();

            var prediction = svc.Predict(MakeFeatures(ruleBasedScore: 80), MlMode.ACTIVE);

            prediction.Should().NotBeNull();
            // EV = P(WIN)*1.5 - P(LOSS)*1.0
            decimal expectedEv = prediction!.RawWinProbability * 1.5m
                                 - (1m - prediction.RawWinProbability) * 1.0m;
            prediction.ExpectedValueR.Should().BeApproximately(expectedEv, 0.01m);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task LoadActiveModelAsync_Applies_Calibration_Artifact()
    {
        var sourceModelPath = GetModelPath("outcome_predictor_v20260409_013337.onnx");
        var calibrationPath = GetModelPath("recalibrator_v20260409_013340.json");
        var tempDir = Directory.CreateTempSubdirectory("ml-calibration-raw");
        var isolatedModelPath = Path.Combine(tempDir.FullName, Path.GetFileName(sourceModelPath));

        try
        {
            File.Copy(sourceModelPath, isolatedModelPath, overwrite: true);

            var repoWithArtifacts = new Mock<IMlModelRepository>();
            repoWithArtifacts.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(sourceModelPath, "v20260409_013337"));

            var repoWithoutArtifacts = new Mock<IMlModelRepository>();
            repoWithoutArtifacts.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeModelMetadata(isolatedModelPath, "v20260409_013337"));

            using var calibratedSvc = CreateService(repoWithArtifacts.Object);
            using var rawSvc = CreateService(repoWithoutArtifacts.Object);

            await calibratedSvc.LoadActiveModelAsync();
            await rawSvc.LoadActiveModelAsync();

            var rawPrediction = rawSvc.Predict(MakeFeatures(ruleBasedScore: 80), MlMode.ACTIVE);
            var calibratedPrediction = calibratedSvc.Predict(MakeFeatures(ruleBasedScore: 80), MlMode.ACTIVE);

            rawPrediction.Should().NotBeNull();
            calibratedPrediction.Should().NotBeNull();

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(calibrationPath));
            var table = doc.RootElement.GetProperty("calibration_table");
            var expectedCalibrated = InterpolateCalibrationTable(
                table,
                (float)rawPrediction!.RawWinProbability);

            // CalibratedWinProbability stores the post-calibration value; RawWinProbability is always uncalibrated.
            calibratedPrediction!.CalibratedWinProbability.Should().BeApproximately(expectedCalibrated, 0.0001m);
            // PredictionConfidence = max(calibrated, 1-calibrated)*100 — reflects binary classification confidence.
            var expectedConfidence = (int)Math.Round(Math.Max(expectedCalibrated, 1m - expectedCalibrated) * 100m);
            calibratedPrediction.PredictionConfidence.Should().Be(expectedConfidence);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static decimal InterpolateCalibrationTable(JsonElement table, float rawProbability)
    {
        var points = table.EnumerateObject()
            .Select(p => new
            {
                Raw = float.Parse(p.Name, CultureInfo.InvariantCulture),
                Calibrated = p.Value.GetDecimal()
            })
            .OrderBy(p => p.Raw)
            .ToList();

        var clamped = Math.Clamp(rawProbability, 0f, 1f);
        if (clamped <= points[0].Raw)
            return points[0].Calibrated;
        if (clamped >= points[^1].Raw)
            return points[^1].Calibrated;

        for (var i = 1; i < points.Count; i++)
        {
            var lower = points[i - 1];
            var upper = points[i];
            if (clamped > upper.Raw)
                continue;

            if (Math.Abs(upper.Raw - lower.Raw) < 1e-6f)
                return upper.Calibrated;

            var t = (decimal)((clamped - lower.Raw) / (upper.Raw - lower.Raw));
            return lower.Calibrated + t * (upper.Calibrated - lower.Calibrated);
        }

        return points[^1].Calibrated;
    }

    [Fact]
    public async Task LoadActiveModelAsync_Loads_Threshold_Lookup_Into_FrequencyManager()
    {
        var modelPath = GetModelPath("outcome_predictor_v20260409_013337.onnx");
        var mockRepo = new Mock<IMlModelRepository>();
        mockRepo.Setup(r => r.GetActiveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeModelMetadata(modelPath, "v20260409_013337"));

        var frequencyManager = new SignalFrequencyManager(new Mock<ILogger<SignalFrequencyManager>>().Object);
        using var svc = CreateService(mockRepo.Object);
        svc.SetFrequencyManager(frequencyManager);

        await svc.LoadActiveModelAsync();

        var threshold = frequencyManager.GetDynamicThreshold(
            Regime.BULLISH,
            adx: 35m,
            atrPct: 0.005m,
            hourOfDay: 14,
            recentWinRate: 0.50m,
            timeframe: "5m",
            p: StrategyParameters.Default with
            {
                MlDynamicThresholdsEnabled = true,
                MlDynamicThresholdMin = 40,
                MlDynamicThresholdMax = 95,
                MlDynamicThresholdMaxDelta = 100
            });

        threshold.BuyThreshold.Should().Be(90);
        threshold.Source.Should().Contain("BULLISH_high_overlap");
    }
}
