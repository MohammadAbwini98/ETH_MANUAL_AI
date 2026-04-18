namespace EthSignal.Infrastructure.Db;

public interface IDbMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
}
