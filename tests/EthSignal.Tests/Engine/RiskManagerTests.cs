using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P5 tests: Risk Management and Price Targets.</summary>
public class RiskManagerTests
{
    private static readonly RiskPolicy Policy = new()
    {
        AccountBalanceUsd = 50m,
        RiskPercentPerTrade = 0.5m,
        AtrMultiplier = 0.8m,
        RewardToRisk = 1.5m,
        MinAtrThreshold = 0.5m,
        MaxSpreadPct = 0.003m,
        MinRiskRewardAfterRounding = 1.2m
    };

    /// <summary>P5-T1: Risk amount calculation.</summary>
    [Fact]
    public void RiskUsd_Computed_Correctly()
    {
        var result = RiskManager.ComputeRisk(
            SignalDirection.BUY, entryPrice: 2000m, atr: 10m,
            swingExtreme: 1990m, spreadPct: 0.001m, Policy);

        result.Allowed.Should().BeTrue();
        // RiskUsd = 50 * 0.5 / 100 = 0.25
        result.RiskUsd.Should().Be(0.25m);
        result.RiskPercent.Should().Be(0.5m);
    }

    /// <summary>P5-T2: BUY SL/TP computation.</summary>
    [Fact]
    public void BUY_SL_TP_Computed_Correctly()
    {
        // ATR=10, swingLow=1990 → ATR stop = 0.8*10 = 8, swing stop = 2000-1990 = 10
        // stopDistance = max(8, 10) = 10
        // SL = 2000 - 10 = 1990
        // TP = 2000 + 1.5*10 = 2015
        var result = RiskManager.ComputeRisk(
            SignalDirection.BUY, entryPrice: 2000m, atr: 10m,
            swingExtreme: 1990m, spreadPct: 0.001m, Policy);

        result.Allowed.Should().BeTrue();
        result.StopDistance.Should().Be(10m);
        result.StopLoss.Should().Be(1990m);
        result.TakeProfit.Should().Be(2015m);
    }

    /// <summary>P5-T3: SELL SL/TP computation.</summary>
    [Fact]
    public void SELL_SL_TP_Computed_Correctly()
    {
        // ATR=10, swingHigh=2010 → ATR stop = 8, swing stop = 2010-2000 = 10
        // stopDistance = max(8, 10) = 10
        // SL = 2000 + 10 = 2010
        // TP = 2000 - 1.5*10 = 1985
        var result = RiskManager.ComputeRisk(
            SignalDirection.SELL, entryPrice: 2000m, atr: 10m,
            swingExtreme: 2010m, spreadPct: 0.001m, Policy);

        result.Allowed.Should().BeTrue();
        result.StopDistance.Should().Be(10m);
        result.StopLoss.Should().Be(2010m);
        result.TakeProfit.Should().Be(1985m);
    }

    /// <summary>P5-T4: Safety block — excessive spread.</summary>
    [Fact]
    public void Excessive_Spread_Blocks_Trade()
    {
        var result = RiskManager.ComputeRisk(
            SignalDirection.BUY, entryPrice: 2000m, atr: 10m,
            swingExtreme: 1990m, spreadPct: 0.005m, Policy);

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("SpreadPct");
    }

    /// <summary>P5-T4: Safety block — low ATR.</summary>
    [Fact]
    public void Low_ATR_Blocks_Trade()
    {
        var result = RiskManager.ComputeRisk(
            SignalDirection.BUY, entryPrice: 2000m, atr: 0.3m,
            swingExtreme: 1999m, spreadPct: 0.001m, Policy);

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("ATR");
    }

    [Fact]
    public void NO_TRADE_Direction_Blocked()
    {
        var result = RiskManager.ComputeRisk(
            SignalDirection.NO_TRADE, entryPrice: 2000m, atr: 10m,
            swingExtreme: 1990m, spreadPct: 0.001m, Policy);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void ATR_Stop_Used_When_Larger_Than_Swing()
    {
        // ATR=20, swingLow=1995 → ATR stop = 0.8*20 = 16, swing stop = 2000-1995 = 5
        // stopDistance = max(16, 5) = 16
        var result = RiskManager.ComputeRisk(
            SignalDirection.BUY, entryPrice: 2000m, atr: 20m,
            swingExtreme: 1995m, spreadPct: 0.001m, Policy);

        result.StopDistance.Should().Be(16m);
        result.StopLoss.Should().Be(1984m);
        result.TakeProfit.Should().Be(2024m);
    }

    [Fact]
    public void EstimateLiveFillPrice_Uses_Wider_Buffer_For_Fast_LowConfidence_Than_Long_HighConfidence()
    {
        var parameters = StrategyParameters.Default;

        var fastLowConfidence = RiskManager.EstimateLiveFillPrice(
            SignalDirection.BUY, 2000m, 0.001m, parameters, "1m", confidenceScore: 45, atr: 18m);
        var longHighConfidence = RiskManager.EstimateLiveFillPrice(
            SignalDirection.BUY, 2000m, 0.001m, parameters, "1h", confidenceScore: 90, atr: 18m);

        fastLowConfidence.Should().BeGreaterThan(longHighConfidence);
    }
}
