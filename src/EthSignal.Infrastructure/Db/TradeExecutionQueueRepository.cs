using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class TradeExecutionQueueRepository : ITradeExecutionQueueRepository
{
    private readonly string _connectionString;

    public TradeExecutionQueueRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<long> InsertAsync(QueuedTradeExecution entry, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".trade_execution_queue
                (signal_id, evaluation_id, source_type, requested_by, requested_size, force_market_execution,
                 candidate_json, status, executed_trade_id, failure_reason, error_details,
                 created_at_utc, updated_at_utc, processed_at_utc)
            VALUES
                (@signal_id, @evaluation_id, @source_type, @requested_by, @requested_size, @force_market_execution,
                 @candidate_json::jsonb, @status, @executed_trade_id, @failure_reason, @error_details,
                 @created_at_utc, @updated_at_utc, @processed_at_utc)
            RETURNING queue_entry_id;", conn);
        Bind(cmd, entry);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(QueuedTradeExecution entry, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".trade_execution_queue
            SET signal_id = @signal_id,
                evaluation_id = @evaluation_id,
                source_type = @source_type,
                requested_by = @requested_by,
                requested_size = @requested_size,
                force_market_execution = @force_market_execution,
                candidate_json = @candidate_json::jsonb,
                status = @status,
                executed_trade_id = @executed_trade_id,
                failure_reason = @failure_reason,
                error_details = @error_details,
                updated_at_utc = @updated_at_utc,
                processed_at_utc = @processed_at_utc
            WHERE queue_entry_id = @queue_entry_id;", conn);
        Bind(cmd, entry);
        cmd.Parameters.AddWithValue("queue_entry_id", entry.QueueEntryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<QueuedTradeExecution?> GetActiveBySourceSignalAsync(Guid signalId, SignalExecutionSourceType sourceType, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT queue_entry_id, signal_id, evaluation_id, source_type, requested_by, requested_size, force_market_execution,
                   candidate_json::text, status, executed_trade_id, failure_reason, error_details,
                   created_at_utc, updated_at_utc, processed_at_utc
            FROM ""ETH"".trade_execution_queue
            WHERE signal_id = @signal_id
              AND source_type = @source_type
              AND status IN ('Queued', 'Processing')
            ORDER BY created_at_utc DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("signal_id", signalId);
        cmd.Parameters.AddWithValue("source_type", sourceType.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Read(reader);
    }

    public async Task<QueuedTradeExecution?> GetNextQueuedAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT queue_entry_id, signal_id, evaluation_id, source_type, requested_by, requested_size, force_market_execution,
                   candidate_json::text, status, executed_trade_id, failure_reason, error_details,
                   created_at_utc, updated_at_utc, processed_at_utc
            FROM ""ETH"".trade_execution_queue
            WHERE status = 'Queued'
            ORDER BY created_at_utc ASC, queue_entry_id ASC
            LIMIT 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return Read(reader);
    }

    public async Task<bool> HasQueuedAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS(
                SELECT 1
                FROM ""ETH"".trade_execution_queue
                WHERE status = 'Queued'
            );", conn);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<QueuedTradeExecution>> GetActiveEntriesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT queue_entry_id, signal_id, evaluation_id, source_type, requested_by, requested_size, force_market_execution,
                   candidate_json::text, status, executed_trade_id, failure_reason, error_details,
                   created_at_utc, updated_at_utc, processed_at_utc
            FROM ""ETH"".trade_execution_queue
            WHERE status IN ('Queued', 'Processing')
            ORDER BY created_at_utc ASC, queue_entry_id ASC
            LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));

        var entries = new List<QueuedTradeExecution>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(Read(reader));
        return entries;
    }

    public async Task<(int QueuedCount, int ProcessingCount, int CompletedCount, int FailedCount)> GetStatusCountsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                COUNT(*) FILTER (WHERE status = 'Queued')::INT AS queued_count,
                COUNT(*) FILTER (WHERE status = 'Processing')::INT AS processing_count,
                COUNT(*) FILTER (WHERE status = 'Completed')::INT AS completed_count,
                COUNT(*) FILTER (WHERE status = 'Failed')::INT AS failed_count
            FROM ""ETH"".trade_execution_queue;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static void Bind(NpgsqlCommand cmd, QueuedTradeExecution entry)
    {
        cmd.Parameters.AddWithValue("signal_id", entry.SignalId);
        cmd.Parameters.AddWithValue("evaluation_id", (object?)entry.EvaluationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("source_type", entry.SourceType.ToString());
        cmd.Parameters.AddWithValue("requested_by", entry.RequestedBy);
        cmd.Parameters.AddWithValue("requested_size", (object?)entry.RequestedSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("force_market_execution", entry.ForceMarketExecution);
        cmd.Parameters.AddWithValue("candidate_json", entry.CandidateJson);
        cmd.Parameters.AddWithValue("status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("executed_trade_id", (object?)entry.ExecutedTradeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)entry.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error_details", (object?)entry.ErrorDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created_at_utc", entry.CreatedAtUtc);
        cmd.Parameters.AddWithValue("updated_at_utc", entry.UpdatedAtUtc);
        cmd.Parameters.AddWithValue("processed_at_utc", (object?)entry.ProcessedAtUtc ?? DBNull.Value);
    }

    private static QueuedTradeExecution Read(NpgsqlDataReader reader) => new()
    {
        QueueEntryId = reader.GetInt64(0),
        SignalId = reader.GetGuid(1),
        EvaluationId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        SourceType = Enum.Parse<SignalExecutionSourceType>(reader.GetString(3)),
        RequestedBy = reader.GetString(4),
        RequestedSize = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
        ForceMarketExecution = reader.GetBoolean(6),
        CandidateJson = reader.GetString(7),
        Status = Enum.Parse<TradeExecutionQueueStatus>(reader.GetString(8)),
        ExecutedTradeId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        FailureReason = reader.IsDBNull(10) ? null : reader.GetString(10),
        ErrorDetails = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(12),
        UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(13),
        ProcessedAtUtc = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14)
    };
}
