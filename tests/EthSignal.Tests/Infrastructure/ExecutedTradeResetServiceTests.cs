using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public sealed class ExecutedTradeResetServiceTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";
    private readonly DbMigrator _migrator = new(ConnString);
    private readonly ExecutedTradeRepository _tradeRepository = new(ConnString);
    private readonly TradeExecutionRuntimeState _runtimeState = new();

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnString);
        var dbName = builder.Database!;
        builder.Database = "postgres";
        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn);
        check.Parameters.AddWithValue("db", dbName);
        if (await check.ExecuteScalarAsync() == null)
        {
            await using var create = new NpgsqlCommand($@"CREATE DATABASE ""{dbName}""", conn);
            await create.ExecuteNonQueryAsync();
        }

        await _migrator.MigrateAsync();
        await ResetTablesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ResetAsync_Clears_ExecutedTradingSection_Only()
    {
        var signalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await using (var conn = new NpgsqlConnection(ConnString))
        {
            await conn.OpenAsync();

            await using var insertSignal = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".signals
                    (signal_id, symbol, timeframe, signal_time_utc, direction, entry_price, tp_price, sl_price, risk_percent, risk_usd, confidence_score, regime, strategy_version, reasons_json, status)
                VALUES
                    (@signal_id, 'ETHUSD', '5m', NOW(), 'BUY', 2300, 2310, 2290, 0.5, 10, 80, 'BULLISH', 'v1.0', '[""reset-test""]'::jsonb, 'OPEN');", conn);
            insertSignal.Parameters.AddWithValue("signal_id", signalId);
            await insertSignal.ExecuteNonQueryAsync();

            await using var insertOutcome = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".signal_outcomes
                    (signal_id, evaluated_at_utc, bars_observed, tp_hit, sl_hit, partial_win, outcome_label, pnl_r, mfe_price, mae_price, mfe_r, mae_r, closed_at_utc)
                VALUES
                    (@signal_id, NOW(), 4, TRUE, FALSE, FALSE, 'WIN', 1.5, 2310, 2295, 1.5, -0.5, NOW());", conn);
            insertOutcome.Parameters.AddWithValue("signal_id", signalId);
            await insertOutcome.ExecuteNonQueryAsync();
        }

        var executedTradeId = await _tradeRepository.InsertExecutedTradeAsync(new ExecutedTrade
        {
            SignalId = signalId,
            EvaluationId = Guid.NewGuid(),
            SourceType = SignalExecutionSourceType.Recommended,
            Symbol = "ETHUSD",
            Instrument = "ETHUSD",
            Timeframe = "5m",
            Direction = SignalDirection.BUY,
            RecommendedEntryPrice = 2300m,
            ActualEntryPrice = 2300.5m,
            TpPrice = 2310m,
            SlPrice = 2290m,
            RequestedSize = 0.05m,
            ExecutedSize = 0.05m,
            DealReference = "DEAL-REF-1",
            DealId = "DEAL-ID-1",
            Status = ExecutedTradeStatus.Open,
            AccountId = "demo-1",
            AccountName = "DEMOAI",
            IsDemo = true,
            AccountCurrency = "USD",
            OpenedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-6),
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        await using (var conn = new NpgsqlConnection(ConnString))
        {
            await conn.OpenAsync();

            await using var queueCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".trade_execution_queue
                    (signal_id, evaluation_id, source_type, requested_by, requested_size, force_market_execution, candidate_json, status, executed_trade_id, created_at_utc, updated_at_utc)
                VALUES
                    (@signal_id, @evaluation_id, 'Recommended', 'dashboard', 0.05, FALSE, '{}'::jsonb, 'Queued', @executed_trade_id, NOW(), NOW());", conn);
            queueCmd.Parameters.AddWithValue("signal_id", signalId);
            queueCmd.Parameters.AddWithValue("evaluation_id", Guid.NewGuid());
            queueCmd.Parameters.AddWithValue("executed_trade_id", executedTradeId);
            await queueCmd.ExecuteNonQueryAsync();

            await using var attemptCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".execution_attempts
                    (executed_trade_id, signal_id, source_type, attempt_type, success, summary, error_details, broker_payload, created_at_utc)
                VALUES
                    (@executed_trade_id, @signal_id, 'Recommended', 'place_position', TRUE, 'ok', NULL, NULL, NOW());", conn);
            attemptCmd.Parameters.AddWithValue("executed_trade_id", executedTradeId);
            attemptCmd.Parameters.AddWithValue("signal_id", signalId);
            await attemptCmd.ExecuteNonQueryAsync();

            await using var eventCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".execution_events
                    (executed_trade_id, signal_id, source_type, event_type, message, details_json, created_at_utc)
                VALUES
                    (@executed_trade_id, @signal_id, 'Recommended', 'execution_requested', 'queued', '{}'::jsonb, NOW());", conn);
            eventCmd.Parameters.AddWithValue("executed_trade_id", executedTradeId);
            eventCmd.Parameters.AddWithValue("signal_id", signalId);
            await eventCmd.ExecuteNonQueryAsync();

            await using var snapshotCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".account_snapshots
                    (account_id, account_name, currency, balance, equity, available, margin, funds, open_positions, is_demo, hedging_mode, captured_at_utc)
                VALUES
                    ('demo-1', 'DEMOAI', 'USD', 10000, 10050, 9500, 550, 10050, 1, TRUE, FALSE, NOW());", conn);
            await snapshotCmd.ExecuteNonQueryAsync();

            await using var closeCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".close_trade_actions
                    (executed_trade_id, requested_by, reason, success, message, deal_reference, deal_id, close_level, pnl, created_at_utc)
                VALUES
                    (@executed_trade_id, 'dashboard', 'reset-test', TRUE, 'closed', 'CLOSE-REF-1', 'DEAL-ID-1', 2305, 2.5, NOW());", conn);
            closeCmd.Parameters.AddWithValue("executed_trade_id", executedTradeId);
            await closeCmd.ExecuteNonQueryAsync();
        }

        _runtimeState.RecordBrokerError("broker-error");
        _runtimeState.RecordOrderNote("order-note");

        var sut = new ExecutedTradeResetService(ConnString, _runtimeState, NullLogger<ExecutedTradeResetService>.Instance);
        var result = await sut.ResetAsync(CancellationToken.None);

        result.QueueEntriesCleared.Should().Be(1);
        result.ExecutedTradesCleared.Should().Be(1);
        result.ExecutionAttemptsCleared.Should().Be(1);
        result.ExecutionEventsCleared.Should().Be(1);
        result.AccountSnapshotsCleared.Should().Be(1);
        result.CloseActionsCleared.Should().Be(1);

        await using var verifyConn = new NpgsqlConnection(ConnString);
        await verifyConn.OpenAsync();

        (await CountAsync(verifyConn, "trade_execution_queue")).Should().Be(0);
        (await CountAsync(verifyConn, "executed_trades")).Should().Be(0);
        (await CountAsync(verifyConn, "execution_attempts")).Should().Be(0);
        (await CountAsync(verifyConn, "execution_events")).Should().Be(0);
        (await CountAsync(verifyConn, "account_snapshots")).Should().Be(0);
        (await CountAsync(verifyConn, "close_trade_actions")).Should().Be(0);
        (await CountAsync(verifyConn, "signals")).Should().Be(1);
        (await CountAsync(verifyConn, "signal_outcomes")).Should().Be(1);

        _runtimeState.LatestBrokerError.Should().BeNull();
        _runtimeState.LatestOrderNote.Should().BeNull();
    }

    private async Task ResetTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var truncate = new NpgsqlCommand(@"
            TRUNCATE TABLE
                ""ETH"".close_trade_actions,
                ""ETH"".execution_attempts,
                ""ETH"".execution_events,
                ""ETH"".trade_execution_queue,
                ""ETH"".account_snapshots,
                ""ETH"".executed_trades,
                ""ETH"".signal_outcomes,
                ""ETH"".signals
            RESTART IDENTITY CASCADE;", conn);
        await truncate.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(NpgsqlConnection conn, string tableName)
    {
        await using var cmd = new NpgsqlCommand($@"SELECT COUNT(*)::INT FROM ""ETH"".""{tableName}"";", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
