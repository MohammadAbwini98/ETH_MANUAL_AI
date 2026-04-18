namespace EthSignal.Domain.Models;

public enum Regime
{
    NEUTRAL,
    BULLISH,
    BEARISH
}

/// <summary>
/// Result of 15m regime classification.
/// Produced once per closed 15m candle.
/// </summary>
public sealed record RegimeResult
{
    public required string Symbol { get; init; }
    public required DateTimeOffset CandleOpenTimeUtc { get; init; }
    public required Regime Regime { get; init; }

    /// <summary>Score 0-5: how many bullish/bearish conditions were met.</summary>
    public int RegimeScore { get; init; }

    /// <summary>Conditions that were satisfied.</summary>
    public required IReadOnlyList<string> TriggeredConditions { get; init; }

    /// <summary>Conditions that were NOT satisfied (preventing classification).</summary>
    public required IReadOnlyList<string> DisqualifyingConditions { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
