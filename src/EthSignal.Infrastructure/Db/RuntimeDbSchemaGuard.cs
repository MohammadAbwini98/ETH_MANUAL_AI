using System.Collections.Concurrent;

namespace EthSignal.Infrastructure.Db;

internal static class RuntimeDbSchemaGuard
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Migrated = new(StringComparer.Ordinal);

    public static async Task EnsureMigratedAsync(string connectionString, CancellationToken ct = default)
    {
        if (Migrated.ContainsKey(connectionString))
            return;

        var gate = Gates.GetOrAdd(connectionString, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Migrated.ContainsKey(connectionString))
                return;

            var migrator = new DbMigrator(connectionString);
            await migrator.MigrateAsync(ct);
            Migrated[connectionString] = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
