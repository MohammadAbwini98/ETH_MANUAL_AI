using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Rolling indicator history per timeframe for ML feature extraction.
///
/// Capacity is 300 bars per timeframe (25 h at 5 m) so that:
///  - prior-day session structure features can see yesterday's H/L/VWAP
///  - realized-vol 4h (48 bars) and ATR percentile (50 bars) have enough history
///
/// Call PreloadAsync once at startup to warm the buffer from the DB; after that
/// Add() keeps it current from live candle closes.
/// </summary>
internal sealed class MlRecentSnapshotBuffer
{
    // 300 bars = 25 h at 5 m — enough for prior-day context and 4h volatility
    private const int DefaultCapacity = 300;

    private readonly int _maxPerTimeframe;
    private readonly Dictionary<string, List<IndicatorSnapshot>> _buffers =
        new(StringComparer.OrdinalIgnoreCase);

    public MlRecentSnapshotBuffer(int maxPerTimeframe = DefaultCapacity)
    {
        _maxPerTimeframe = Math.Max(1, maxPerTimeframe);
    }

    public void Add(string timeframe, IndicatorSnapshot snapshot)
    {
        if (!_buffers.TryGetValue(timeframe, out var buffer))
        {
            buffer = [];
            _buffers[timeframe] = buffer;
        }

        buffer.Insert(0, snapshot);
        if (buffer.Count > _maxPerTimeframe)
            buffer.RemoveAt(_maxPerTimeframe);
    }

    /// <summary>
    /// Bulk-inserts historical snapshots (oldest-first) so the buffer is warm
    /// from the very first live candle close.  Snapshots that are already in the
    /// buffer (same timeframe + timestamp) are silently skipped.
    /// </summary>
    public void Preload(string timeframe, IReadOnlyList<IndicatorSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;

        if (!_buffers.TryGetValue(timeframe, out var buffer))
        {
            buffer = [];
            _buffers[timeframe] = buffer;
        }

        // snapshots arrives oldest-first from DB ORDER BY candle_open_time_utc ASC.
        // We need newest-first in the buffer, so iterate in reverse.
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            var snap = snapshots[i];
            // Skip duplicates (e.g. if Add() was already called for this bar)
            if (buffer.Any(b => b.CandleOpenTimeUtc == snap.CandleOpenTimeUtc))
                continue;
            buffer.Add(snap);
        }

        // Sort newest-first and trim to capacity
        buffer.Sort((a, b) => b.CandleOpenTimeUtc.CompareTo(a.CandleOpenTimeUtc));
        if (buffer.Count > _maxPerTimeframe)
            buffer.RemoveRange(_maxPerTimeframe, buffer.Count - _maxPerTimeframe);
    }

    public IReadOnlyList<IndicatorSnapshot> Get(string timeframe)
    {
        return _buffers.TryGetValue(timeframe, out var buffer)
            ? buffer.AsReadOnly()
            : Array.Empty<IndicatorSnapshot>();
    }
}
