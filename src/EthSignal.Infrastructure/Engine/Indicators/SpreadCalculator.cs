namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Spread_t = Ask_t - Bid_t
/// SpreadPct_t = Spread_t / Mid_t
/// Returns absolute spread (Ask - Bid close).
/// </summary>
public static class SpreadCalculator
{
    public static decimal[] Calculate(
        IReadOnlyList<decimal> bidCloses,
        IReadOnlyList<decimal> askCloses)
    {
        int count = bidCloses.Count;
        var result = new decimal[count];
        for (int i = 0; i < count; i++)
            result[i] = askCloses[i] - bidCloses[i];
        return result;
    }
}
