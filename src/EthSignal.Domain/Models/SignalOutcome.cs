namespace EthSignal.Domain.Models;

public enum OutcomeLabel { PENDING, WIN, LOSS, EXPIRED, AMBIGUOUS }

/// <summary>Phase 6: Signal outcome after evaluating future candles.</summary>
public sealed record SignalOutcome
{
    public required Guid SignalId { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int BarsObserved { get; init; }
    public bool TpHit { get; init; }
    public bool SlHit { get; init; }
    public bool PartialWin { get; init; }
    public required OutcomeLabel OutcomeLabel { get; init; }

    public decimal PnlR { get; init; }
    public decimal MfePrice { get; init; }
    public decimal MaePrice { get; init; }
    public decimal MfeR { get; init; }
    public decimal MaeR { get; init; }
    public DateTimeOffset? ClosedAtUtc { get; init; }
}
