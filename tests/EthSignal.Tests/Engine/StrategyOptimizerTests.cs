using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P7 tests: Adaptive improvement and strategy versioning.</summary>
public class StrategyOptimizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);

    private static IndicatorSnapshot MakeSnap(DateTimeOffset time) => new()
    {
        Symbol = "ETHUSD", Timeframe = "5m", CandleOpenTimeUtc = time,
        Ema20 = 2050, Ema50 = 2040, Rsi14 = 55, Macd = 1, MacdSignal = 0.5m, MacdHist = 0.5m,
        Atr14 = 10, Adx14 = 25, PlusDi = 25, MinusDi = 15,
        VolumeSma20 = 100, Vwap = 2045, Spread = 1, CloseMid = 2055, IsProvisional = false
    };

    private static RichCandle MakeCandle(DateTimeOffset time) => new()
    {
        OpenTime = time,
        BidOpen = 2048, BidHigh = 2060, BidLow = 2045, BidClose = 2055,
        AskOpen = 2049, AskHigh = 2061, AskLow = 2046, AskClose = 2056,
        Volume = 150, IsClosed = true
    };

    /// <summary>P7-T1: Feature export contains all expected columns.</summary>
    [Fact]
    public void ExportFeatures_Contains_All_Columns()
    {
        var signal = new SignalRecommendation
        {
            SignalId = Guid.NewGuid(), Symbol = "ETHUSD", Timeframe = "5m",
            SignalTimeUtc = T0, Direction = SignalDirection.BUY,
            EntryPrice = 2055, TpPrice = 2070, SlPrice = 2045,
            ConfidenceScore = 80, Regime = Regime.BULLISH, Reasons = ["test"]
        };
        var snap = MakeSnap(T0);
        var outcome = new SignalOutcome
        {
            SignalId = signal.SignalId, OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m
        };
        var candle = MakeCandle(T0);

        var rows = StrategyOptimizer.ExportFeatures([signal], [snap], [outcome], [candle]);

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.SignalId.Should().Be(signal.SignalId);
        row.Ema20MinusEma50.Should().Be(10m);
        row.Rsi14.Should().Be(55m);
        row.MacdHist.Should().Be(0.5m);
        row.Adx14.Should().Be(25m);
        row.Outcome.Should().Be(OutcomeLabel.WIN);
        row.PnlR.Should().Be(1.5m);
        row.HourOfDay.Should().Be(10);
    }

    /// <summary>P7-T2: Draft weight generation produces new version.</summary>
    [Fact]
    public void ProposeDraftWeights_Produces_New_Version()
    {
        var current = new StrategyVersion { Version = "v1.0" };

        // Create 12 feature rows with wins when conditions are good
        var features = Enumerable.Range(0, 12).Select(i => new StrategyOptimizer.FeatureRow(
            SignalId: Guid.NewGuid(),
            RegimeScore: 80, Ema20MinusEma50: 10, Ema20Slope: 1,
            Rsi14: 52, MacdHist: 0.5m, Adx14: 25, PlusDiMinusMinusDi: 10,
            DistanceToVwap: 5, VolumeRatio: 1.5m, SpreadPct: 0.001m,
            AtrPct: 0.005m, BodyRatio: 0.6m, HourOfDay: 10,
            Outcome: i < 8 ? OutcomeLabel.WIN : OutcomeLabel.LOSS,
            PnlR: i < 8 ? 1.5m : -1m
        )).ToList();

        var draft = StrategyOptimizer.ProposeDraftWeights(features, current);

        draft.Version.Should().Be("v1.1");
        draft.IsDraft.Should().BeTrue();
        draft.WeightRegime.Should().Be(20);
        // Other weights should sum to 80
        int otherSum = draft.WeightEma + draft.WeightRsi + draft.WeightMacd +
                       draft.WeightAdx + draft.WeightVwap + draft.WeightVolume + draft.WeightSpread;
        otherSum.Should().Be(80);
    }

    /// <summary>P7-T3: Reproducibility — same inputs produce same output.</summary>
    [Fact]
    public void Same_Input_Produces_Same_Draft()
    {
        var current = new StrategyVersion { Version = "v1.0" };
        var features = Enumerable.Range(0, 15).Select(i => new StrategyOptimizer.FeatureRow(
            SignalId: Guid.NewGuid(),
            RegimeScore: 75, Ema20MinusEma50: 8, Ema20Slope: 0.5m,
            Rsi14: 50, MacdHist: 0.3m, Adx14: 20, PlusDiMinusMinusDi: 8,
            DistanceToVwap: 3, VolumeRatio: 1.3m, SpreadPct: 0.001m,
            AtrPct: 0.004m, BodyRatio: 0.5m, HourOfDay: 12,
            Outcome: i % 3 == 0 ? OutcomeLabel.LOSS : OutcomeLabel.WIN,
            PnlR: i % 3 == 0 ? -1m : 1.5m
        )).ToList();

        var draft1 = StrategyOptimizer.ProposeDraftWeights(features, current);
        var draft2 = StrategyOptimizer.ProposeDraftWeights(features, current);

        draft1.Should().BeEquivalentTo(draft2, options => options.Excluding(x => x.CreatedAtUtc));
    }
}
