using System.Text.Json;
using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class ReplayRepository : IReplayRepository
{
    private readonly string _connectionString;

    public ReplayRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<long> InsertRunAsync(ReplayRun run, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".replay_runs
                (symbol, timeframe_base, timeframe_primary, timeframe_bias,
                 start_utc, end_utc, parameter_set_id, strategy_version,
                 mode, status, trigger_source, code_version)
            VALUES (@sym, @tfb, @tfp, @tfbi, @start, @end, @psid, @sv,
                    @mode, @status, @trigger, @cv)
            RETURNING id;", conn);

        cmd.Parameters.AddWithValue("sym", run.Symbol);
        cmd.Parameters.AddWithValue("tfb", run.TimeframeBase);
        cmd.Parameters.AddWithValue("tfp", run.TimeframePrimary);
        cmd.Parameters.AddWithValue("tfbi", run.TimeframeBias);
        cmd.Parameters.AddWithValue("start", run.StartUtc);
        cmd.Parameters.AddWithValue("end", run.EndUtc);
        cmd.Parameters.AddWithValue("psid", (object?)run.ParameterSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sv", run.StrategyVersion);
        cmd.Parameters.AddWithValue("mode", run.Mode.ToString());
        cmd.Parameters.AddWithValue("status", run.Status.ToString());
        cmd.Parameters.AddWithValue("trigger", run.TriggerSource);
        cmd.Parameters.AddWithValue("cv", (object?)run.CodeVersion ?? DBNull.Value);

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ReplayRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, timeframe_base, timeframe_primary, timeframe_bias,
                   start_utc, end_utc, parameter_set_id, strategy_version,
                   mode, status, started_utc, finished_utc,
                   candles_read_count, signals_generated_count, outcomes_finalized_count,
                   gap_event_count, error_text, code_version, trigger_source, checkpoint_time
            FROM ""ETH"".replay_runs WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new ReplayRun
        {
            Id = r.GetInt64(0),
            Symbol = r.GetString(1),
            TimeframeBase = r.GetString(2),
            TimeframePrimary = r.GetString(3),
            TimeframeBias = r.GetString(4),
            StartUtc = r.GetFieldValue<DateTimeOffset>(5),
            EndUtc = r.GetFieldValue<DateTimeOffset>(6),
            ParameterSetId = r.IsDBNull(7) ? null : r.GetInt64(7),
            StrategyVersion = r.GetString(8),
            Mode = Enum.Parse<ReplayMode>(r.GetString(9)),
            Status = Enum.Parse<RunStatus>(r.GetString(10)),
            StartedUtc = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
            FinishedUtc = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
            CandlesReadCount = r.GetInt32(13),
            SignalsGeneratedCount = r.GetInt32(14),
            OutcomesFinalizedCount = r.GetInt32(15),
            GapEventCount = r.GetInt32(16),
            ErrorText = r.IsDBNull(17) ? null : r.GetString(17),
            CodeVersion = r.IsDBNull(18) ? null : r.GetString(18),
            TriggerSource = r.GetString(19),
            CheckpointTime = r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20)
        };
    }

    public async Task UpdateRunStatusAsync(long id, RunStatus status, string? error, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = status == RunStatus.Running
            ? @"UPDATE ""ETH"".replay_runs SET status = @s, started_utc = NOW() WHERE id = @id;"
            : @"UPDATE ""ETH"".replay_runs SET status = @s, error_text = @err WHERE id = @id;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status.ToString());
        if (status != RunStatus.Running)
            cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRunProgressAsync(long id, int candlesRead, int signalsGenerated,
        int outcomesFinalized, DateTimeOffset? checkpoint, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".replay_runs
            SET candles_read_count = @cr, signals_generated_count = @sg,
                outcomes_finalized_count = @of, checkpoint_time = @cp
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cr", candlesRead);
        cmd.Parameters.AddWithValue("sg", signalsGenerated);
        cmd.Parameters.AddWithValue("of", outcomesFinalized);
        cmd.Parameters.AddWithValue("cp", (object?)checkpoint ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRunFinishedAsync(long id, RunStatus status, int candlesRead,
        int signalsGenerated, int outcomesFinalized, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".replay_runs
            SET status = @s, finished_utc = NOW(),
                candles_read_count = @cr, signals_generated_count = @sg,
                outcomes_finalized_count = @of
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status.ToString());
        cmd.Parameters.AddWithValue("cr", candlesRead);
        cmd.Parameters.AddWithValue("sg", signalsGenerated);
        cmd.Parameters.AddWithValue("of", outcomesFinalized);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
