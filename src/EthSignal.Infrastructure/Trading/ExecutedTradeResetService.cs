using Npgsql;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Trading;

public sealed class ExecutedTradeResetService : IExecutedTradeResetService
{
    private readonly string _connectionString;
    private readonly TradeExecutionRuntimeState _runtimeState;
    private readonly ILogger<ExecutedTradeResetService> _logger;

    public ExecutedTradeResetService(
        string connectionString,
        TradeExecutionRuntimeState runtimeState,
        ILogger<ExecutedTradeResetService> logger)
    {
        _connectionString = connectionString;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    public async Task<ExecutedTradeResetResult> ResetAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[ExecutedTradeReset] Reset started for executed-trades dashboard section");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in new[]
                 {
                     "trade_execution_queue",
                     "executed_trades",
                     "execution_attempts",
                     "execution_events",
                     "account_snapshots",
                     "close_trade_actions"
                 })
        {
            await using var countCmd = new NpgsqlCommand($@"SELECT COUNT(*)::INT FROM ""ETH"".""{tableName}"";", conn, tx);
            counts[tableName] = (int)(await countCmd.ExecuteScalarAsync(ct))!;
        }

        await using (var truncateCmd = new NpgsqlCommand(@"
            TRUNCATE TABLE
                ""ETH"".close_trade_actions,
                ""ETH"".execution_attempts,
                ""ETH"".execution_events,
                ""ETH"".trade_execution_queue,
                ""ETH"".account_snapshots,
                ""ETH"".executed_trades
            RESTART IDENTITY;", conn, tx))
        {
            await truncateCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _runtimeState.ClearExecutionTelemetry();

        var result = new ExecutedTradeResetResult
        {
            ResetAtUtc = DateTimeOffset.UtcNow,
            QueueEntriesCleared = counts["trade_execution_queue"],
            ExecutedTradesCleared = counts["executed_trades"],
            ExecutionAttemptsCleared = counts["execution_attempts"],
            ExecutionEventsCleared = counts["execution_events"],
            AccountSnapshotsCleared = counts["account_snapshots"],
            CloseActionsCleared = counts["close_trade_actions"]
        };

        _logger.LogWarning(
            "[ExecutedTradeReset] Reset completed: Queue={QueueEntriesCleared}, Trades={ExecutedTradesCleared}, Attempts={ExecutionAttemptsCleared}, Events={ExecutionEventsCleared}, Snapshots={AccountSnapshotsCleared}, CloseActions={CloseActionsCleared}",
            result.QueueEntriesCleared,
            result.ExecutedTradesCleared,
            result.ExecutionAttemptsCleared,
            result.ExecutionEventsCleared,
            result.AccountSnapshotsCleared,
            result.CloseActionsCleared);

        return result;
    }
}
