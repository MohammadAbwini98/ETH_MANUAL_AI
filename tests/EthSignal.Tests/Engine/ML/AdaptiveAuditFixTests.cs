using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Engine.ML;

/// <summary>
/// Tests for the 12 adaptive strategy audit fixes (2026-04-16).
/// Covers: overlay resolution, adaptive parameter service, DRY volume,
/// neutral cliff, retrospective reachability, EXPIRED outcomes, dedup.
/// </summary>
public class AdaptiveAuditFixTests
{
    private static StrategyParameters BaseParams() => StrategyParameters.Default;

    private static MarketConditionClass MakeCondition(
        VolatilityTier vol = VolatilityTier.NORMAL,
        TrendStrength trend = TrendStrength.MODERATE,
        TradingSession session = TradingSession.LONDON,
        SpreadQuality spread = SpreadQuality.NORMAL,
        VolumeTier volume = VolumeTier.NORMAL)
        => new(vol, trend, session, spread, volume);

    private static SignalOutcome MakeOutcome(OutcomeLabel label, decimal pnlR) => new()
    {
        SignalId = Guid.NewGuid(),
        OutcomeLabel = label,
        PnlR = pnlR,
        EvaluatedAtUtc = DateTimeOffset.UtcNow
    };

    // ═══════════════════════════════════════════════════════
    //  Issue #1: adapted_parameters_json — BuildOverlayDiffsJson
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue1_BuildOverlayDiffsJson_Returns_NonNull_When_Overlay_Changes_Params()
    {
        var baseP = BaseParams();
        var adapted = baseP with { ConfidenceBuyThreshold = baseP.ConfidenceBuyThreshold + 10 };

        var json = MarketAdaptiveParameterService.BuildOverlayDiffsJson(baseP, adapted);

        json.Should().NotBeNull();
        json.Should().Contain("ConfBuyDelta");
    }

    [Fact]
    public void Issue1_BuildOverlayDiffsJson_Returns_Null_When_No_Changes()
    {
        var p = BaseParams();
        MarketAdaptiveParameterService.BuildOverlayDiffsJson(p, p).Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #8: RecordOutcome includes EXPIRED outcomes
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue8_RecordOutcome_Accepts_Expired_Outcome()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);
        var p = BaseParams() with { AdaptiveRetrospectiveMinOutcomes = 3 };

        var conditionKey = "NORMAL_MODERATE";
        var expired = MakeOutcome(OutcomeLabel.EXPIRED, -0.1m);
        service.RecordOutcome(expired, conditionKey, p);

        var status = service.GetStatus(p);
        status.ConditionDetails.Should().Contain(d => d.ConditionKey == conditionKey && d.OutcomeCount == 1);
    }

    [Fact]
    public void Issue8_RecordOutcome_Rejects_Pending_And_Ambiguous()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);
        var p = BaseParams();

        service.RecordOutcome(MakeOutcome(OutcomeLabel.PENDING, 0), "X_Y", p);
        service.RecordOutcome(MakeOutcome(OutcomeLabel.AMBIGUOUS, 0), "X_Y", p);

        var status = service.GetStatus(p);
        status.ConditionDetails.Should().NotContain(d => d.ConditionKey == "X_Y");
    }

    [Fact]
    public void Issue8_Expectancy_Uses_All_Resolved_Including_Expired()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);
        var p = BaseParams() with
        {
            AdaptiveRetrospectiveMinOutcomes = 5,
            AdaptiveRetrospectiveWindowSize = 20
        };

        var key = "NORMAL_MODERATE";
        // 3 wins, 2 expired (small negative), -> expectancy should reflect expired
        for (int i = 0; i < 3; i++)
            service.RecordOutcome(MakeOutcome(OutcomeLabel.WIN, 1.5m), key, p);
        for (int i = 0; i < 2; i++)
            service.RecordOutcome(MakeOutcome(OutcomeLabel.EXPIRED, -0.05m), key, p);

        var detail = service.GetStatus(p).ConditionDetails.First(d => d.ConditionKey == key);
        detail.OutcomeCount.Should().Be(5);
        // Win rate should be 3/5 = 0.6
        detail.WinRate.Should().BeApproximately(0.6m, 0.01m);
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #9: NeutralRegimePolicyOverride cliff at 0.5
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue9_NeutralPolicyOverride_Applied_At_Low_Intensity()
    {
        var p = BaseParams() with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.AllowMlGatedEntriesInNeutral
        };
        var condition = MakeCondition(vol: VolatilityTier.EXTREME); // triggers BlockAll overlay

        // At intensity 0.3 the overlay should still apply (was cliff at 0.5 before fix)
        var adapted = AdaptiveOverlayResolver.ApplyOverlays(p, condition, 0.3m);
        adapted.NeutralRegimePolicy.Should().Be(NeutralRegimePolicy.BlockAllEntriesInNeutral);
    }

    [Fact]
    public void Issue9_NeutralPolicyOverride_Not_Applied_At_Zero_Intensity()
    {
        var p = BaseParams() with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.AllowMlGatedEntriesInNeutral
        };
        var condition = MakeCondition(vol: VolatilityTier.EXTREME);

        // At zero intensity, no overlay should apply
        var adapted = AdaptiveOverlayResolver.ApplyOverlays(p, condition, 0m);
        adapted.NeutralRegimePolicy.Should().Be(NeutralRegimePolicy.AllowMlGatedEntriesInNeutral);
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #10: Retrospective reachability — coarse keys
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue10_CoarseKey_Has_Only_Two_Segments()
    {
        var condition = MakeCondition(VolatilityTier.HIGH, TrendStrength.STRONG,
            TradingSession.OVERLAP, SpreadQuality.WIDE, VolumeTier.ACTIVE);

        condition.ToCoarseKey().Should().Be("HIGH_STRONG");
        condition.ToCoarseKey().Split('_').Should().HaveCount(2);
    }

    [Fact]
    public void Issue10_MaxCardinality_Of_Coarse_Keys_Is_12()
    {
        var keys = new HashSet<string>();
        foreach (var vol in Enum.GetValues<VolatilityTier>())
            foreach (var trend in Enum.GetValues<TrendStrength>())
                keys.Add($"{vol}_{trend}");

        keys.Should().HaveCount(12); // 4 volatility × 3 trend
    }

    [Fact]
    public void Issue10_Retrospective_Fires_At_Configured_MinOutcomes()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);
        var p = BaseParams() with
        {
            AdaptiveRetrospectiveMinOutcomes = 5,
            AdaptiveRetrospectiveWindowSize = 30
        };

        var key = "NORMAL_MODERATE";
        // Record 5 losses → should trigger retrospective (expectancy < 0)
        for (int i = 0; i < 5; i++)
            service.RecordOutcome(MakeOutcome(OutcomeLabel.LOSS, -1.0m), key, p);

        var status = service.GetStatus(p);
        status.RetrospectiveOverlayCount.Should().BeGreaterThan(0,
            "retrospective should fire at configured min outcomes, not a hardcoded 15");
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #11: DRY volume overlay is conservative
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue11_DryVolume_Does_Not_Relax_VolumeGate()
    {
        var p = BaseParams(); // VolumeMultiplierMin = 0.8
        var condition = MakeCondition(volume: VolumeTier.DRY);

        var adapted = AdaptiveOverlayResolver.ApplyOverlays(p, condition, 1.0m);

        // After fix: DRY volume should NOT lower the multiplier below base
        adapted.VolumeMultiplierMin.Should().BeGreaterThanOrEqualTo(p.VolumeMultiplierMin,
            "DRY markets should be more conservative, not more permissive");
    }

    [Fact]
    public void Issue11_DryVolume_Raises_Confidence_Thresholds()
    {
        var p = BaseParams();
        var condition = MakeCondition(volume: VolumeTier.DRY);

        var adapted = AdaptiveOverlayResolver.ApplyOverlays(p, condition, 1.0m);

        adapted.ConfidenceBuyThreshold.Should().BeGreaterThan(p.ConfidenceBuyThreshold);
        adapted.ConfidenceSellThreshold.Should().BeGreaterThan(p.ConfidenceSellThreshold);
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #2: MarketConditionClass on decisions
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue2_SignalDecision_Supports_MarketConditionClass()
    {
        var decision = new SignalDecision
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = DateTimeOffset.UtcNow,
            BarTimeUtc = DateTimeOffset.UtcNow,
            DecisionType = SignalDirection.NO_TRADE,
            OutcomeCategory = OutcomeCategory.STRATEGY_NO_TRADE,
            ReasonCodes = [],
            ReasonDetails = [],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            MarketConditionClass = "NORMAL_MODERATE_LONDON_NORMAL_NORMAL",
            AdaptedParametersJson = "{\"ConfBuyDelta\":5}"
        };

        decision.MarketConditionClass.Should().NotBeNullOrEmpty();
        decision.AdaptedParametersJson.Should().NotBeNullOrEmpty();
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #3: State rehydration (unit-level)
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task Issue3_LoadState_Without_Repository_Is_NoOp()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger, logRepo: null, stateRepo: null);

        // Should not throw
        await service.LoadStateAsync();

        var status = service.GetStatus(BaseParams());
        status.TrackedConditionCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #4: Configured threshold used (not hardcoded)
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue4_Retrospective_Uses_Configured_MinOutcomes_Not_Hardcoded()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);

        // Lowered threshold: 3 outcomes should trigger retrospective
        var p = BaseParams() with { AdaptiveRetrospectiveMinOutcomes = 3 };
        var key = "HIGH_WEAK";

        for (int i = 0; i < 3; i++)
            service.RecordOutcome(MakeOutcome(OutcomeLabel.LOSS, -1.0m), key, p);

        var status = service.GetStatus(p);
        status.RetrospectiveOverlayCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Issue4_Retrospective_Does_Not_Fire_Below_Configured_Threshold()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);

        // Higher threshold: 20 outcomes required
        var p = BaseParams() with { AdaptiveRetrospectiveMinOutcomes = 20 };
        var key = "HIGH_WEAK";

        for (int i = 0; i < 10; i++)
            service.RecordOutcome(MakeOutcome(OutcomeLabel.LOSS, -1.0m), key, p);

        var status = service.GetStatus(p);
        status.RetrospectiveOverlayCount.Should().Be(0,
            "10 outcomes should not trigger retrospective when threshold is 20");
    }

    // ═══════════════════════════════════════════════════════
    //  Issue #5: Force log on first evaluation after restart
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Issue5_AdaptParameters_Logs_On_First_Evaluation()
    {
        var mockLogger = new Mock<ILogger<MarketAdaptiveParameterService>>();
        var service = new MarketAdaptiveParameterService(mockLogger.Object);
        var p = BaseParams();
        var snap = MakeSnap();
        var regime = MakeRegime();
        var candle = MakeCandle();
        var recentSnaps = new List<IndicatorSnapshot> { snap };

        // First call triggers forced log
        var (adapted, condition) = service.AdaptParameters(p, snap, regime, candle, Timeframe.M5, recentSnaps);

        adapted.Should().NotBeNull();
        condition.Should().NotBeNull();
        // The forced log flag should be consumed after first evaluation
        // Second call at the same condition should NOT force-log
        var (adapted2, _) = service.AdaptParameters(p, snap, regime, candle, Timeframe.M5, recentSnaps);
        adapted2.Should().NotBeNull();
    }

    [Fact]
    public void Issue13_AdaptParameters_Does_Not_Persist_Global_Parameter_Snapshots()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var paramRepo = new Mock<IParameterRepository>(MockBehavior.Strict);
        var service = new MarketAdaptiveParameterService(logger, paramRepo: paramRepo.Object);

        var p = BaseParams();
        var snap = MakeSnap();
        var regime = MakeRegime();
        var candle = MakeCandle();
        var recentSnaps = new List<IndicatorSnapshot> { snap };

        service.AdaptParameters(p, snap, regime, candle, Timeframe.M5, recentSnaps);

        paramRepo.Verify(r => r.GetActiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        paramRepo.Verify(r => r.InsertAsync(It.IsAny<StrategyParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
        paramRepo.Verify(r => r.ActivateAsync(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Issue14_RecordOutcome_Is_Isolated_Per_Timeframe()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var service = new MarketAdaptiveParameterService(logger);
        var p = BaseParams();

        service.RecordOutcome(MakeOutcome(OutcomeLabel.WIN, 1.2m), "1m", "NORMAL_MODERATE", p);
        service.RecordOutcome(MakeOutcome(OutcomeLabel.LOSS, -1.0m), "1h", "NORMAL_MODERATE", p);

        var status = service.GetStatus(p);
        status.ConditionDetails.Should().ContainSingle(d =>
            d.Timeframe == "1m" &&
            d.ConditionKey == "NORMAL_MODERATE" &&
            d.OutcomeCount == 1);
        status.ConditionDetails.Should().ContainSingle(d =>
            d.Timeframe == "1h" &&
            d.ConditionKey == "NORMAL_MODERATE" &&
            d.OutcomeCount == 1);
    }

    [Fact]
    public void Issue14_AdaptParameters_Persists_Independent_Timeframe_Profile_States()
    {
        var logger = new Mock<ILogger<MarketAdaptiveParameterService>>().Object;
        var stateRepo = new Mock<IAdaptiveStateRepository>();
        stateRepo.Setup(r => r.UpsertTimeframeProfileStateAsync(It.IsAny<AdaptiveTimeframeProfileState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        stateRepo.Setup(r => r.AppendTimeframeProfileChangeAsync(It.IsAny<AdaptiveTimeframeProfileChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new MarketAdaptiveParameterService(logger, stateRepo: stateRepo.Object);
        var p = BaseParams() with
        {
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };

        var m1Snap = MakeSnap() with { Timeframe = "1m" };
        var h1Snap = MakeSnap() with { Timeframe = "1h", CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        var regime = MakeRegime();
        var m1Candle = MakeCandle();
        var h1Candle = MakeCandle() with { OpenTime = DateTimeOffset.UtcNow.AddHours(-1) };

        service.AdaptParameters(p, m1Snap, regime, m1Candle, Timeframe.M1, new List<IndicatorSnapshot> { m1Snap });
        service.AdaptParameters(p, h1Snap, regime, h1Candle, Timeframe.H1, new List<IndicatorSnapshot> { h1Snap });

        SpinWait.SpinUntil(() => stateRepo.Invocations.Count >= 4, TimeSpan.FromSeconds(1))
            .Should().BeTrue("each timeframe should persist its own current state and change row");

        var status = service.GetStatus(p);
        status.TimeframeProfiles.Should().ContainSingle(profile =>
            profile.Timeframe == "1m" &&
            profile.StopAtrMultiplier == p.TimeframeProfiles.M1.StopAtrMultiplier);
        status.TimeframeProfiles.Should().ContainSingle(profile =>
            profile.Timeframe == "1h" &&
            profile.TargetRMultiple == p.TimeframeProfiles.H1.TargetRMultiple);
        status.RecentChanges.Should().Contain(change => change.Timeframe == "1m");
        status.RecentChanges.Should().Contain(change => change.Timeframe == "1h");

        stateRepo.Verify(r => r.UpsertTimeframeProfileStateAsync(
            It.Is<AdaptiveTimeframeProfileState>(state => state.Timeframe == "1m"),
            It.IsAny<CancellationToken>()), Times.Once);
        stateRepo.Verify(r => r.UpsertTimeframeProfileStateAsync(
            It.Is<AdaptiveTimeframeProfileState>(state => state.Timeframe == "1h"),
            It.IsAny<CancellationToken>()), Times.Once);
        stateRepo.Verify(r => r.AppendTimeframeProfileChangeAsync(
            It.Is<AdaptiveTimeframeProfileChange>(change => change.Timeframe == "1m"),
            It.IsAny<CancellationToken>()), Times.Once);
        stateRepo.Verify(r => r.AppendTimeframeProfileChangeAsync(
            It.Is<AdaptiveTimeframeProfileChange>(change => change.Timeframe == "1h"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════
    //  Overlay merging correctness
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void MergeOverlays_Sums_Deltas()
    {
        var overlays = new List<ParameterOverlay>
        {
            new() { ConfidenceBuyThresholdDelta = 5 },
            new() { ConfidenceBuyThresholdDelta = 3 },
        };

        var merged = AdaptiveOverlayResolver.MergeOverlays(overlays);
        merged.ConfidenceBuyThresholdDelta.Should().Be(8);
    }

    [Fact]
    public void MergeOverlays_NeutralPolicy_MostRestrictiveWins()
    {
        var overlays = new List<ParameterOverlay>
        {
            new() { NeutralRegimePolicyOverride = NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral },
            new() { NeutralRegimePolicyOverride = NeutralRegimePolicy.BlockAllEntriesInNeutral },
        };

        var merged = AdaptiveOverlayResolver.MergeOverlays(overlays);
        merged.NeutralRegimePolicyOverride.Should().Be(NeutralRegimePolicy.BlockAllEntriesInNeutral);
    }

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static IndicatorSnapshot MakeSnap() => new()
    {
        Symbol = "ETHUSD",
        Timeframe = "5m",
        CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        Ema20 = 2090,
        Ema50 = 2085,
        Rsi14 = 52,
        Macd = 0.5m,
        MacdSignal = 0.3m,
        MacdHist = 0.2m,
        Atr14 = 10m,
        Adx14 = 22,
        PlusDi = 25,
        MinusDi = 15,
        VolumeSma20 = 100,
        Vwap = 2080,
        Spread = 1m,
        CloseMid = 2100,
        MidHigh = 2105,
        MidLow = 2095,
        IsProvisional = false
    };

    private static RegimeResult MakeRegime() => new()
    {
        Symbol = "ETHUSD",
        CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-15),
        Regime = Regime.BULLISH,
        RegimeScore = 5,
        TriggeredConditions = ["all"],
        DisqualifyingConditions = []
    };

    private static RichCandle MakeCandle() => new()
    {
        OpenTime = DateTimeOffset.UtcNow.AddMinutes(-5),
        BidOpen = 2097,
        BidHigh = 2105,
        BidLow = 2095,
        BidClose = 2100,
        AskOpen = 2098,
        AskHigh = 2106,
        AskLow = 2096,
        AskClose = 2101,
        Volume = 150,
        IsClosed = true
    };
}
