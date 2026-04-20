using System.Text;
using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class ExecutedTradeRepository : IExecutedTradeRepository
{
    private readonly string _connectionString;

    public ExecutedTradeRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<long> InsertExecutedTradeAsync(ExecutedTrade trade, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".executed_trades
                (signal_id, evaluation_id, source_type, symbol, instrument, timeframe, direction,
                 recommended_entry_price, actual_entry_price, tp_price, sl_price,
                 requested_size, executed_size, deal_reference, deal_id, status,
                 account_id, account_name, is_demo, account_currency, opened_at_utc, closed_at_utc, pnl,
                 failure_reason, error_details, force_closed, close_source,
                 created_at_utc, updated_at_utc)
            VALUES
                (@signal_id, @evaluation_id, @source_type, @symbol, @instrument, @timeframe, @direction,
                 @recommended_entry_price, @actual_entry_price, @tp_price, @sl_price,
                 @requested_size, @executed_size, @deal_reference, @deal_id, @status,
                 @account_id, @account_name, @is_demo, @account_currency, @opened_at_utc, @closed_at_utc, @pnl,
                 @failure_reason, @error_details, @force_closed, @close_source,
                 @created_at_utc, @updated_at_utc)
            RETURNING executed_trade_id;", conn);
        BindTrade(cmd, trade);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateExecutedTradeAsync(ExecutedTrade trade, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".executed_trades
            SET evaluation_id = @evaluation_id,
                source_type = @source_type,
                symbol = @symbol,
                instrument = @instrument,
                timeframe = @timeframe,
                direction = @direction,
                recommended_entry_price = @recommended_entry_price,
                actual_entry_price = @actual_entry_price,
                tp_price = @tp_price,
                sl_price = @sl_price,
                requested_size = @requested_size,
                executed_size = @executed_size,
                deal_reference = @deal_reference,
                deal_id = @deal_id,
                status = @status,
                account_id = @account_id,
                account_name = @account_name,
                is_demo = @is_demo,
                account_currency = @account_currency,
                opened_at_utc = @opened_at_utc,
                closed_at_utc = @closed_at_utc,
                pnl = @pnl,
                failure_reason = @failure_reason,
                error_details = @error_details,
                force_closed = @force_closed,
                close_source = @close_source,
                updated_at_utc = @updated_at_utc
            WHERE executed_trade_id = @executed_trade_id;", conn);
        BindTrade(cmd, trade);
        cmd.Parameters.AddWithValue("executed_trade_id", trade.ExecutedTradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExecutedTrade?> GetExecutedTradeAsync(long executedTradeId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
             SELECT executed_trade_id, signal_id, evaluation_id, source_type, symbol, instrument, timeframe, direction,
                 recommended_entry_price, actual_entry_price, tp_price, sl_price,
                 requested_size, executed_size, deal_reference, deal_id, status,
                 account_id, account_name, is_demo, account_currency, opened_at_utc, closed_at_utc, pnl,
                   failure_reason, error_details, force_closed, close_source,
                   created_at_utc, updated_at_utc
            FROM ""ETH"".executed_trades
            WHERE executed_trade_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", executedTradeId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadTrade(reader);
    }

    public async Task<ExecutedTrade?> GetBySourceSignalAsync(Guid signalId, SignalExecutionSourceType sourceType, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
             SELECT executed_trade_id, signal_id, evaluation_id, source_type, symbol, instrument, timeframe, direction,
                 recommended_entry_price, actual_entry_price, tp_price, sl_price,
                 requested_size, executed_size, deal_reference, deal_id, status,
                 account_id, account_name, is_demo, account_currency, opened_at_utc, closed_at_utc, pnl,
                   failure_reason, error_details, force_closed, close_source,
                   created_at_utc, updated_at_utc
            FROM ""ETH"".executed_trades
            WHERE signal_id = @signal_id
              AND source_type = @source_type
            ORDER BY created_at_utc DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("signal_id", signalId);
        cmd.Parameters.AddWithValue("source_type", sourceType.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadTrade(reader);
    }

    public async Task<IReadOnlyList<ExecutedTrade>> GetExecutedTradesAsync(ExecutedTradeQuery query, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"
             SELECT executed_trade_id, signal_id, evaluation_id, source_type, symbol, instrument, timeframe, direction,
                 recommended_entry_price, actual_entry_price, tp_price, sl_price,
                 requested_size, executed_size, deal_reference, deal_id, status,
                 account_id, account_name, is_demo, account_currency, opened_at_utc, closed_at_utc, pnl,
                   failure_reason, error_details, force_closed, close_source,
                   created_at_utc, updated_at_utc
            FROM ""ETH"".executed_trades
            WHERE 1 = 1");

        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        AppendFilters(sql, cmd, query);
        sql.Append(" ORDER BY created_at_utc DESC LIMIT @limit OFFSET @offset;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("limit", Math.Clamp(query.Limit, 1, 500));
        cmd.Parameters.AddWithValue("offset", Math.Max(0, query.Offset));

        var results = new List<ExecutedTrade>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadTrade(reader));
        return results;
    }

    public async Task<int> GetExecutedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"SELECT COUNT(*)::INT FROM ""ETH"".executed_trades WHERE 1 = 1");
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        AppendFilters(sql, cmd, query);
        cmd.CommandText = sql.ToString();
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<ExecutedTradeStats> GetExecutionStatsAsync(CancellationToken ct = default)
        => GetExecutionStatsAsync(new ExecutedTradeQuery(), ct);

    public async Task<ExecutedTradeStats> GetExecutionStatsAsync(ExecutedTradeQuery query, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"
            SELECT
                COUNT(*)::INT AS total_executed,
                COUNT(*) FILTER (WHERE status = 'Open')::INT AS open_trades,
                COUNT(*) FILTER (WHERE status = 'Closed' AND pnl > 0)::INT AS wins,
                COUNT(*) FILTER (WHERE status = 'Closed' AND pnl <= 0)::INT AS losses,
                COUNT(*) FILTER (WHERE status IN ('Failed', 'Rejected', 'ValidationFailed', 'CloseFailed'))::INT AS failed_executions,
                COALESCE(SUM(pnl), 0)::NUMERIC AS total_pnl,
                COALESCE(MAX(account_currency) FILTER (WHERE account_currency <> ''), 'USD') AS currency
            FROM ""ETH"".executed_trades
            WHERE 1 = 1");
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        AppendFilters(sql, cmd, query);
        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var wins = reader.GetInt32(2);
        var losses = reader.GetInt32(3);
        var decided = wins + losses;
        return new ExecutedTradeStats
        {
            TotalExecuted = reader.GetInt32(0),
            OpenTrades = reader.GetInt32(1),
            Wins = wins,
            Losses = losses,
            FailedExecutions = reader.GetInt32(4),
            TotalPnl = reader.GetDecimal(5),
            WinRate = decided > 0 ? wins * 100m / decided : 0m,
            Currency = reader.GetString(6)
        };
    }

    public Task<int> GetOpenExecutedTradeCountAsync(CancellationToken ct = default)
        => GetOpenExecutedTradeCountAsync(new ExecutedTradeQuery(), ct);

    public async Task<int> GetOpenExecutedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"SELECT COUNT(*)::INT FROM ""ETH"".executed_trades WHERE status = 'Open'");
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        AppendFilters(sql, cmd, query);
        cmd.CommandText = sql.ToString();
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertExecutionAttemptAsync(long? executedTradeId, Guid signalId, SignalExecutionSourceType sourceType, string attemptType, bool success, string? summary, string? errorDetails, string? brokerPayload, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".execution_attempts
                (executed_trade_id, signal_id, source_type, attempt_type, success, summary, error_details, broker_payload, created_at_utc)
            VALUES
                (@executed_trade_id, @signal_id, @source_type, @attempt_type, @success, @summary, @error_details, @broker_payload, NOW());", conn);
        cmd.Parameters.AddWithValue("executed_trade_id", (object?)executedTradeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("signal_id", signalId);
        cmd.Parameters.AddWithValue("source_type", sourceType.ToString());
        cmd.Parameters.AddWithValue("attempt_type", attemptType);
        cmd.Parameters.AddWithValue("success", success);
        cmd.Parameters.AddWithValue("summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error_details", (object?)errorDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("broker_payload", (object?)brokerPayload ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertExecutionEventAsync(long? executedTradeId, Guid signalId, SignalExecutionSourceType sourceType, string eventType, string message, string? detailsJson, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".execution_events
                (executed_trade_id, signal_id, source_type, event_type, message, details_json, created_at_utc)
            VALUES
                (@executed_trade_id, @signal_id, @source_type, @event_type, @message, @details_json::jsonb, NOW());", conn);
        cmd.Parameters.AddWithValue("executed_trade_id", (object?)executedTradeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("signal_id", signalId);
        cmd.Parameters.AddWithValue("source_type", sourceType.ToString());
        cmd.Parameters.AddWithValue("event_type", eventType);
        cmd.Parameters.AddWithValue("message", message);
        cmd.Parameters.AddWithValue("details_json", (object?)detailsJson ?? "null");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> InsertAccountSnapshotAsync(AccountSnapshot snapshot, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".account_snapshots
                (account_id, account_name, currency, balance, equity, available, margin, funds, open_positions, is_demo, hedging_mode, captured_at_utc)
            VALUES
                (@account_id, @account_name, @currency, @balance, @equity, @available, @margin, @funds, @open_positions, @is_demo, @hedging_mode, @captured_at_utc)
            RETURNING snapshot_id;", conn);
        cmd.Parameters.AddWithValue("account_id", snapshot.AccountId);
        cmd.Parameters.AddWithValue("account_name", snapshot.AccountName);
        cmd.Parameters.AddWithValue("currency", snapshot.Currency);
        cmd.Parameters.AddWithValue("balance", snapshot.Balance);
        cmd.Parameters.AddWithValue("equity", snapshot.Equity);
        cmd.Parameters.AddWithValue("available", snapshot.Available);
        cmd.Parameters.AddWithValue("margin", snapshot.Margin);
        cmd.Parameters.AddWithValue("funds", snapshot.Funds);
        cmd.Parameters.AddWithValue("open_positions", snapshot.OpenPositions);
        cmd.Parameters.AddWithValue("is_demo", snapshot.IsDemo);
        cmd.Parameters.AddWithValue("hedging_mode", snapshot.HedgingMode);
        cmd.Parameters.AddWithValue("captured_at_utc", snapshot.CapturedAtUtc);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<AccountSnapshot?> GetLatestAccountSnapshotAsync(CancellationToken ct = default)
        => GetLatestAccountSnapshotAsync(accountName: null, isDemo: null, ct);

    public async Task<AccountSnapshot?> GetLatestAccountSnapshotAsync(string? accountName, bool? isDemo, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var sql = new StringBuilder(@"
            SELECT snapshot_id, account_id, account_name, currency, balance, equity, available, margin, funds, open_positions, is_demo, hedging_mode, captured_at_utc
            FROM ""ETH"".account_snapshots
            WHERE 1 = 1");
        await using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            sql.Append(" AND account_name = @snapshot_account_name");
            cmd.Parameters.AddWithValue("snapshot_account_name", accountName);
        }
        if (isDemo.HasValue)
        {
            sql.Append(" AND is_demo = @snapshot_is_demo");
            cmd.Parameters.AddWithValue("snapshot_is_demo", isDemo.Value);
        }
        sql.Append(" ORDER BY captured_at_utc DESC LIMIT 1;");
        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new AccountSnapshot
        {
            SnapshotId = reader.GetInt64(0),
            AccountId = reader.GetString(1),
            AccountName = reader.GetString(2),
            Currency = reader.GetString(3),
            Balance = reader.GetDecimal(4),
            Equity = reader.GetDecimal(5),
            Available = reader.GetDecimal(6),
            Margin = reader.GetDecimal(7),
            Funds = reader.GetDecimal(8),
            OpenPositions = reader.GetInt32(9),
            IsDemo = reader.GetBoolean(10),
            HedgingMode = reader.GetBoolean(11),
            CapturedAtUtc = reader.GetFieldValue<DateTimeOffset>(12)
        };
    }

    public async Task InsertCloseTradeActionAsync(long executedTradeId, ForceCloseRequest request, ForceCloseResult result, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".close_trade_actions
                (executed_trade_id, requested_by, reason, success, message, deal_reference, deal_id, close_level, pnl, created_at_utc)
            VALUES
                (@executed_trade_id, @requested_by, @reason, @success, @message, @deal_reference, @deal_id, @close_level, @pnl, NOW());", conn);
        cmd.Parameters.AddWithValue("executed_trade_id", executedTradeId);
        cmd.Parameters.AddWithValue("requested_by", request.RequestedBy);
        cmd.Parameters.AddWithValue("reason", (object?)request.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("success", result.Success);
        cmd.Parameters.AddWithValue("message", result.Message);
        cmd.Parameters.AddWithValue("deal_reference", (object?)result.DealReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deal_id", (object?)result.DealId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("close_level", (object?)result.CloseLevel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pnl", (object?)result.Pnl ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AppendFilters(StringBuilder sql, NpgsqlCommand cmd, ExecutedTradeQuery query)
    {
        if (query.FromUtc.HasValue)
        {
            sql.Append(" AND created_at_utc >= @from_utc");
            cmd.Parameters.AddWithValue("from_utc", query.FromUtc.Value);
        }
        if (query.ToUtc.HasValue)
        {
            sql.Append(" AND created_at_utc <= @to_utc");
            cmd.Parameters.AddWithValue("to_utc", query.ToUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Instrument))
        {
            sql.Append(" AND instrument = @instrument");
            cmd.Parameters.AddWithValue("instrument", query.Instrument);
        }
        if (!string.IsNullOrWhiteSpace(query.AccountId))
        {
            sql.Append(" AND account_id = @account_id");
            cmd.Parameters.AddWithValue("account_id", query.AccountId);
        }
        if (!string.IsNullOrWhiteSpace(query.AccountName))
        {
            sql.Append(" AND account_name = @account_name");
            cmd.Parameters.AddWithValue("account_name", query.AccountName);
        }
        if (query.IsDemo.HasValue)
        {
            sql.Append(" AND is_demo = @is_demo");
            cmd.Parameters.AddWithValue("is_demo", query.IsDemo.Value);
        }
        if (query.Direction.HasValue)
        {
            sql.Append(" AND direction = @direction");
            cmd.Parameters.AddWithValue("direction", query.Direction.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(query.Timeframe))
        {
            sql.Append(" AND timeframe = @timeframe");
            cmd.Parameters.AddWithValue("timeframe", query.Timeframe);
        }
        if (query.SourceType.HasValue)
        {
            sql.Append(" AND source_type = @source_type");
            cmd.Parameters.AddWithValue("source_type", query.SourceType.Value.ToString());
        }
        if (query.Status.HasValue)
        {
            sql.Append(" AND status = @status");
            cmd.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }
    }

    private static void BindTrade(NpgsqlCommand cmd, ExecutedTrade trade)
    {
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("signal_id", trade.SignalId);
        cmd.Parameters.AddWithValue("evaluation_id", (object?)trade.EvaluationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("source_type", trade.SourceType.ToString());
        cmd.Parameters.AddWithValue("symbol", trade.Symbol);
        cmd.Parameters.AddWithValue("instrument", trade.Instrument);
        cmd.Parameters.AddWithValue("timeframe", trade.Timeframe);
        cmd.Parameters.AddWithValue("direction", trade.Direction.ToString());
        cmd.Parameters.AddWithValue("recommended_entry_price", trade.RecommendedEntryPrice);
        cmd.Parameters.AddWithValue("actual_entry_price", trade.ActualEntryPrice);
        cmd.Parameters.AddWithValue("tp_price", trade.TpPrice);
        cmd.Parameters.AddWithValue("sl_price", trade.SlPrice);
        cmd.Parameters.AddWithValue("requested_size", trade.RequestedSize);
        cmd.Parameters.AddWithValue("executed_size", trade.ExecutedSize);
        cmd.Parameters.AddWithValue("deal_reference", (object?)trade.DealReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deal_id", (object?)trade.DealId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", trade.Status.ToString());
        cmd.Parameters.AddWithValue("account_id", trade.AccountId);
        cmd.Parameters.AddWithValue("account_name", trade.AccountName);
        cmd.Parameters.AddWithValue("is_demo", trade.IsDemo);
        cmd.Parameters.AddWithValue("account_currency", trade.AccountCurrency);
        cmd.Parameters.AddWithValue("opened_at_utc", (object?)trade.OpenedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("closed_at_utc", (object?)trade.ClosedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pnl", (object?)trade.Pnl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failure_reason", (object?)trade.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error_details", (object?)trade.ErrorDetails ?? DBNull.Value);
        cmd.Parameters.AddWithValue("force_closed", trade.ForceClosed);
        cmd.Parameters.AddWithValue("close_source", (object?)trade.CloseSource?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created_at_utc", trade.CreatedAtUtc);
        cmd.Parameters.AddWithValue("updated_at_utc", trade.UpdatedAtUtc);
    }

    private static ExecutedTrade ReadTrade(NpgsqlDataReader reader) => new()
    {
        ExecutedTradeId = reader.GetInt64(0),
        SignalId = reader.GetGuid(1),
        EvaluationId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        SourceType = Enum.Parse<SignalExecutionSourceType>(reader.GetString(3), true),
        Symbol = reader.GetString(4),
        Instrument = reader.GetString(5),
        Timeframe = reader.GetString(6),
        Direction = Enum.Parse<SignalDirection>(reader.GetString(7), true),
        RecommendedEntryPrice = reader.GetDecimal(8),
        ActualEntryPrice = reader.GetDecimal(9),
        TpPrice = reader.GetDecimal(10),
        SlPrice = reader.GetDecimal(11),
        RequestedSize = reader.GetDecimal(12),
        ExecutedSize = reader.GetDecimal(13),
        DealReference = reader.IsDBNull(14) ? null : reader.GetString(14),
        DealId = reader.IsDBNull(15) ? null : reader.GetString(15),
        Status = Enum.Parse<ExecutedTradeStatus>(reader.GetString(16), true),
        AccountId = reader.GetString(17),
        AccountName = reader.GetString(18),
        IsDemo = reader.GetBoolean(19),
        AccountCurrency = reader.GetString(20),
        OpenedAtUtc = reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
        ClosedAtUtc = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
        Pnl = reader.IsDBNull(23) ? null : reader.GetDecimal(23),
        FailureReason = reader.IsDBNull(24) ? null : reader.GetString(24),
        ErrorDetails = reader.IsDBNull(25) ? null : reader.GetString(25),
        ForceClosed = reader.GetBoolean(26),
        CloseSource = reader.IsDBNull(27) ? null : Enum.Parse<TradeCloseSource>(reader.GetString(27), true),
        CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(28),
        UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(29)
    };
}
