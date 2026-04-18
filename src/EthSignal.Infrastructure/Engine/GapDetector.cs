using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

public static class GapDetector
{
    /// <summary>
    /// Detects missing candle slots within a time range.
    /// Compares expected timestamps (every tf.Minutes from 'from' to 'to')
    /// against actual timestamps found in the database.
    /// </summary>
    public static List<GapEvent> DetectGaps(
        string symbol,
        Timeframe tf,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<DateTimeOffset> actualTimes,
        string gapSource = "LIVE")
    {
        var actualSet = new HashSet<DateTimeOffset>(actualTimes);
        var gaps = new List<GapEvent>();
        var now = DateTimeOffset.UtcNow;

        var expected = tf.Floor(from);
        while (expected < to)
        {
            if (!actualSet.Contains(expected))
            {
                // Find the next actual time after the missing slot
                DateTimeOffset? nextActual = null;
                var search = expected.Add(tf.Duration);
                while (search < to)
                {
                    if (actualSet.Contains(search))
                    {
                        nextActual = search;
                        break;
                    }
                    search = search.Add(tf.Duration);
                }

                gaps.Add(new GapEvent(
                    Symbol: symbol,
                    TimeframeName: tf.Name,
                    ExpectedTime: expected,
                    ActualNextTime: nextActual,
                    GapDuration: nextActual.HasValue ? nextActual.Value - expected : tf.Duration,
                    GapType: "missing_candle",
                    DetectedAtUtc: now,
                    GapSource: gapSource));
            }

            expected = expected.Add(tf.Duration);
        }

        return gaps;
    }
}
