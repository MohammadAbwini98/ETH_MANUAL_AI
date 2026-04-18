namespace EthSignal.Domain.Models;

public sealed record Timeframe(string Name, string Table, string ApiResolution, int Minutes)
{
    public static readonly Timeframe M1 = new("1m", "candles_1m", "MINUTE", 1);
    public static readonly Timeframe M5 = new("5m", "candles_5m", "MINUTE_5", 5);
    public static readonly Timeframe M15 = new("15m", "candles_15m", "MINUTE_15", 15);
    public static readonly Timeframe M30 = new("30m", "candles_30m", "MINUTE_30", 30);
    public static readonly Timeframe H1 = new("1h", "candles_1h", "HOUR", 60);
    public static readonly Timeframe H4 = new("4h", "candles_4h", "HOUR_4", 240);

    public static readonly Timeframe[] All = [M1, M5, M15, M30, H1, H4];

    /// <summary>Timeframes used for signal evaluation (includes 1m for scalping).</summary>
    public static readonly Timeframe[] Signal = [M1, M5, M15, M30, H1, H4];

    public TimeSpan Duration => TimeSpan.FromMinutes(Minutes);

    /// <summary>
    /// Find a Timeframe by name (e.g. "5m", "1h").
    /// Throws <see cref="ArgumentException"/> if the name is not recognized.
    /// Use <see cref="TryByName"/> when a null result is acceptable.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is not a known timeframe.</exception>
    public static Timeframe ByName(string name)
    {
        var tf = All.FirstOrDefault(t => t.Name == name);
        if (tf == null)
            throw new ArgumentException(
                $"Unknown timeframe name '{name}'. Valid names: {string.Join(", ", All.Select(t => t.Name))}",
                nameof(name));
        return tf;
    }

    /// <summary>
    /// Find a Timeframe by name (e.g. "5m", "1h").
    /// Returns <paramref name="fallback"/> (default <see cref="M5"/>) if the name is not recognized.
    /// Prefer <see cref="ByName"/> for strict validation; use this only when a silent fallback is intentional.
    /// </summary>
    public static Timeframe ByNameOrDefault(string name, Timeframe? fallback = null)
        => All.FirstOrDefault(t => t.Name == name) ?? fallback ?? M5;

    private static readonly long EpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

    /// <summary>
    /// Floor a timestamp to the timeframe boundary.
    /// E.g. 10:03:12 on M5 → 10:00:00, 10:14:59 on M15 → 10:00:00.
    /// </summary>
    public DateTimeOffset Floor(DateTimeOffset timestamp)
    {
        var sinceTicks = timestamp.UtcTicks - EpochTicks;
        var durTicks = Duration.Ticks;
        var floored = sinceTicks - (sinceTicks % durTicks);
        return new DateTimeOffset(EpochTicks + floored, TimeSpan.Zero);
    }
}
