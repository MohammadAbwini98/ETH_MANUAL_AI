using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

public sealed class ExitEngineTests
{
    private static readonly ExitEngine.ExitPolicy IntradayPolicy = new()
    {
        AtrMultiplier = 1.0m,
        SpreadSlippageBufferPct = 0m,
        MinStopDistancePct = 0.001m,
        MaxStopDistancePct = 0.05m,
        MinRewardToRisk = 1.2m,
        DefaultRewardToRisk = 1.4m,
        Tp1RMultiple = 1.0m,
        Tp2RMultiple = 2.0m,
        Tp3RMultiple = 3.0m,
        TrendingTpMultiplier = 1.0m,
        TrendingSlMultiplier = 1.0m,
        RangingTpMultiplier = 1.0m,
        RangingSlMultiplier = 1.0m,
        HighConfidenceTpBoost = 1.0m,
        LowConfidenceTpReduce = 1.0m,
        HighConfidenceThreshold = 80,
        LowConfidenceThreshold = 55,
        ScalpMinAtrTpMultiplier = 0.8m,
        ScalpMaxAtrTpMultiplier = 1.5m,
        IntradayMinAtrTpMultiplier = 1.5m,
        IntradayMaxAtrTpMultiplier = 3.0m,
        StructureBufferAtrMultiplier = 0m,
        AccountBalanceUsd = 50m,
        RiskPercentPerTrade = 0.5m,
        HardMaxRiskPercent = 1.0m,
        MinAtrThreshold = 0.5m,
        MaxSpreadPct = 0.01m
    };

    [Fact]
    public void Compute_Uses_Next_Structure_Target_When_Nearest_Target_Is_Microstructure_On_Higher_Timeframe()
    {
        var result = ExitEngine.Compute(
            new ExitEngine.ExitContext
            {
                Direction = SignalDirection.BUY,
                EntryPrice = 2000m,
                Atr = 10m,
                SpreadPct = 0.001m,
                ConfidenceScore = 70,
                Regime = Regime.BULLISH,
                Timeframe = "15m",
                SwingExtreme = 1994m,
                Structure = new StructureAnalyzer.StructureLevels
                {
                    SwingLows = [1994m],
                    SwingHighs = [2004m, 2020m],
                    ResistanceZones = [2004m, 2020m],
                    SupportZones = [1994m],
                    NearestResistance = 2004m,
                    SecondResistance = 2020m,
                    NearestSupport = 1994m
                }
            },
            IntradayPolicy);

        result.Allowed.Should().BeTrue();
        result.Tp2.Should().Be(2020m);
        result.Explanation.Should().Contain("using next structure target");
    }

    [Fact]
    public void Compute_Falls_Back_To_Atr_Based_Tp_When_Only_Microstructure_Exists_On_Higher_Timeframe()
    {
        var result = ExitEngine.Compute(
            new ExitEngine.ExitContext
            {
                Direction = SignalDirection.BUY,
                EntryPrice = 2000m,
                Atr = 10m,
                SpreadPct = 0.001m,
                ConfidenceScore = 70,
                Regime = Regime.BULLISH,
                Timeframe = "30m",
                SwingExtreme = 1994m,
                Structure = new StructureAnalyzer.StructureLevels
                {
                    SwingLows = [1994m],
                    SwingHighs = [2004m],
                    ResistanceZones = [2004m],
                    SupportZones = [1994m],
                    NearestResistance = 2004m,
                    NearestSupport = 1994m
                }
            },
            IntradayPolicy);

        result.Allowed.Should().BeTrue();
        result.TakeProfit.Should().Be(2015m);
        result.ExitModel.Should().Be("STRUCTURE_SL_ONLY");
        result.Explanation.Should().Contain("falling back to ATR-based TP");
    }

    [Fact]
    public void Compute_Rejects_Microstructure_On_Scalp_When_No_Meaningful_Target_Exists()
    {
        var result = ExitEngine.Compute(
            new ExitEngine.ExitContext
            {
                Direction = SignalDirection.BUY,
                EntryPrice = 2000m,
                Atr = 10m,
                SpreadPct = 0.001m,
                ConfidenceScore = 70,
                Regime = Regime.BULLISH,
                Timeframe = "1m",
                SwingExtreme = 1994m,
                Structure = new StructureAnalyzer.StructureLevels
                {
                    SwingLows = [1994m],
                    SwingHighs = [2004m],
                    ResistanceZones = [2004m],
                    SupportZones = [1994m],
                    NearestResistance = 2004m,
                    NearestSupport = 1994m
                }
            },
            IntradayPolicy);

        result.Allowed.Should().BeFalse();
        result.RejectReason.Should().Contain("Structure target too close");
    }

    [Fact]
    public void Compute_Clamps_Atr_Target_To_Timeframe_Band_When_Stop_Distance_Is_Too_Wide()
    {
        var policy = IntradayPolicy with
        {
            AtrMultiplier = 2.0m,
            DefaultRewardToRisk = 4.0m
        };

        var result = ExitEngine.Compute(
            new ExitEngine.ExitContext
            {
                Direction = SignalDirection.BUY,
                EntryPrice = 2000m,
                Atr = 10m,
                SpreadPct = 0.001m,
                ConfidenceScore = 70,
                Regime = Regime.BULLISH,
                Timeframe = "15m",
                SwingExtreme = 1995m
            },
            policy);

        result.Allowed.Should().BeTrue();
        result.TakeProfit.Should().Be(2030m);
        result.RiskRewardRatio.Should().Be(1.5m);
    }

    [Fact]
    public void BuildPolicy_Clamps_Minimum_Reward_To_Risk_To_Timeframe_Target()
    {
        var parameters = StrategyParameters.Default with
        {
            MinRiskRewardAfterRounding = 1.5m,
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };

        var policy = ExitEngine.BuildPolicy(parameters, "5m");

        policy.DefaultRewardToRisk.Should().Be(1.4m);
        policy.MinRewardToRisk.Should().Be(1.4m);
    }

    [Fact]
    public void BuildScalpPolicy_Does_Not_Require_More_Than_The_Scalp_Target()
    {
        var parameters = StrategyParameters.Default with
        {
            MinRiskRewardAfterRounding = 1.8m,
            ScalpTargetRMultiple = 1.35m
        };

        var policy = ExitEngine.BuildScalpPolicy(parameters);

        policy.DefaultRewardToRisk.Should().Be(1.35m);
        policy.MinRewardToRisk.Should().Be(1.35m);
    }
}
