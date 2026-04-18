using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public sealed class BlockedMlFeatureBackfillServiceTests
{
    [Fact]
    public async Task BackfillAsync_Reconstructs_And_Inserts_Missing_Blocked_Feature_Row()
    {
        var decisionAuditRepo = new Mock<IDecisionAuditRepository>();
        var candleRepo = new Mock<ICandleRepository>();
        var indicatorRepo = new Mock<IIndicatorRepository>();
        var regimeRepo = new Mock<IRegimeRepository>();
        var signalRepo = new Mock<ISignalRepository>();
        var mlFeatureRepo = new Mock<IMlFeatureRepository>();

        var evaluationId = Guid.NewGuid();
        var barTime = new DateTimeOffset(2026, 4, 17, 19, 45, 0, TimeSpan.Zero);
        var signalTime = barTime.AddMinutes(1);
        var parameters = StrategyParameters.Default with
        {
            StrategyVersion = "v3.1",
            TimeframeBias = "15m",
            MlMode = MlMode.SHADOW
        };

        decisionAuditRepo.Setup(r => r.GetBlockedMlBackfillCandidatesAsync("ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DecisionMlBackfillCandidate
                {
                    EvaluationId = evaluationId,
                    Symbol = "ETHUSD",
                    Timeframe = "1m",
                    SignalTimeUtc = signalTime,
                    DecisionTimeUtc = barTime.AddSeconds(10),
                    BarTimeUtc = barTime,
                    DecisionTypeRaw = "BUY",
                    CandidateDirectionRaw = "BUY",
                    RegimeRaw = "BULLISH",
                    ConfidenceScore = 72,
                    ParameterSetId = "v3.1",
                    EffectiveRuntimeParametersJson = JsonSerializer.Serialize(parameters),
                    IndicatorsJson = JsonSerializer.Serialize(new Dictionary<string, decimal>
                    {
                        ["ema20"] = 2400m,
                        ["ema50"] = 2398m,
                        ["rsi14"] = 58m,
                        ["macd_hist"] = 0.4m,
                        ["adx14"] = 26m,
                        ["plus_di"] = 28m,
                        ["minus_di"] = 13m,
                        ["atr14"] = 2m,
                        ["vwap"] = 2399m,
                        ["volume_sma20"] = 120m,
                        ["spread"] = 1m,
                        ["close_mid"] = 2401m
                    })
                }
            ]);

        candleRepo.Setup(r => r.GetClosedCandlesInRangeAsync(
                Timeframe.M1,
                "ETHUSD",
                barTime,
                barTime.AddMinutes(1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new RichCandle
                {
                    OpenTime = barTime,
                    BidOpen = 2400m,
                    BidHigh = 2403m,
                    BidLow = 2399m,
                    BidClose = 2401m,
                    AskOpen = 2401m,
                    AskHigh = 2404m,
                    AskLow = 2400m,
                    AskClose = 2402m,
                    Volume = 150m,
                    BuyerPct = 55m,
                    SellerPct = 45m,
                    IsClosed = true
                }
            ]);

        indicatorRepo.Setup(r => r.GetSnapshotsAsync(
                "ETHUSD",
                "1m",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                MakeSnapshot(barTime.AddMinutes(-1), 2399m, 56m),
                MakeSnapshot(barTime, 2401m, 58m)
            ]);

        regimeRepo.Setup(r => r.GetLatestBeforeAsync(
                "ETHUSD",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegimeResult
            {
                Symbol = "ETHUSD",
                CandleOpenTimeUtc = barTime.AddMinutes(-15),
                Regime = Regime.BULLISH,
                RegimeScore = 4,
                TriggeredConditions = ["trend"],
                DisqualifyingConditions = []
            });

        signalRepo.Setup(r => r.GetRecentSignalsAsync(
                "ETHUSD",
                "1m",
                It.IsAny<DateTimeOffset>(),
                barTime,
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SignalRecommendation
                {
                    SignalId = Guid.NewGuid(),
                    Symbol = "ETHUSD",
                    Timeframe = "1m",
                    SignalTimeUtc = barTime.AddMinutes(-2),
                    Direction = SignalDirection.SELL,
                    EntryPrice = 2398m,
                    TpPrice = 2392m,
                    SlPrice = 2402m,
                    RiskPercent = 0.5m,
                    RiskUsd = 10m,
                    ConfidenceScore = 65,
                    Regime = Regime.BEARISH,
                    StrategyVersion = "v3.1",
                    Reasons = ["recent"],
                    Status = SignalStatus.CLOSED
                }
            ]);

        signalRepo.Setup(r => r.GetLatestSignalBeforeAsync(
                "ETHUSD",
                signalTime,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalRecommendation
            {
                SignalId = Guid.NewGuid(),
                Symbol = "ETHUSD",
                Timeframe = "1m",
                SignalTimeUtc = barTime.AddMinutes(-2),
                Direction = SignalDirection.BUY,
                EntryPrice = 2399m,
                TpPrice = 2404m,
                SlPrice = 2395m,
                RiskPercent = 0.5m,
                RiskUsd = 10m,
                ConfidenceScore = 70,
                Regime = Regime.BULLISH,
                StrategyVersion = "v3.1",
                Reasons = ["latest"],
                Status = SignalStatus.CLOSED
            });

        signalRepo.Setup(r => r.GetOutcomesAsync(
                "ETHUSD",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                "v3.1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SignalOutcome
                {
                    SignalId = Guid.NewGuid(),
                    EvaluatedAtUtc = barTime.AddMinutes(-3),
                    OutcomeLabel = OutcomeLabel.WIN,
                    PnlR = 1.2m
                }
            ]);

        MlFeatureVector? saved = null;
        string? savedVersion = null;
        string? savedStatus = null;
        mlFeatureRepo.Setup(r => r.InsertAsync(
                It.IsAny<MlFeatureVector>(),
                null,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<MlFeatureVector, Guid?, string, string, CancellationToken>((fv, _, version, status, _) =>
            {
                saved = fv;
                savedVersion = version;
                savedStatus = status;
            })
            .Returns(Task.CompletedTask);

        var service = new BlockedMlFeatureBackfillService(
            decisionAuditRepo.Object,
            candleRepo.Object,
            indicatorRepo.Object,
            regimeRepo.Object,
            signalRepo.Object,
            mlFeatureRepo.Object,
            NullLogger<BlockedMlFeatureBackfillService>.Instance);

        var result = await service.BackfillAsync("ETHUSD", CancellationToken.None);

        result.Candidates.Should().Be(1);
        result.Backfilled.Should().Be(1);
        result.SkippedMissingCandle.Should().Be(0);
        result.SkippedMissingIndicators.Should().Be(0);
        saved.Should().NotBeNull();
        saved!.EvaluationId.Should().Be(evaluationId);
        saved.Timeframe.Should().Be("1m");
        saved.DirectionEncoded.Should().Be(1);
        saved.RuleBasedScore.Should().Be(72);
        savedVersion.Should().Be(MlFeatureExtractor.FeatureVersion);
        savedStatus.Should().Be(MlEvaluationLinkStatus.OperationallyBlocked);
    }

    private static IndicatorSnapshot MakeSnapshot(DateTimeOffset time, decimal closeMid, decimal rsi) => new()
    {
        Symbol = "ETHUSD",
        Timeframe = "1m",
        CandleOpenTimeUtc = time,
        Ema20 = closeMid - 1m,
        Ema50 = closeMid - 3m,
        Rsi14 = rsi,
        Macd = 0.2m,
        MacdSignal = 0.1m,
        MacdHist = 0.1m,
        Atr14 = 2m,
        Adx14 = 24m,
        PlusDi = 25m,
        MinusDi = 14m,
        VolumeSma20 = 120m,
        Vwap = closeMid - 0.5m,
        Spread = 1m,
        CloseMid = closeMid,
        MidHigh = closeMid + 2m,
        MidLow = closeMid - 2m,
        IsProvisional = false
    };
}
