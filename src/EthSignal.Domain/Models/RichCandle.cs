namespace EthSignal.Domain.Models;

/// <summary>
/// Market candle with separate bid/ask/mid OHLC, volume, sentiment, and provenance timestamps.
/// </summary>
public sealed record RichCandle
{
    public required DateTimeOffset OpenTime { get; init; }

    // Bid OHLC
    public required decimal BidOpen { get; init; }
    public required decimal BidHigh { get; init; }
    public required decimal BidLow { get; init; }
    public required decimal BidClose { get; init; }

    // Ask OHLC
    public required decimal AskOpen { get; init; }
    public required decimal AskHigh { get; init; }
    public required decimal AskLow { get; init; }
    public required decimal AskClose { get; init; }

    // Mid OHLC (stored explicitly for query convenience)
    public decimal MidOpen => (BidOpen + AskOpen) / 2m;
    public decimal MidHigh => (BidHigh + AskHigh) / 2m;
    public decimal MidLow => (BidLow + AskLow) / 2m;
    public decimal MidClose => (BidClose + AskClose) / 2m;

    public decimal Volume { get; init; }
    public decimal BuyerPct { get; init; }
    public decimal SellerPct { get; init; }

    public DateTimeOffset? SourceTimestampUtc { get; init; }
    public DateTimeOffset ReceivedTimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsClosed { get; init; } = true;

    /// <summary>
    /// U-05 FIX: Returns true if OHLC invariants hold for all price streams.
    /// For each stream: High >= max(Open, Close) and Low <= min(Open, Close) and High >= Low.
    /// </summary>
    public bool IsOhlcValid()
    {
        return ValidateStream(BidOpen, BidHigh, BidLow, BidClose)
            && ValidateStream(AskOpen, AskHigh, AskLow, AskClose);
    }

    /// <summary>
    /// U-05 FIX: Repairs OHLC invariant violations by enforcing
    /// High = max(Open, High, Close) and Low = min(Open, Low, Close).
    /// Returns a corrected copy. No-op if already valid.
    /// </summary>
    public RichCandle RepairOhlc()
    {
        return this with
        {
            BidHigh = Math.Max(BidHigh, Math.Max(BidOpen, BidClose)),
            BidLow  = Math.Min(BidLow, Math.Min(BidOpen, BidClose)),
            AskHigh = Math.Max(AskHigh, Math.Max(AskOpen, AskClose)),
            AskLow  = Math.Min(AskLow, Math.Min(AskOpen, AskClose))
        };
    }

    private static bool ValidateStream(decimal open, decimal high, decimal low, decimal close)
        => high >= Math.Max(open, close)
        && low <= Math.Min(open, close)
        && high >= low;
}
