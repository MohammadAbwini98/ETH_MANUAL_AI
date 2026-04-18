namespace EthSignal.Domain.Models;

/// <summary>Phase 6: Aggregated performance statistics.</summary>
public sealed record PerformanceStats
{
    public int TotalSignals { get; init; }
    public int ResolvedSignals { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Expired { get; init; }
    public int Ambiguous { get; init; }
    public decimal WinRate { get; init; }
    public decimal AverageR { get; init; }
    public decimal ProfitFactor { get; init; }
    public decimal TotalPnlR { get; init; }
}
