namespace EthSignal.Domain.Models;

/// <summary>
/// One indicator snapshot per closed candle per timeframe.
/// Contains all Phase 2 indicators computed on mid-price candles.
/// </summary>
public sealed record IndicatorSnapshot
{
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset CandleOpenTimeUtc { get; init; }

    public decimal Ema20 { get; init; }
    public decimal Ema50 { get; init; }
    public decimal Rsi14 { get; init; }

    public decimal Macd { get; init; }
    public decimal MacdSignal { get; init; }
    public decimal MacdHist { get; init; }

    public decimal Atr14 { get; init; }
    public decimal Adx14 { get; init; }
    public decimal PlusDi { get; init; }
    public decimal MinusDi { get; init; }

    public decimal VolumeSma20 { get; init; }
    public decimal Vwap { get; init; }
    public decimal Spread { get; init; }

    public decimal CloseMid { get; init; }
    public decimal MidHigh { get; init; }
    public decimal MidLow { get; init; }

    public bool IsProvisional { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
