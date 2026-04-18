using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface ISignalRepository
{
    Task InsertSignalAsync(SignalRecommendation signal, CancellationToken ct = default);
    Task InsertOutcomeAsync(SignalOutcome outcome, CancellationToken ct = default);
    Task<SignalRecommendation?> GetLatestSignalAsync(string symbol, CancellationToken ct = default);
    Task<SignalRecommendation?> GetLatestSignalBeforeAsync(string symbol, DateTimeOffset before, CancellationToken ct = default);
    Task<SignalRecommendation?> GetLatestPrimaryTimeframeSignalAsync(string symbol, string primaryTimeframe, CancellationToken ct = default);
    Task<IReadOnlyList<SignalRecommendation>> GetSignalHistoryAsync(string symbol, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<SignalRecommendation>> GetRecentSignalsAsync(
        string symbol,
        string timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default);
    Task<IReadOnlyList<SignalWithOutcome>> GetSignalHistoryWithOutcomesAsync(string symbol, int limit, int offset, CancellationToken ct = default);
    Task<int> GetSignalCountAsync(string symbol, CancellationToken ct = default);
    Task<IReadOnlyList<SignalOutcome>> GetOutcomesAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<SignalOutcome>> GetOutcomesAsync(string symbol, DateTimeOffset from, DateTimeOffset to, string strategyVersion, CancellationToken ct = default);
    Task<IReadOnlyList<SignalRecommendation>> GetOpenSignalsAsync(string symbol, CancellationToken ct = default);
    Task UpdateSignalStatusAsync(Guid signalId, SignalStatus status, CancellationToken ct = default);
    Task InsertSignalFeaturesAsync(Guid signalId, Dictionary<string, decimal> features, CancellationToken ct = default);
}

public sealed record SignalWithOutcome
{
    public required SignalRecommendation Signal { get; init; }
    public SignalOutcome? Outcome { get; init; }
}
