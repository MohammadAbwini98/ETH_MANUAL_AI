using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>
/// B-07 tests: Parameterization, replay, and optimizer.
/// Covers Phase B07-1 through B07-4 acceptance criteria.
/// </summary>
public class B07Tests
{
    // ─── B07-1: Parameter validation ─────────────────

    [Fact]
    public void Default_Parameters_Are_Valid()
    {
        var p = StrategyParameters.Default;
        p.Validate().Should().BeNull();
    }

    [Fact]
    public void Validation_Rejects_EmaFast_GE_EmaSlow()
    {
        var p = StrategyParameters.Default with { EmaFastPeriod = 50, EmaSlowPeriod = 20 };
        p.Validate().Should().Contain("EmaFastPeriod");
    }

    [Fact]
    public void Validation_Rejects_HardMaxRisk_Below_PerTrade()
    {
        var p = StrategyParameters.Default with { HardMaxRiskPercent = 0.1m, RiskPerTradePercent = 0.5m };
        p.Validate().Should().Contain("HardMaxRiskPercent");
    }

    [Fact]
    public void Validation_Rejects_Negative_StopAtr()
    {
        var p = StrategyParameters.Default with { StopAtrMultiplier = -1m };
        p.Validate().Should().Contain("StopAtrMultiplier");
    }

    [Fact]
    public void Validation_Rejects_RsiBuyMin_GE_Max()
    {
        var p = StrategyParameters.Default with { RsiBuyMin = 70, RsiBuyMax = 35 };
        p.Validate().Should().Contain("RsiBuyMin");
    }

    [Fact]
    public void Serialization_RoundTrip_Preserves_All_Fields()
    {
        var p = StrategyParameters.Default with { AdxTrendThreshold = 22m, TargetRMultiple = 2.5m };
        var json = p.ToJson();
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<StrategyParameters>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        deserialized.Should().NotBeNull();
        deserialized!.AdxTrendThreshold.Should().Be(22m);
        deserialized.TargetRMultiple.Should().Be(2.5m);
        deserialized.EmaFastPeriod.Should().Be(20);
    }

    [Fact]
    public void ToRiskPolicy_Maps_All_Fields()
    {
        var p = StrategyParameters.Default with
        {
            AccountBalanceUsd = 100m,
            RiskPerTradePercent = 1.0m,
            StopAtrMultiplier = 1.2m,
            TargetRMultiple = 2.0m
        };
        var rp = p.ToRiskPolicy();
        rp.AccountBalanceUsd.Should().Be(100m);
        rp.RiskPercentPerTrade.Should().Be(1.0m);
        rp.AtrMultiplier.Should().Be(1.2m);
        rp.RewardToRisk.Should().Be(2.0m);
    }

    // ─── B07-1: Parameterized engines use parameters ──

    [Fact]
    public void IndicatorEngine_Uses_Custom_Periods()
    {
        var candles = MakeClosedCandles(60, 2000m, 1m);
        var p = StrategyParameters.Default with { EmaFastPeriod = 10, EmaSlowPeriod = 30, WarmUpPeriod = 30 };

        var defaultSnaps = IndicatorEngine.ComputeAll("ETHUSD", "5m", candles);
        var customSnaps = IndicatorEngine.ComputeAll("ETHUSD", "5m", candles, p);

        // Different EMA periods should produce different values after warmup
        var dSnap = defaultSnaps[^1];
        var cSnap = customSnaps[^1];
        // With trending data and different periods, EMAs should differ
        (dSnap.Ema20 == cSnap.Ema20 && dSnap.Ema50 == cSnap.Ema50).Should().BeFalse(
            "different EMA periods should produce different indicator values");
    }

    [Fact]
    public void RegimeAnalyzer_Uses_Custom_AdxThreshold()
    {
        // ADX at 17 — passes default threshold (15) but fails higher threshold (20)
        var snapshots = MakeRegimeSnapshots(adx: 17);

        var resultDefault = RegimeAnalyzer.Classify("ETHUSD", snapshots, StrategyParameters.Default);
        var resultStrict = RegimeAnalyzer.Classify("ETHUSD", snapshots,
            StrategyParameters.Default with { AdxTrendThreshold = 20m });

        // Default: ADX 17 >= 15 → can be directional
        // Strict: ADX 17 < 20 → forced NEUTRAL
        resultStrict.Regime.Should().Be(Regime.NEUTRAL);
    }

    [Fact]
    public void OutcomeEvaluator_Uses_Custom_TimeoutBars()
    {
        var signal = MakeSignal(SignalDirection.BUY, 2000m, 2020m, 1990m);
        // 5 candles that don't hit TP or SL
        var futureCandles = Enumerable.Range(0, 5).Select(i => MakeCandle(
            signal.SignalTimeUtc.AddMinutes(5 * i), 2001m, 2005m, 1995m, 2002m)).ToList();

        // Default timeout = 60 → 5 candles = PENDING
        var outcomeDefault = OutcomeEvaluator.Evaluate(signal, futureCandles);
        outcomeDefault.OutcomeLabel.Should().Be(OutcomeLabel.PENDING);

        // Custom timeout = 3 → 5 >= 3 = EXPIRED
        // FR-3: When evaluating on M5 (intraday), IntradayTimeoutBars takes precedence
        var outcomeShort = OutcomeEvaluator.Evaluate(signal, futureCandles,
            StrategyParameters.Default with { OutcomeTimeoutBars = 3, IntradayTimeoutBars = 3 });
        outcomeShort.OutcomeLabel.Should().Be(OutcomeLabel.EXPIRED);
    }

    // ─── B07-2: Replay determinism ───────────────────

    [Fact]
    public void ReplayState_Maintains_Bounded_Window()
    {
        // Use explicit OutcomeTimeoutBars=60 so maxWindow=max(70,80)=80, 1m limit=1200.
        // This is independent of the Default OutcomeTimeoutBars value (which may vary).
        var p = StrategyParameters.Default with { OutcomeTimeoutBars = 60 };
        var state = new ReplayState(p);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Add 2000 candles — well above the 1200-candle 1m limit (maxWindow*15)
        for (int i = 0; i < 2000; i++)
            state.Add1mCandle(MakeCandle(t.AddMinutes(i), 2000 + i * 0.1m, 2001 + i * 0.1m,
                1999 + i * 0.1m, 2000.5m + i * 0.1m));

        // Window should be bounded to maxWindow*15=1200, not 2000
        state.Candles1m.Count.Should().BeLessThan(2000);
        state.Candles1m.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReplayState_Get1mCandlesInRange_Returns_Correct_Subset()
    {
        var state = new ReplayState(StrategyParameters.Default);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 20; i++)
            state.Add1mCandle(MakeCandle(t.AddMinutes(i), 2000, 2001, 1999, 2000));

        var range = state.Get1mCandlesInRange(t.AddMinutes(5), t.AddMinutes(10));
        range.Count.Should().Be(5);
        range[0].OpenTime.Should().Be(t.AddMinutes(5));
    }

    [Fact]
    public void ReplayResult_ComputeMetrics_Correct()
    {
        var result = new ReplayResult { CandlesProcessed = 100 };

        // Add 3 signals with outcomes: WIN(1.5R), LOSS(-1R), WIN(1.5R)
        for (int i = 0; i < 3; i++)
        {
            result.Signals.Add(MakeSignal(SignalDirection.BUY, 2000, 2020, 1990));
            result.Outcomes.Add(new SignalOutcome
            {
                SignalId = result.Signals[i].SignalId,
                OutcomeLabel = i == 1 ? OutcomeLabel.LOSS : OutcomeLabel.WIN,
                PnlR = i == 1 ? -1m : 1.5m,
                BarsObserved = 10
            });
        }

        var metrics = result.ComputeMetrics();
        metrics.TradeCount.Should().Be(3);
        metrics.Wins.Should().Be(2);
        metrics.Losses.Should().Be(1);
        metrics.WinRate.Should().BeApproximately(0.6667m, 0.01m);
        metrics.TotalPnlR.Should().Be(2.0m);
        metrics.ProfitFactor.Should().Be(3.0m); // 3.0 / 1.0
    }

    // ─── B07-4: Optimizer scoring ────────────────────

    [Fact]
    public void Optimizer_Config_Defaults_Are_Sane()
    {
        var config = new OptimizerConfig();
        config.FoldCount.Should().Be(3);
        config.MaxCandidates.Should().Be(50);
        config.MinTradeCount.Should().Be(10);
        config.MinImprovementPct.Should().Be(5.0m);
        config.MaxDrawdownExpansion.Should().Be(1.10m);
    }

    // ─── Helpers ─────────────────────────────────────

    private static List<RichCandle> MakeClosedCandles(int count, decimal startPrice, decimal trend)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(i =>
        {
            var mid = startPrice + i * trend;
            return new RichCandle
            {
                OpenTime = t.AddMinutes(5 * i),
                BidOpen = mid - 0.5m,
                BidHigh = mid + 5,
                BidLow = mid - 5,
                BidClose = mid,
                AskOpen = mid + 0.5m,
                AskHigh = mid + 6,
                AskLow = mid - 4,
                AskClose = mid + 1,
                Volume = 100 + i,
                IsClosed = true
            };
        }).ToList();
    }

    private static List<IndicatorSnapshot> MakeRegimeSnapshots(decimal adx)
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, 10).Select(i => new IndicatorSnapshot
        {
            Symbol = "ETHUSD",
            Timeframe = "15m",
            CandleOpenTimeUtc = t.AddMinutes(15 * i),
            Ema20 = 2050 + i * 2, // rising
            Ema50 = 2040 + i,     // slower rise → EMA20 > EMA50
            Rsi14 = 55,
            Macd = 1,
            MacdSignal = 0.5m,
            MacdHist = 0.5m,
            Atr14 = 10,
            Adx14 = adx,
            PlusDi = 25,
            MinusDi = 15,
            VolumeSma20 = 100,
            Vwap = 2045 + i,
            Spread = 1,
            CloseMid = 2055 + i * 3,
            IsProvisional = false
        }).ToList();
    }

    private static SignalRecommendation MakeSignal(SignalDirection dir, decimal entry, decimal tp, decimal sl)
        => new()
        {
            SignalId = Guid.NewGuid(),
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
            Direction = dir,
            EntryPrice = entry,
            TpPrice = tp,
            SlPrice = sl,
            ConfidenceScore = 75,
            Regime = Regime.BULLISH,
            Reasons = ["test"]
        };

    private static RichCandle MakeCandle(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close)
        => new()
        {
            OpenTime = time,
            BidOpen = open - 0.5m,
            BidHigh = high - 0.5m,
            BidLow = low - 0.5m,
            BidClose = close - 0.5m,
            AskOpen = open + 0.5m,
            AskHigh = high + 0.5m,
            AskLow = low + 0.5m,
            AskClose = close + 0.5m,
            Volume = 100,
            IsClosed = true
        };
}
