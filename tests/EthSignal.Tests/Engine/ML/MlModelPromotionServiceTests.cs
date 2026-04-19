using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class MlModelPromotionServiceTests
{
    [Fact]
    public async Task PromoteBestModelAsync_NoActive_ActivatesBestCandidate()
    {
        var weakPath = Path.GetTempFileName();
        var strongPath = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    MakeModel(1, "candidate-weak", weakPath, MlModelStatus.Candidate, auc: 0.61m, brier: 0.19m, samples: 250),
                    MakeModel(2, "candidate-strong", strongPath, MlModelStatus.Candidate, auc: 0.83m, brier: 0.16m, samples: 710)
                ]);

            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);

            var result = await service.PromoteBestModelAsync("outcome_predictor");

            result.Activated.Should().BeTrue();
            result.SelectedModel!.ModelVersion.Should().Be("candidate-strong");
            repo.Verify(r => r.UpdateStatusAsync(2, MlModelStatus.Active, null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(weakPath);
            File.Delete(strongPath);
        }
    }

    [Fact]
    public async Task PromoteBestModelAsync_StrongerCandidate_ReplacesWeakActive()
    {
        var activePath = Path.GetTempFileName();
        var candidatePath = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    MakeModel(10, "active-v1", activePath, MlModelStatus.Active, auc: 0.61m, brier: 0.19m, samples: 260, activatedAtUtc: DateTimeOffset.UtcNow.AddHours(-4)),
                    MakeModel(11, "candidate-v2", candidatePath, MlModelStatus.Candidate, auc: 0.83m, brier: 0.16m, samples: 710)
                ]);

            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);

            var result = await service.PromoteBestModelAsync("outcome_predictor");

            result.Activated.Should().BeTrue();
            result.PreviousActiveModel!.ModelVersion.Should().Be("active-v1");
            result.SelectedModel!.ModelVersion.Should().Be("candidate-v2");
            repo.Verify(r => r.UpdateStatusAsync(
                    10,
                    MlModelStatus.Retired,
                    It.Is<string?>(reason => reason != null && reason.Contains("Auto-promoted candidate-v2")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            repo.Verify(r => r.UpdateStatusAsync(11, MlModelStatus.Active, null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(activePath);
            File.Delete(candidatePath);
        }
    }

    [Fact]
    public async Task PromoteBestModelAsync_DoesNotReplaceActive_WhenCandidateIsNotMateriallyBetter()
    {
        var activePath = Path.GetTempFileName();
        var candidatePath = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    MakeModel(21, "active-v2", activePath, MlModelStatus.Active, auc: 0.80m, brier: 0.16m, samples: 700, activatedAtUtc: DateTimeOffset.UtcNow.AddHours(-2)),
                    MakeModel(22, "candidate-v3", candidatePath, MlModelStatus.Candidate, auc: 0.804m, brier: 0.158m, samples: 720)
                ]);

            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);

            var result = await service.PromoteBestModelAsync("outcome_predictor");

            result.Activated.Should().BeFalse();
            result.Reason.Should().Contain("remains preferred");
            repo.Verify(r => r.UpdateStatusAsync(It.IsAny<long>(), It.IsAny<MlModelStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(activePath);
            File.Delete(candidatePath);
        }
    }

    [Fact]
    public void EvaluatePromotion_CriticalFeatureDrift_DoesNotBlockPromotionByItself()
    {
        var candidatePath = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);
            var models = new[]
            {
                MakeModel(31, "candidate-strong", candidatePath, MlModelStatus.Candidate,
                    auc: 0.83m, brier: 0.16m, samples: 710)
            };

            var ctx = new MlPromotionContext { FeatureDriftStatus = MlDiagnosticsStatus.Critical };
            var decision = service.EvaluatePromotion("outcome_predictor", models, ctx);

            decision.ShouldActivate.Should().BeTrue();
            decision.SelectedModel!.ModelVersion.Should().Be("candidate-strong");
            decision.Reason.Should().Contain("No active model exists");
            decision.BlockReasons.Should().BeEmpty();
            service.LastPromotionBlockReason.Should().BeNull(); // set only by PromoteBestModelAsync
        }
        finally
        {
            File.Delete(candidatePath);
        }
    }

    [Fact]
    public void EvaluatePromotion_HeuristicCandidate_CannotBeActivated()
    {
        var path = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);
            var model = MakeModel(41, "heuristic-v1", path, MlModelStatus.Candidate,
                auc: 0.99m, brier: 0.01m, samples: 99999);
            var heuristic = model with { FileFormat = "heuristic" };

            var decision = service.EvaluatePromotion("outcome_predictor", new[] { heuristic });

            decision.ShouldActivate.Should().BeFalse();
            decision.Reason.Should().Contain("heuristic");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EvaluatePromotion_MissingFoldMetrics_IsBlockedAsIncompleteArtifact()
    {
        var path = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);
            var model = MakeModel(51, "no-fold-metrics", path, MlModelStatus.Candidate,
                auc: 0.83m, brier: 0.15m, samples: 600) with { FoldMetricsJson = "[]" };

            var decision = service.EvaluatePromotion("outcome_predictor", new[] { model });

            decision.ShouldActivate.Should().BeFalse();
            decision.Reason.Should().Contain("fold metrics");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetPromotionBlockers_EnforcesAccuracyFirstSampleAndCalibrationGates()
    {
        var path = Path.GetTempFileName();
        try
        {
            var repo = new Mock<IMlModelRepository>();
            var service = new MlModelPromotionService(repo.Object, NullLogger<MlModelPromotionService>.Instance);
            var model = MakeModel(61, "weak-ctx", path, MlModelStatus.Candidate,
                auc: 0.83m, brier: 0.15m, samples: 600);

            var ctx = new MlPromotionContext
            {
                FeatureDriftStatus = MlDiagnosticsStatus.Healthy,
                LabeledSamples = 150,   // < 200
                Wins = 20,              // < 30
                Losses = 25,            // < 30
                CalibrationSampleCount = 10, // < 50
                CalibrationBrier = 0.25m,    // > 0.20
                ThresholdLift = -0.05m       // negative
            };

            var blockers = service.GetPromotionBlockers(model, ctx);

            blockers.Should().Contain(b => b.Contains("clean labeled samples"));
            blockers.Should().Contain(b => b.Contains("WIN samples"));
            blockers.Should().Contain(b => b.Contains("LOSS samples"));
            blockers.Should().Contain(b => b.Contains("calibration samples"));
            blockers.Should().NotContain(b => b.Contains("live calibration Brier"));
            blockers.Should().Contain(b => b.Contains("threshold lift"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string ValidFoldMetricsJson =
        "[{\"fold\":1,\"auc_roc\":0.82,\"brier_score\":0.16,\"log_loss\":0.46,\"expected_calibration_error\":0.06}," +
        "{\"fold\":2,\"auc_roc\":0.80,\"brier_score\":0.17,\"log_loss\":0.47,\"expected_calibration_error\":0.07}," +
        "{\"fold\":3,\"auc_roc\":0.84,\"brier_score\":0.15,\"log_loss\":0.45,\"expected_calibration_error\":0.05}]";

    private static MlModelMetadata MakeModel(
        long id,
        string version,
        string filePath,
        MlModelStatus status,
        decimal auc,
        decimal brier,
        int samples,
        DateTimeOffset? activatedAtUtc = null)
    {
        return new MlModelMetadata
        {
            Id = id,
            ModelType = "outcome_predictor",
            ModelVersion = version,
            FilePath = filePath,
            FileFormat = "onnx",
            TrainStartUtc = DateTimeOffset.UtcNow.AddDays(-30),
            TrainEndUtc = DateTimeOffset.UtcNow.AddDays(-1),
            TrainingSampleCount = samples,
            FeatureCount = 58,
            FeatureListJson = "[]",
            AucRoc = auc,
            BrierScore = brier,
            ExpectedCalibrationError = 0.06m,
            FoldMetricsJson = ValidFoldMetricsJson,
            FeatureImportanceJson = "{}",
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-id),
            ActivatedAtUtc = activatedAtUtc
        };
    }
}
