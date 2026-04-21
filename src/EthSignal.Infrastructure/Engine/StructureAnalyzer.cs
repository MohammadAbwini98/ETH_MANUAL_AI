using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Detects market structure levels (swing highs/lows, S/R zones) from candle history.
/// Used by <see cref="ExitEngine"/> to set structure-aware TP and SL.
/// </summary>
public static class StructureAnalyzer
{
    /// <summary>Result of structure analysis on a candle series.</summary>
    public sealed record StructureLevels
    {
        /// <summary>Recent swing lows sorted ascending by price.</summary>
        public required IReadOnlyList<decimal> SwingLows { get; init; }

        /// <summary>Recent swing highs sorted ascending by price.</summary>
        public required IReadOnlyList<decimal> SwingHighs { get; init; }

        /// <summary>All resistance zones above current price, ordered nearest-first.</summary>
        public IReadOnlyList<decimal> ResistanceZones { get; init; } = [];

        /// <summary>All support zones below current price, ordered nearest-first.</summary>
        public IReadOnlyList<decimal> SupportZones { get; init; } = [];

        /// <summary>Nearest support zone below current price (0 if none found).</summary>
        public decimal NearestSupport { get; init; }

        /// <summary>Nearest resistance zone above current price (0 if none found).</summary>
        public decimal NearestResistance { get; init; }

        /// <summary>Second resistance above current price (0 if none found).</summary>
        public decimal SecondResistance { get; init; }

        /// <summary>Second support below current price (0 if none found).</summary>
        public decimal SecondSupport { get; init; }
    }

    /// <summary>
    /// Detect swing highs and lows using a left/right shoulder approach.
    /// A swing high is a candle whose high is higher than <paramref name="shoulderBars"/> candles on each side.
    /// </summary>
    public static StructureLevels Analyze(
        IReadOnlyList<RichCandle> candles,
        decimal currentPrice,
        int shoulderBars = 2)
    {
        if (candles.Count < shoulderBars * 2 + 1)
            return new StructureLevels
            {
                SwingLows = [],
                SwingHighs = [],
            };

        var swingHighs = new List<decimal>();
        var swingLows = new List<decimal>();

        for (int i = shoulderBars; i < candles.Count - shoulderBars; i++)
        {
            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int j = 1; j <= shoulderBars; j++)
            {
                if (candles[i].MidHigh <= candles[i - j].MidHigh ||
                    candles[i].MidHigh <= candles[i + j].MidHigh)
                    isSwingHigh = false;

                if (candles[i].MidLow >= candles[i - j].MidLow ||
                    candles[i].MidLow >= candles[i + j].MidLow)
                    isSwingLow = false;

                if (!isSwingHigh && !isSwingLow) break;
            }

            if (isSwingHigh) swingHighs.Add(candles[i].MidHigh);
            if (isSwingLow) swingLows.Add(candles[i].MidLow);
        }

        swingHighs.Sort();
        swingLows.Sort();

        // Cluster nearby levels (within 0.15% of each other) to find zones
        var resistanceZones = ClusterLevels(swingHighs, currentPrice, 0.0015m);
        var supportZones = ClusterLevels(swingLows, currentPrice, 0.0015m);

        // Find nearest resistance above current price
        var resistancesAbove = resistanceZones.Where(z => z > currentPrice).OrderBy(z => z).ToList();
        var supportsBelow = supportZones.Where(z => z < currentPrice).OrderByDescending(z => z).ToList();

        return new StructureLevels
        {
            SwingHighs = swingHighs,
            SwingLows = swingLows,
            ResistanceZones = resistancesAbove,
            SupportZones = supportsBelow,
            NearestResistance = resistancesAbove.Count > 0 ? resistancesAbove[0] : 0,
            SecondResistance = resistancesAbove.Count > 1 ? resistancesAbove[1] : 0,
            NearestSupport = supportsBelow.Count > 0 ? supportsBelow[0] : 0,
            SecondSupport = supportsBelow.Count > 1 ? supportsBelow[1] : 0,
        };
    }

    /// <summary>
    /// Find the best structure-based invalidation level for SL placement.
    /// For BUY: nearest swing low below entry (the level where the long thesis breaks).
    /// For SELL: nearest swing high above entry (the level where the short thesis breaks).
    /// </summary>
    public static decimal FindInvalidationLevel(
        StructureLevels levels,
        SignalDirection direction,
        decimal entryPrice)
    {
        if (direction == SignalDirection.BUY)
        {
            // Find the nearest swing low below entry
            var below = levels.SwingLows
                .Where(l => l < entryPrice)
                .OrderByDescending(l => l)
                .FirstOrDefault();
            return below > 0 ? below : 0;
        }
        else
        {
            // Find the nearest swing high above entry
            var above = levels.SwingHighs
                .Where(h => h > entryPrice)
                .OrderBy(h => h)
                .FirstOrDefault();
            return above > 0 ? above : 0;
        }
    }

    /// <summary>
    /// Find structure-based TP target.
    /// For BUY: nearest resistance above entry.
    /// For SELL: nearest support below entry.
    /// </summary>
    public static decimal FindStructureTarget(
        StructureLevels levels,
        SignalDirection direction,
        decimal entryPrice,
        decimal minimumDistance = 0m)
    {
        if (direction == SignalDirection.BUY)
        {
            foreach (var resistance in EnumerateResistanceTargets(levels, entryPrice))
            {
                if (resistance - entryPrice >= minimumDistance)
                    return resistance;
            }

            return 0;
        }
        else
        {
            foreach (var support in EnumerateSupportTargets(levels, entryPrice))
            {
                if (entryPrice - support >= minimumDistance)
                    return support;
            }

            return 0;
        }
    }

    private static IEnumerable<decimal> EnumerateResistanceTargets(StructureLevels levels, decimal entryPrice)
    {
        if (levels.ResistanceZones.Count > 0)
            return levels.ResistanceZones.Where(z => z > entryPrice).OrderBy(z => z);

        return new[] { levels.NearestResistance, levels.SecondResistance }
            .Where(z => z > entryPrice)
            .Distinct()
            .OrderBy(z => z);
    }

    private static IEnumerable<decimal> EnumerateSupportTargets(StructureLevels levels, decimal entryPrice)
    {
        if (levels.SupportZones.Count > 0)
            return levels.SupportZones.Where(z => z > 0 && z < entryPrice).OrderByDescending(z => z);

        return new[] { levels.NearestSupport, levels.SecondSupport }
            .Where(z => z > 0 && z < entryPrice)
            .Distinct()
            .OrderByDescending(z => z);
    }

    /// <summary>Cluster nearby price levels and return zone midpoints.</summary>
    private static List<decimal> ClusterLevels(List<decimal> sorted, decimal refPrice, decimal tolerancePct)
    {
        if (sorted.Count == 0) return [];

        var zones = new List<decimal>();
        var cluster = new List<decimal> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            decimal threshold = refPrice > 0 ? refPrice * tolerancePct : sorted[i] * tolerancePct;
            if (sorted[i] - cluster[^1] <= threshold)
            {
                cluster.Add(sorted[i]);
            }
            else
            {
                zones.Add(cluster.Average());
                cluster = [sorted[i]];
            }
        }
        zones.Add(cluster.Average());
        return zones;
    }

    /// <summary>Average extension for decimal lists.</summary>
    private static decimal Average(this List<decimal> list)
        => list.Count == 0 ? 0 : list.Sum() / list.Count;
}
