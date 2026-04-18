using EthSignal.Domain.Models;

namespace EthSignal.Domain.Validation;

public static class CandleValidator
{
    public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

    /// <summary>
    /// Validates OHLC invariants on all three price types (bid, ask, mid).
    /// Rules per SRS 7.1.4:
    ///   Low &lt;= Open &lt;= High
    ///   Low &lt;= Close &lt;= High
    ///   High &gt;= Low
    ///   Volume &gt;= 0
    /// </summary>
    public static ValidationResult Validate(RichCandle candle)
    {
        var errors = new List<string>();

        ValidateOhlc(errors, "Bid", candle.BidOpen, candle.BidHigh, candle.BidLow, candle.BidClose);
        ValidateOhlc(errors, "Ask", candle.AskOpen, candle.AskHigh, candle.AskLow, candle.AskClose);
        ValidateOhlc(errors, "Mid", candle.MidOpen, candle.MidHigh, candle.MidLow, candle.MidClose);

        if (candle.Volume < 0)
            errors.Add("Volume is negative.");

        if (candle.AskOpen < candle.BidOpen)
            errors.Add("Ask open is less than bid open (negative spread).");

        return new ValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateOhlc(List<string> errors, string label, decimal open, decimal high, decimal low, decimal close)
    {
        if (high < low)
            errors.Add($"{label}: High ({high}) < Low ({low}).");
        if (high < open)
            errors.Add($"{label}: High ({high}) < Open ({open}).");
        if (high < close)
            errors.Add($"{label}: High ({high}) < Close ({close}).");
        if (low > open)
            errors.Add($"{label}: Low ({low}) > Open ({open}).");
        if (low > close)
            errors.Add($"{label}: Low ({low}) > Close ({close}).");
    }
}
