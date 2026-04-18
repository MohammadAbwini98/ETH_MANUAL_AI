using System.Runtime.CompilerServices;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Apis;

/// <summary>
/// Decorator: wraps any ITickProvider and discards price spikes.
/// A spike is defined as |newMid - prevMid| / prevMid > maxDeviationPct.
/// Default: 0.5% — normal ETH spread is 0.01–0.05%.
/// To avoid latching forever on a stale reference, the filter can rebase after
/// a long quiet gap once several consistent outlier ticks confirm the new level.
/// </summary>
public sealed class TickSpikeFilter : ITickProvider
{
    private const int RebaseConfirmTicks = 3;
    private static readonly TimeSpan RebaseGapThreshold = TimeSpan.FromSeconds(30);
    private const decimal RebaseClusterPctFloor = 0.20m;

    private readonly ITickProvider _inner;
    private readonly decimal       _maxDeviationPct;
    private readonly ILogger<TickSpikeFilter> _logger;

    public TickProviderKind Kind      => _inner.Kind;
    public bool             IsHealthy => _inner.IsHealthy;
    public double           TickRateHz => _inner.TickRateHz;

    public TickSpikeFilter(ITickProvider inner, decimal maxDeviationPct,
        ILogger<TickSpikeFilter> logger)
    {
        _inner           = inner;
        _maxDeviationPct = maxDeviationPct;
        _logger          = logger;
    }

    public Task StartAsync(string epic, CancellationToken ct) =>
        _inner.StartAsync(epic, ct);

    public async IAsyncEnumerable<SpotPrice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        SpotPrice? prev = null;
        SpotPrice? rebaseCandidate = null;
        var rebaseCandidateCount = 0;
        var rebaseDirection = 0;

        await foreach (var spot in _inner.ReadAllAsync(ct))
        {
            if (prev == null)
            {
                prev = spot;
                yield return spot;
                continue;
            }

            if (prev.Mid == 0m)
            {
                prev = spot;
                yield return spot;
                continue;
            }

            var deviation = GetDeviationPct(spot.Mid, prev.Mid);
            if (deviation > _maxDeviationPct)
            {
                if (TryConfirmRebase(
                    prev,
                    spot,
                    ref rebaseCandidate,
                    ref rebaseCandidateCount,
                    ref rebaseDirection))
                {
                    _logger.LogWarning(
                        "[SpikeFilter] Rebasing after stale gap: prev={Prev:F2} new={New:F2} deviation={Dev:F2}% confirmations={Count} gap={GapSec:F0}s",
                        prev.Mid, spot.Mid, deviation, rebaseCandidateCount,
                        (spot.Timestamp - prev.Timestamp).TotalSeconds);

                    prev = spot;
                    rebaseCandidate = null;
                    rebaseCandidateCount = 0;
                    rebaseDirection = 0;
                    yield return spot;
                    continue;
                }

                _logger.LogWarning(
                    "[SpikeFilter] Tick dropped: prev={Prev:F2} new={New:F2} deviation={Dev:F2}%",
                    prev.Mid, spot.Mid, deviation);
                // Do not update prev for isolated outliers; keep previous valid price as reference.
                continue;
            }

            rebaseCandidate = null;
            rebaseCandidateCount = 0;
            rebaseDirection = 0;
            prev = spot;
            yield return spot;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private bool TryConfirmRebase(
        SpotPrice prev,
        SpotPrice current,
        ref SpotPrice? candidate,
        ref int candidateCount,
        ref int candidateDirection)
    {
        if (current.Timestamp - prev.Timestamp < RebaseGapThreshold)
        {
            candidate = null;
            candidateCount = 0;
            candidateDirection = 0;
            return false;
        }

        var direction = Math.Sign(current.Mid - prev.Mid);
        if (direction == 0)
        {
            candidate = null;
            candidateCount = 0;
            candidateDirection = 0;
            return false;
        }

        var clusterTolerancePct = Math.Max(RebaseClusterPctFloor, _maxDeviationPct / 2m);
        if (candidate != null && candidateDirection == direction)
        {
            var candidateDeviation = GetDeviationPct(current.Mid, candidate.Mid);
            if (candidateDeviation <= clusterTolerancePct)
            {
                candidate = current;
                candidateCount++;
            }
            else
            {
                candidate = current;
                candidateCount = 1;
                candidateDirection = direction;
            }
        }
        else
        {
            candidate = current;
            candidateCount = 1;
            candidateDirection = direction;
        }

        return candidateCount >= RebaseConfirmTicks;
    }

    private static decimal GetDeviationPct(decimal currentMid, decimal referenceMid) =>
        Math.Abs(currentMid - referenceMid) / referenceMid * 100m;
}
