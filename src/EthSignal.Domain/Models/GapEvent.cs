namespace EthSignal.Domain.Models;

public sealed record GapEvent(
    string Symbol,
    string TimeframeName,
    DateTimeOffset ExpectedTime,
    DateTimeOffset? ActualNextTime,
    TimeSpan GapDuration,
    string GapType,
    DateTimeOffset DetectedAtUtc,
    string GapSource = "LIVE");
