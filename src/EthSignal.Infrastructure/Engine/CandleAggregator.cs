using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

public static class CandleAggregator
{
    /// <summary>
    /// Aggregates 1m RichCandles into a higher timeframe.
    /// Per SRS 7.1.2: O=first, H=max, L=min, C=last, V=sum.
    /// Applied independently to bid, ask (mid is computed).
    /// </summary>
    public static List<RichCandle> Aggregate(IReadOnlyList<RichCandle> minuteCandles, Timeframe tf)
    {
        if (tf.Minutes == 1)
            return minuteCandles.ToList();

        var groups = new Dictionary<DateTimeOffset, List<RichCandle>>();
        foreach (var c in minuteCandles)
        {
            var key = tf.Floor(c.OpenTime);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(c);
        }

        var result = new List<RichCandle>(groups.Count);
        foreach (var (key, group) in groups)
        {
            group.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
            var first = group[0];
            var last = group[^1];

            result.Add(new RichCandle
            {
                OpenTime = key,

                BidOpen  = first.BidOpen,
                BidHigh  = group.Max(c => c.BidHigh),
                BidLow   = group.Min(c => c.BidLow),
                BidClose = last.BidClose,

                AskOpen  = first.AskOpen,
                AskHigh  = group.Max(c => c.AskHigh),
                AskLow   = group.Min(c => c.AskLow),
                AskClose = last.AskClose,

                Volume    = group.Sum(c => c.Volume),
                BuyerPct  = last.BuyerPct,
                SellerPct = last.SellerPct,

                SourceTimestampUtc = last.SourceTimestampUtc,
                ReceivedTimestampUtc = last.ReceivedTimestampUtc
            });
        }

        result.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));

        // U-05 FIX: Repair any OHLC violations caused by corrupted source 1m candles
        for (int i = 0; i < result.Count; i++)
        {
            if (!result[i].IsOhlcValid())
                result[i] = result[i].RepairOhlc();
        }

        return result;
    }
}
