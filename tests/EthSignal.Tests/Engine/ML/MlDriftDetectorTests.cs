using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class MlDriftDetectorTests
{
    private static MlDriftDetector CreateDetector()
    {
        var logger = new Mock<ILogger<MlDriftDetector>>().Object;
        var modelRepo = new Mock<IMlModelRepository>().Object;
        return new MlDriftDetector(modelRepo, logger);
    }

    private static StrategyParameters DefaultParams() => new()
    {
        MlDriftAucThreshold = 0.52m,
        MlDriftBrierThreshold = 0.28m
    };

    [Fact]
    public void CheckDrift_Returns_NoDrift_When_InsufficientData()
    {
        var detector = CreateDetector();
        // Only 10 predictions — below 30 minimum
        for (int i = 0; i < 10; i++)
            detector.RecordOutcome(0.6m, true);

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.DriftDetected.Should().BeFalse();
    }

    [Fact]
    public void CheckDrift_Returns_NoDrift_When_Good_Predictions()
    {
        var detector = CreateDetector();
        // Perfectly calibrated: high predictions → wins, low predictions → losses
        for (int i = 0; i < 20; i++)
        {
            detector.RecordOutcome(0.80m, true);
            detector.RecordOutcome(0.30m, false);
        }

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.DriftDetected.Should().BeFalse();
        result.RollingAuc.Should().BeGreaterThan(0.52m);
    }

    [Fact]
    public void CheckDrift_Detects_AucDrift_When_Predictions_Inverted()
    {
        var detector = CreateDetector();
        // Inverted predictions: high prob → losses, low prob → wins
        for (int i = 0; i < 20; i++)
        {
            detector.RecordOutcome(0.80m, false);
            detector.RecordOutcome(0.20m, true);
        }

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.DriftDetected.Should().BeTrue();
        result.AucDrift.Should().BeTrue();
        result.RollingAuc.Should().BeLessThan(0.52m);
    }

    [Fact]
    public void CheckDrift_Detects_BrierDrift_When_HighError()
    {
        var detector = CreateDetector();
        // Large prediction errors: predict 0.90 but always lose
        for (int i = 0; i < 40; i++)
            detector.RecordOutcome(0.90m, false);

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.DriftDetected.Should().BeTrue();
        result.BrierDrift.Should().BeTrue();
        result.RollingBrier.Should().BeGreaterThan(0.28m);
    }

    [Fact]
    public void CheckDrift_Detects_WinRate_Gap()
    {
        var detector = CreateDetector();
        // Predict 0.70 average but actual win rate is ~45%
        for (int i = 0; i < 40; i++)
        {
            if (i % 2 == 0)
                detector.RecordOutcome(0.70m, false); // 20 losses at predicted 0.70
            else
                detector.RecordOutcome(0.70m, i % 4 == 1); // half of remaining win
        }

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        // Actual win rate = 10/40 = 0.25, predicted mean = 0.70, gap = 0.45 > 0.15
        result.WinRateDrift.Should().BeTrue();
    }

    [Fact]
    public void CheckDrift_Rolling_Window_Caps_At_100()
    {
        var detector = CreateDetector();
        // Add 120 predictions — should only keep last 100
        for (int i = 0; i < 60; i++)
        {
            detector.RecordOutcome(0.60m, true);
            detector.RecordOutcome(0.40m, false);
        }

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.WindowSize.Should().Be(100);
    }

    [Fact]
    public void CheckDrift_DriftResult_Contains_AllMetrics()
    {
        var detector = CreateDetector();
        for (int i = 0; i < 40; i++)
            detector.RecordOutcome(0.65m, i % 2 == 0);

        var result = detector.CheckDrift(DefaultParams(), "v1.0");

        result.WindowSize.Should().Be(40);
        result.ActualWinRate.Should().BeGreaterThan(0);
        result.PredictedMeanWin.Should().Be(0.65m);
    }
}
