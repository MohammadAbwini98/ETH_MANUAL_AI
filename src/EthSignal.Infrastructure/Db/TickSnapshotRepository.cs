using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class TickSnapshotRepository : ITickSnapshotRepository
{
    private readonly string _connectionString;

    public TickSnapshotRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InsertAsync(
        string symbol,
        string epic,
        SpotPrice spot,
        string providerKind,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ui_tick_samples
                (symbol, epic, tick_time_utc, bid, ask, mid, provider_kind, received_at_utc)
            VALUES (@symbol, @epic, @tickTime, @bid, @ask, @mid, @providerKind, NOW());", conn);

        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("epic", epic);
        cmd.Parameters.AddWithValue("tickTime", spot.Timestamp);
        cmd.Parameters.AddWithValue("bid", spot.Bid);
        cmd.Parameters.AddWithValue("ask", spot.Ask);
        cmd.Parameters.AddWithValue("mid", spot.Mid);
        cmd.Parameters.AddWithValue("providerKind", providerKind);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
