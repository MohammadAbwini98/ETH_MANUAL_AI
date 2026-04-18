namespace EthSignal.Domain.Models;

public sealed record IngestionAuditEntry(
    string Operation,
    string Symbol,
    string TimeframeName,
    DateTimeOffset? PeriodFrom,
    DateTimeOffset? PeriodTo,
    int CandlesFetched,
    int CandlesInserted,
    int CandlesUpdated,
    int DuplicatesSkipped,
    int ValidationErrors,
    TimeSpan Duration,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc);
