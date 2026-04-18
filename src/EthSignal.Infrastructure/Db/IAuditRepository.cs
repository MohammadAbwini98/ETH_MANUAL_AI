using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IAuditRepository
{
    Task InsertAuditAsync(IngestionAuditEntry entry, CancellationToken ct = default);
    Task InsertGapsAsync(IReadOnlyList<GapEvent> gaps, CancellationToken ct = default);
    /// <summary>B-05: Check for unresolved gaps in the last N minutes.</summary>
    Task<bool> HasRecentUnresolvedGapsAsync(string symbol, int lookbackMinutes = 60, CancellationToken ct = default);

    /// <summary>U-04: Resolve old historical gaps that no longer block live processing.</summary>
    Task<int> ResolveOldGapsAsync(string symbol, int maxAgeMinutes = 120, CancellationToken ct = default);

    /// <summary>U-04: Get gap diagnostics for admin/health visibility.</summary>
    Task<GapDiagnostics> GetGapDiagnosticsAsync(string symbol, CancellationToken ct = default);
}

public sealed record GapDiagnostics(
    int UnresolvedRecentCount,
    int UnresolvedTotalCount,
    DateTimeOffset? OldestUnresolvedExpectedTime,
    DateTimeOffset? NewestDetectedAtUtc);
