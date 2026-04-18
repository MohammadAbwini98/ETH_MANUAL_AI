namespace EthSignal.Domain.Models;

public sealed record SpotPrice(
    decimal Bid,
    decimal Ask,
    decimal Mid,
    DateTimeOffset Timestamp);
