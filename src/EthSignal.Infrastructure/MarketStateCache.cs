using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;

namespace EthSignal.Infrastructure;

/// <summary>
/// Thread-safe in-memory cache of latest market state.
/// The live tick processor writes; dashboard API endpoints read.
/// This eliminates duplicate broker API calls from the dashboard.
/// </summary>
public sealed class MarketStateCache
{
    private SpotPrice? _latestSpot;
    private DateTimeOffset? _lastTickTime;
    private DateTimeOffset? _lastCandleCloseTime;
    private string? _lastCandleTimeframe;
    private int _tickCount;
    private string? _lastError;
    private double _tickRateHz;
    private TickProviderKind _tickProviderKind;
    private readonly object _lock = new();

    // Indicator cache — updated by LiveTickProcessor every ~1s
    private readonly Dictionary<string, IndicatorSnapshot> _indicatorCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-TF regime cache — updated on each TF candle close
    private readonly Dictionary<string, RegimeResult> _regimeCache = new(StringComparer.OrdinalIgnoreCase);

    public SpotPrice? LatestSpot
    {
        get { lock (_lock) return _latestSpot; }
        set { lock (_lock) { _latestSpot = value; _lastTickTime = DateTimeOffset.UtcNow; _tickCount++; } }
    }

    public void RecordCandleClose(string timeframe, DateTimeOffset candleTime)
    {
        lock (_lock) { _lastCandleCloseTime = candleTime; _lastCandleTimeframe = timeframe; }
    }

    public void RecordError(string message)
    {
        lock (_lock) _lastError = message;
    }

    public void RecordTickMetrics(double tickRateHz, TickProviderKind kind)
    {
        lock (_lock)
        {
            _tickRateHz = tickRateHz;
            _tickProviderKind = kind;
        }
    }

    public HealthInfo GetHealthInfo()
    {
        lock (_lock) return new HealthInfo(
            _lastTickTime, _tickCount, _lastCandleCloseTime, _lastCandleTimeframe, _lastError,
            _tickRateHz,
            _tickProviderKind.ToString());
    }

    public void UpdateIndicator(string timeframe, IndicatorSnapshot snapshot)
    {
        lock (_lock)
            _indicatorCache[timeframe] = snapshot;
    }

    public IndicatorSnapshot? GetCachedIndicator(string timeframe)
    {
        lock (_lock)
            return _indicatorCache.TryGetValue(timeframe, out var snap) ? snap : null;
    }

    public IReadOnlyDictionary<string, IndicatorSnapshot> GetIndicatorCacheSnapshot()
    {
        lock (_lock)
            return new Dictionary<string, IndicatorSnapshot>(_indicatorCache, StringComparer.OrdinalIgnoreCase);
    }

    public void UpdateRegime(string timeframe, RegimeResult regime)
    {
        lock (_lock)
            _regimeCache[timeframe] = regime;
    }

    public RegimeResult? GetCachedRegime(string timeframe)
    {
        lock (_lock)
            return _regimeCache.TryGetValue(timeframe, out var regime) ? regime : null;
    }

    public IReadOnlyDictionary<string, RegimeResult> GetRegimeCacheSnapshot()
    {
        lock (_lock)
            return new Dictionary<string, RegimeResult>(_regimeCache, StringComparer.OrdinalIgnoreCase);
    }

    public record HealthInfo(
        DateTimeOffset? LastTickTime,
        int TickCount,
        DateTimeOffset? LastCandleCloseTime,
        string? LastCandleTimeframe,
        string? LastError,
        double TickRateHz,
        string TickProviderKind);
}
