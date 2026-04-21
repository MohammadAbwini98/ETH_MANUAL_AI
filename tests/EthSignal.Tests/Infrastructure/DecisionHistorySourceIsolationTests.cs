using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public sealed class DecisionHistorySourceIsolationTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";

    private readonly DbMigrator _migrator = new(ConnString);
    private readonly CandleRepository _candleRepository = new(ConnString);
    private readonly DecisionAuditRepository _decisionRepository =
        new(ConnString, NullLogger<DecisionAuditRepository>.Instance);

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnString);
        var dbName = builder.Database!;
        builder.Database = "postgres";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();

        await using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn))
        {
            check.Parameters.AddWithValue("db", dbName);
            if (await check.ExecuteScalarAsync() == null)
            {
                await using var create = new NpgsqlCommand($@"CREATE DATABASE ""{dbName}""", conn);
                await create.ExecuteNonQueryAsync();
            }
        }

        await conn.CloseAsync();

        await _migrator.MigrateAsync();

        await using var truncateConn = new NpgsqlConnection(ConnString);
        await truncateConn.OpenAsync();

        await using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".blocked_signal_outcomes;", truncateConn))
            await cmd.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".generated_signal_outcomes;", truncateConn))
            await cmd.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".signal_decision_audit;", truncateConn))
            await cmd.ExecuteNonQueryAsync();

        foreach (var tf in Timeframe.All)
        {
            await using var cmd = new NpgsqlCommand($@"TRUNCATE TABLE ""ETH"".{tf.Table};", truncateConn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GeneratedHistory_Excludes_Blocked_Decisions()
    {
        var generatedDecision = CreateDecision(
            decisionId: Guid.Parse("10101010-1010-1010-1010-101010101010"),
            evaluationId: Guid.Parse("20101010-1010-1010-1010-101010101010"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 9, 55, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.SIGNAL_GENERATED,
            decisionType: SignalDirection.BUY,
            candidateDirection: SignalDirection.BUY,
            finalBlockReason: null);
        var blockedDecision = CreateDecision(
            decisionId: Guid.Parse("30303030-3030-3030-3030-303030303030"),
            evaluationId: Guid.Parse("40404040-4040-4040-4040-404040404040"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 5, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.OPERATIONAL_BLOCKED,
            decisionType: SignalDirection.NO_TRADE,
            candidateDirection: SignalDirection.SELL,
            finalBlockReason: "Session limit");

        await _decisionRepository.InsertDecisionAsync(generatedDecision);
        await _decisionRepository.InsertDecisionAsync(blockedDecision);

        var sut = new GeneratedSignalHistoryService(
            ConnString,
            _candleRepository,
            NullLogger<GeneratedSignalHistoryService>.Instance);

        var page = await sut.GetHistoryAsync("ETHUSD", pageSize: 50, offset: 0, hours: null);

        page.Total.Should().Be(1);
        page.Signals.Should().ContainSingle();
        page.Signals[0].Signal.SignalId.Should().Be(generatedDecision.DecisionId);
        page.Signals[0].Signal.EvaluationId.Should().Be(generatedDecision.EvaluationId);
    }

    [Fact]
    public async Task BlockedHistory_Excludes_Generated_Decisions()
    {
        var generatedDecision = CreateDecision(
            decisionId: Guid.Parse("50505050-5050-5050-5050-505050505050"),
            evaluationId: Guid.Parse("60606060-6060-6060-6060-606060606060"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 9, 55, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.SIGNAL_GENERATED,
            decisionType: SignalDirection.BUY,
            candidateDirection: SignalDirection.BUY,
            finalBlockReason: null);
        var blockedDecision = CreateDecision(
            decisionId: Guid.Parse("70707070-7070-7070-7070-707070707070"),
            evaluationId: Guid.Parse("80808080-8080-8080-8080-808080808080"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 5, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.OPERATIONAL_BLOCKED,
            decisionType: SignalDirection.NO_TRADE,
            candidateDirection: SignalDirection.BUY,
            finalBlockReason: "Daily loss cap");

        await _decisionRepository.InsertDecisionAsync(generatedDecision);
        await _decisionRepository.InsertDecisionAsync(blockedDecision);

        var sut = new BlockedSignalHistoryService(
            ConnString,
            _candleRepository,
            NullLogger<BlockedSignalHistoryService>.Instance);

        var page = await sut.GetHistoryAsync("ETHUSD", pageSize: 50, offset: 0);

        page.Total.Should().Be(1);
        page.Signals.Should().ContainSingle();
        page.Signals[0].Signal.SignalId.Should().Be(blockedDecision.DecisionId);
        page.Signals[0].Signal.EvaluationId.Should().Be(blockedDecision.EvaluationId);
    }

    [Fact]
    public async Task GeneratedHistory_Excludes_Inconsistent_RiskBlocked_GeneratedRows()
    {
        var generatedDecision = CreateDecision(
            decisionId: Guid.Parse("90909090-9090-9090-9090-909090909090"),
            evaluationId: Guid.Parse("91919191-9191-9191-9191-919191919191"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 9, 55, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.SIGNAL_GENERATED,
            decisionType: SignalDirection.BUY,
            candidateDirection: SignalDirection.BUY,
            finalBlockReason: null,
            lifecycleState: SignalLifecycleState.PERSISTED);
        var inconsistentDecision = CreateDecision(
            decisionId: Guid.Parse("92929292-9292-9292-9292-929292929292"),
            evaluationId: Guid.Parse("93939393-9393-9393-9393-939393939393"),
            barTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
            decisionTimeUtc: new DateTimeOffset(2026, 4, 10, 10, 5, 5, TimeSpan.Zero),
            outcomeCategory: OutcomeCategory.SIGNAL_GENERATED,
            decisionType: SignalDirection.NO_TRADE,
            candidateDirection: SignalDirection.SELL,
            finalBlockReason: "Legacy blocked row",
            lifecycleState: SignalLifecycleState.RISK_BLOCKED);

        await _decisionRepository.InsertDecisionAsync(generatedDecision);
        await _decisionRepository.InsertDecisionAsync(inconsistentDecision);

        var sut = new GeneratedSignalHistoryService(
            ConnString,
            _candleRepository,
            NullLogger<GeneratedSignalHistoryService>.Instance);

        var page = await sut.GetHistoryAsync("ETHUSD", pageSize: 50, offset: 0, hours: null);

        page.Total.Should().Be(1);
        page.Signals.Should().ContainSingle();
        page.Signals[0].Signal.SignalId.Should().Be(generatedDecision.DecisionId);

        var inconsistent = await sut.GetBySignalIdAsync("ETHUSD", inconsistentDecision.DecisionId);
        inconsistent.Should().BeNull();
    }

    private static SignalDecision CreateDecision(
        Guid decisionId,
        Guid evaluationId,
        DateTimeOffset barTimeUtc,
        DateTimeOffset decisionTimeUtc,
        OutcomeCategory outcomeCategory,
        SignalDirection decisionType,
        SignalDirection? candidateDirection,
        string? finalBlockReason,
        SignalLifecycleState? lifecycleState = null)
    {
        return new SignalDecision
        {
            DecisionId = decisionId,
            EvaluationId = evaluationId,
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = decisionTimeUtc,
            BarTimeUtc = barTimeUtc,
            DecisionType = decisionType,
            OutcomeCategory = outcomeCategory,
            LifecycleState = lifecycleState ?? (outcomeCategory == OutcomeCategory.SIGNAL_GENERATED
                ? SignalLifecycleState.PERSISTED
                : SignalLifecycleState.SESSION_BLOCKED),
            FinalBlockReason = finalBlockReason,
            Origin = DecisionOrigin.CLOSED_BAR,
            UsedRegime = Regime.BULLISH,
            ReasonCodes = [RejectReasonCode.SCORE_BELOW_THRESHOLD],
            ReasonDetails = ["decision-history-source-isolation"],
            ConfidenceScore = 72,
            IndicatorSnapshot = new Dictionary<string, decimal>
            {
                ["close_mid"] = 2200m,
                ["spread"] = 0.5m,
                ["atr14"] = 12m
            },
            ParameterSetId = "v3.1",
            SourceMode = SourceMode.LIVE,
            CandidateDirection = candidateDirection
        };
    }
}
