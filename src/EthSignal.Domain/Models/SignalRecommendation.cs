namespace EthSignal.Domain.Models;

public enum SignalDirection { NO_TRADE, BUY, SELL }
public enum SignalStatus { OPEN, CLOSED, EXPIRED }

/// <summary>Phase 4: Trading signal recommendation.</summary>
public sealed record SignalRecommendation
{
    public Guid SignalId { get; init; } = Guid.NewGuid();
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset SignalTimeUtc { get; init; }
    public required SignalDirection Direction { get; init; }

    public decimal EntryPrice { get; init; }
    public decimal TpPrice { get; init; }
    public decimal SlPrice { get; init; }
    public decimal RiskPercent { get; init; }
    public decimal RiskUsd { get; init; }
    public int ConfidenceScore { get; init; }

    /// <summary>Multi-target TP1 (early partial profit).</summary>
    public decimal Tp1Price { get; init; }
    /// <summary>Multi-target TP2 (structure target).</summary>
    public decimal Tp2Price { get; init; }
    /// <summary>Multi-target TP3 (trailing runner anchor).</summary>
    public decimal Tp3Price { get; init; }

    /// <summary>Actual risk-to-reward ratio after all adjustments.</summary>
    public decimal RiskRewardRatio { get; init; }

    /// <summary>Exit model used: STRUCTURE_FULL, STRUCTURE_SL_ONLY, ATR_BASED, REJECTED.</summary>
    public string? ExitModel { get; init; }

    /// <summary>Human-readable explanation of exit level selection (for dashboard/audit).</summary>
    public string? ExitExplanation { get; init; }

    public required Regime Regime { get; init; }
    public string StrategyVersion { get; init; } = "v1.0";
    public required IReadOnlyList<string> Reasons { get; init; }
    public SignalStatus Status { get; init; } = SignalStatus.OPEN;

    /// <summary>Market condition class active at signal generation (for retrospective analysis).</summary>
    public string? MarketConditionClass { get; init; }

    /// <summary>FR-16: Correlation key spanning feature extraction, prediction, decision, signal.</summary>
    public Guid? EvaluationId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
