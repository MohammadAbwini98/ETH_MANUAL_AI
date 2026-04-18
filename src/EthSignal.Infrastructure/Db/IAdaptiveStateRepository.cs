using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

/// <summary>
/// Issue #3: Persistence for the adaptive parameter system's per-condition outcome
/// windows and retrospective overlays so they survive process restarts.
/// </summary>
public interface IAdaptiveStateRepository
{
    Task<IReadOnlyList<OutcomeWindowSnapshot>> LoadOutcomeWindowsAsync(CancellationToken ct = default);
    Task UpsertOutcomeWindowAsync(string conditionKey, IReadOnlyList<SignalOutcome> outcomes, CancellationToken ct = default);

    Task<IReadOnlyList<RetrospectiveOverlayRecord>> LoadRetrospectiveOverlaysAsync(CancellationToken ct = default);
    Task UpsertRetrospectiveOverlayAsync(string conditionKey, ParameterOverlay overlay, CancellationToken ct = default);
    Task DeleteRetrospectiveOverlayAsync(string conditionKey, CancellationToken ct = default);
}

public sealed record OutcomeWindowSnapshot
{
    public required string ConditionKey { get; init; }
    public required IReadOnlyList<SignalOutcome> Outcomes { get; init; }
}

public sealed record RetrospectiveOverlayRecord
{
    public required string ConditionKey { get; init; }
    public required ParameterOverlay Overlay { get; init; }
}
