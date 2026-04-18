using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface ICandleRepository
{
    Task<int> BulkUpsertAsync(Timeframe tf, string symbol, IReadOnlyList<RichCandle> candles, CancellationToken ct = default);
    Task<RichCandle?> GetOpenCandleAsync(Timeframe tf, string symbol, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLatestClosedTimeAsync(Timeframe tf, string symbol, CancellationToken ct = default);

    /// <summary>Startup sync planning: count closed+open candles for the symbol/timeframe.</summary>
    Task<long> CountCandlesAsync(Timeframe tf, string symbol, CancellationToken ct = default);

    /// <summary>Startup sync planning: earliest closed candle open time for the symbol/timeframe.</summary>
    Task<DateTimeOffset?> GetEarliestClosedTimeAsync(Timeframe tf, string symbol, CancellationToken ct = default);

    Task UpsertOpenCandlesAsync(string symbol, Dictionary<Timeframe, RichCandle> candles, CancellationToken ct = default);
    Task CloseCandlesAsync(string symbol, IReadOnlyList<(Timeframe Tf, RichCandle Final)> toClose, CancellationToken ct = default);
    Task CloseAllOpenAsync(string symbol, CancellationToken ct = default);
    Task CloseOpenCandlesBeforeAsync(string symbol, DateTimeOffset boundary, CancellationToken ct = default);
    Task<IReadOnlyList<DateTimeOffset>> GetCandleTimesAsync(Timeframe tf, string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<RichCandle>> GetClosedCandlesAsync(Timeframe tf, string symbol, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<RichCandle>> GetClosedCandlesInRangeAsync(Timeframe tf, string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<RichCandle>> GetClosedCandlesAfterAsync(Timeframe tf, string symbol, DateTimeOffset after, int limit, CancellationToken ct = default);

    /// <summary>U-05: Scan and repair closed candles with invalid OHLC. Returns count of repaired rows.</summary>
    Task<int> RepairInvalidOhlcAsync(Timeframe tf, string symbol, CancellationToken ct = default);
}
