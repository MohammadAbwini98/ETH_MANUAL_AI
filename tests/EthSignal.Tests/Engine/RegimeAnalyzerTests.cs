using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P3 tests: Market regime classification on 15m context.</summary>
public class RegimeAnalyzerTests
{
    private static IndicatorSnapshot MakeSnapshot(
        DateTimeOffset time,
        decimal ema20, decimal ema50,
        decimal closeMid, decimal vwap,
        decimal adx, decimal plusDi, decimal minusDi)
    {
        return new IndicatorSnapshot
        {
            Symbol = "ETHUSD",
            Timeframe = "15m",
            CandleOpenTimeUtc = time,
            Ema20 = ema20,
            Ema50 = ema50,
            Rsi14 = 55,
            Macd = 0,
            MacdSignal = 0,
            MacdHist = 0,
            Atr14 = 10,
            Adx14 = adx,
            PlusDi = plusDi,
            MinusDi = minusDi,
            VolumeSma20 = 100,
            Vwap = vwap,
            Spread = 1m,
            CloseMid = closeMid,
            IsProvisional = false
        };
    }

    /// <summary>
    /// Build a series of snapshots where EMA20 is rising.
    /// k=3 slope requires at least 4 snapshots.
    /// </summary>
    private static List<IndicatorSnapshot> MakeBullishSeries()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        return
        [
            // EMA20 rising: 100, 101, 102, 103, 104
            // EMA50 flat at 95
            // Close > VWAP, ADX >= 18, +DI > -DI
            MakeSnapshot(t0,                     100m, 95m, 105m, 100m, 25m, 30m, 15m),
            MakeSnapshot(t0.AddMinutes(15),      101m, 95m, 106m, 101m, 25m, 30m, 15m),
            MakeSnapshot(t0.AddMinutes(30),      102m, 95m, 107m, 102m, 25m, 30m, 15m),
            MakeSnapshot(t0.AddMinutes(45),      103m, 95m, 108m, 103m, 25m, 30m, 15m),
            MakeSnapshot(t0.AddMinutes(60),      104m, 95m, 109m, 104m, 25m, 30m, 15m),
        ];
    }

    private static List<IndicatorSnapshot> MakeBearishSeries()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        return
        [
            // EMA20 falling: 100, 99, 98, 97, 96
            // EMA50 flat at 105
            // Close < VWAP, ADX >= 18, -DI > +DI
            MakeSnapshot(t0,                     100m, 105m, 95m, 100m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(15),       99m, 105m, 94m,  99m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(30),       98m, 105m, 93m,  98m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(45),       97m, 105m, 92m,  97m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(60),       96m, 105m, 91m,  96m, 25m, 12m, 30m),
        ];
    }

    private static List<IndicatorSnapshot> MakeNeutralSeries_LowAdx()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        return
        [
            // ADX < 15 → NEUTRAL regardless of other conditions
            MakeSnapshot(t0,                     100m, 95m, 105m, 100m, 8m, 20m, 18m),
            MakeSnapshot(t0.AddMinutes(15),      101m, 95m, 106m, 101m, 9m, 20m, 18m),
            MakeSnapshot(t0.AddMinutes(30),      102m, 95m, 107m, 102m, 10m, 20m, 18m),
            MakeSnapshot(t0.AddMinutes(45),      103m, 95m, 108m, 103m, 11m, 20m, 18m),
            MakeSnapshot(t0.AddMinutes(60),      104m, 95m, 109m, 104m, 12m, 20m, 18m),
        ];
    }

    /// <summary>P3-T1: Bullish classification when all 6 conditions are met (3 mandatory + 3 scored).</summary>
    [Fact]
    public void Bullish_Dataset_Classified_As_BULLISH()
    {
        var snapshots = MakeBullishSeries();
        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);

        result.Regime.Should().Be(Regime.BULLISH);
        result.RegimeScore.Should().BeGreaterThanOrEqualTo(5);
        result.TriggeredConditions.Should().HaveCountGreaterThanOrEqualTo(5);
    }

    /// <summary>P3-T2: Bearish classification when all 6 conditions are met (3 mandatory + 3 scored).</summary>
    [Fact]
    public void Bearish_Dataset_Classified_As_BEARISH()
    {
        var snapshots = MakeBearishSeries();
        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);

        result.Regime.Should().Be(Regime.BEARISH);
        result.RegimeScore.Should().BeGreaterThanOrEqualTo(5);
        result.TriggeredConditions.Should().HaveCountGreaterThanOrEqualTo(5);
    }

    /// <summary>P3-T3: Range/sideways market with low ADX → NEUTRAL.</summary>
    [Fact]
    public void LowAdx_Dataset_Classified_As_NEUTRAL()
    {
        var snapshots = MakeNeutralSeries_LowAdx();
        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);

        result.Regime.Should().Be(Regime.NEUTRAL);
        result.DisqualifyingConditions.Should().NotBeEmpty();
    }

    /// <summary>P3-T4: Boundary behavior — ADX exactly at 20 should qualify (v3.0 threshold).</summary>
    [Fact]
    public void Adx_Exactly_20_Qualifies()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<IndicatorSnapshot>
        {
            MakeSnapshot(t0,                     100m, 95m, 105m, 100m, 20m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(15),      101m, 95m, 106m, 101m, 20m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(30),      102m, 95m, 107m, 102m, 20m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(45),      103m, 95m, 108m, 103m, 20m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(60),      104m, 95m, 109m, 104m, 20m, 25m, 15m),
        };

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        result.Regime.Should().Be(Regime.BULLISH, "ADX=20 should meet >= 20 threshold");
    }

    /// <summary>P3-T4: ADX at 14.99 should NOT qualify → NEUTRAL (threshold is 20).</summary>
    [Fact]
    public void Adx_Below_20_Disqualifies()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<IndicatorSnapshot>
        {
            MakeSnapshot(t0,                     100m, 95m, 105m, 100m, 14.99m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(15),      101m, 95m, 106m, 101m, 14.99m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(30),      102m, 95m, 107m, 102m, 14.99m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(45),      103m, 95m, 108m, 103m, 14.99m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(60),      104m, 95m, 109m, 104m, 14.99m, 25m, 15m),
        };

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        result.Regime.Should().Be(Regime.NEUTRAL, "ADX=14.99 < 15 threshold");
    }

    /// <summary>P3-T4: EMA20 == EMA50 → NEUTRAL (neither > nor <).</summary>
    [Fact]
    public void Ema_Equality_Classified_As_NEUTRAL()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<IndicatorSnapshot>
        {
            MakeSnapshot(t0,                     100m, 100m, 105m, 100m, 25m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(15),      100m, 100m, 105m, 100m, 25m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(30),      100m, 100m, 105m, 100m, 25m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(45),      100m, 100m, 105m, 100m, 25m, 25m, 15m),
            MakeSnapshot(t0.AddMinutes(60),      100m, 100m, 105m, 100m, 25m, 25m, 15m),
        };

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        result.Regime.Should().Be(Regime.NEUTRAL, "EMA20 == EMA50 fails both > and < checks");
    }

    [Fact]
    public void Empty_Snapshots_Returns_NEUTRAL()
    {
        var result = RegimeAnalyzer.Classify("ETHUSD", []);
        result.Regime.Should().Be(Regime.NEUTRAL);
    }

    [Fact]
    public void Insufficient_History_Returns_NEUTRAL()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var snapshots = new List<IndicatorSnapshot>
        {
            MakeSnapshot(t0, 100m, 95m, 105m, 100m, 25m, 30m, 15m),
        };

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        result.Regime.Should().Be(Regime.NEUTRAL, "need at least 4 snapshots for k=3 slope");
    }

    [Fact]
    public void Mixed_Conditions_Returns_NEUTRAL_With_Details()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        // EMA20 > EMA50 (bullish) but slope is FLAT (mandatory fail for bull)
        // EMA20 > EMA50 also fails bearish mandatory EMA alignment
        // → NEUTRAL because neither direction passes all 3 mandatory conditions
        var snapshots = new List<IndicatorSnapshot>
        {
            MakeSnapshot(t0,                     100m, 95m, 90m, 100m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(15),      100m, 95m, 91m, 100m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(30),      100m, 95m, 92m, 100m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(45),      100m, 95m, 93m, 100m, 25m, 12m, 30m),
            MakeSnapshot(t0.AddMinutes(60),      100m, 95m, 94m, 100m, 25m, 12m, 30m),
        };

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        result.Regime.Should().Be(Regime.NEUTRAL, "flat EMA20 slope fails bullish mandatory, EMA alignment fails bearish mandatory");
        result.DisqualifyingConditions.Should().NotBeEmpty();
    }
}
