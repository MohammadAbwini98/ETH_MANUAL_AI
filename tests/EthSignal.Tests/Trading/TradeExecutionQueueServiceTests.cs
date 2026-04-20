using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Collections.Concurrent;

namespace EthSignal.Tests.Trading;

public sealed class TradeExecutionQueueServiceTests
{
    [Fact]
    public async Task EnqueueAsync_WhenSignalAlreadyHasActiveExecution_ReturnsDuplicate()
    {
        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutedTrade
            {
                ExecutedTradeId = 99,
                SignalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                SourceType = SignalExecutionSourceType.Generated,
                Symbol = "ETHUSD",
                Instrument = "ETHUSD",
                Timeframe = "5m",
                Direction = SignalDirection.BUY,
                Status = ExecutedTradeStatus.Open,
                AccountCurrency = "USD",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

        var sut = CreateSut(executedTradeRepository: executedTradeRepository);

        var result = await sut.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate(SignalExecutionSourceType.Generated, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureReason.Should().Be("DuplicateExecution");
    }

    [Fact]
    public async Task EnqueueAsync_WhenTimeframeIs1m_RejectsImmediately()
    {
        var sut = CreateSut();

        var result = await sut.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate(SignalExecutionSourceType.Recommended, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") with
            {
                Timeframe = "1m"
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureReason.Should().Be("TimeframeNotAllowed");
        result.Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task DrainAsync_WhenActiveTradeLimitReached_DoesNotExecuteQueuedTrade()
    {
        var queueEntry = CreateQueueEntry("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var queueRepository = new Mock<ITradeExecutionQueueRepository>();
        queueRepository.Setup(r => r.GetNextQueuedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueEntry);

        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var executionService = new Mock<ITradeExecutionService>(MockBehavior.Strict);
        var sut = CreateSut(
            queueRepository: queueRepository,
            executedTradeRepository: executedTradeRepository,
            executionService: executionService,
            maxConcurrentOpenTrades: 3);

        var processed = await sut.DrainAsync(CancellationToken.None);

        processed.Should().Be(0);
        executionService.Verify(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        queueRepository.Verify(r => r.GetNextQueuedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DrainAsync_WhenCapacityExists_ProcessesQueuedTradesInOrder()
    {
        var first = CreateQueueEntry("11111111-1111-1111-1111-111111111111", 1, DateTimeOffset.UtcNow.AddMinutes(-2));
        var second = CreateQueueEntry("22222222-2222-2222-2222-222222222222", 2, DateTimeOffset.UtcNow.AddMinutes(-1));
        var entries = new Queue<QueuedTradeExecution>([first, second]);
        var queueRepository = new Mock<ITradeExecutionQueueRepository>();
        queueRepository.Setup(r => r.GetNextQueuedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => entries.Count > 0 ? entries.Dequeue() : null);
        queueRepository.Setup(r => r.HasQueuedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => entries.Count > 0);

        var updatedEntries = new List<QueuedTradeExecution>();
        queueRepository.Setup(r => r.UpdateAsync(It.IsAny<QueuedTradeExecution>(), It.IsAny<CancellationToken>()))
            .Callback((QueuedTradeExecution entry, CancellationToken _) => updatedEntries.Add(entry))
            .Returns(Task.CompletedTask);

        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        executedTradeRepository.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);

        var processedSignals = new List<Guid>();
        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((TradeExecutionRequest request, CancellationToken _) => processedSignals.Add(request.Candidate.SignalId))
            .ReturnsAsync((TradeExecutionRequest request, CancellationToken _) => new TradeExecutionResult
            {
                Success = true,
                ExecutedTradeId = request.Candidate.SignalId == first.SignalId ? 10 : 11,
                Status = ExecutedTradeStatus.Open,
                Message = "ok"
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executedTradeRepository: executedTradeRepository,
            executionService: executionService,
            maxConcurrentOpenTrades: 3);

        var processed = await sut.DrainAsync(CancellationToken.None);

        processed.Should().Be(2);
        processedSignals.Should().Equal(first.SignalId, second.SignalId);
        updatedEntries.Count(entry => entry.Status == TradeExecutionQueueStatus.Completed).Should().Be(2);
    }

    [Fact]
    public async Task DrainAsync_WhenFourQueuedEntriesExist_UsesThreeRequestBurstThenCooldown()
    {
        var entries = new Queue<QueuedTradeExecution>([
            CreateQueueEntry("11111111-1111-1111-1111-111111111111", 1, DateTimeOffset.UtcNow.AddMinutes(-4)),
            CreateQueueEntry("22222222-2222-2222-2222-222222222222", 2, DateTimeOffset.UtcNow.AddMinutes(-3)),
            CreateQueueEntry("33333333-3333-3333-3333-333333333333", 3, DateTimeOffset.UtcNow.AddMinutes(-2)),
            CreateQueueEntry("44444444-4444-4444-4444-444444444444", 4, DateTimeOffset.UtcNow.AddMinutes(-1))
        ]);
        var queueRepository = new Mock<ITradeExecutionQueueRepository>();
        queueRepository.Setup(r => r.GetNextQueuedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => entries.Count > 0 ? entries.Dequeue() : null);
        queueRepository.Setup(r => r.HasQueuedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => entries.Count > 0);
        queueRepository.Setup(r => r.UpdateAsync(It.IsAny<QueuedTradeExecution>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        executedTradeRepository.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);

        var currentConcurrency = 0;
        var maxConcurrency = 0;
        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (TradeExecutionRequest request, CancellationToken _) =>
            {
                var now = Interlocked.Increment(ref currentConcurrency);
                var snapshotMax = Volatile.Read(ref maxConcurrency);
                while (now > snapshotMax)
                {
                    Interlocked.CompareExchange(ref maxConcurrency, now, snapshotMax);
                    snapshotMax = Volatile.Read(ref maxConcurrency);
                }

                await Task.Delay(25, CancellationToken.None);
                Interlocked.Decrement(ref currentConcurrency);
                return new TradeExecutionResult
                {
                    Success = true,
                    ExecutedTradeId = request.Candidate.SignalId.GetHashCode(),
                    Status = ExecutedTradeStatus.Open,
                    Message = "ok"
                };
            });

        var cooldowns = new ConcurrentQueue<TimeSpan>();
        var sut = CreateSut(
            queueRepository: queueRepository,
            executedTradeRepository: executedTradeRepository,
            executionService: executionService,
            maxConcurrentOpenTrades: 10,
            queueConcurrentRequestLimit: 3,
            queueCooldownMilliseconds: 500,
            delayAsync: (delay, _) =>
            {
                cooldowns.Enqueue(delay);
                return Task.CompletedTask;
            });

        var processed = await sut.DrainAsync(CancellationToken.None);

        processed.Should().Be(4);
        maxConcurrency.Should().Be(3);
        cooldowns.Should().ContainSingle();
        cooldowns.TryPeek(out var delay).Should().BeTrue();
        delay.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsQueueIdsStatusesAndTimes()
    {
        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var queueRepository = new Mock<ITradeExecutionQueueRepository>();
        queueRepository.Setup(r => r.GetStatusCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((2, 1, 4, 1));
        queueRepository.Setup(r => r.GetActiveEntriesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateQueueEntry("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 42, createdAtUtc) with
                {
                    SourceType = SignalExecutionSourceType.Recommended,
                    Status = TradeExecutionQueueStatus.Queued,
                    UpdatedAtUtc = updatedAtUtc
                }
            ]);

        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var sut = CreateSut(
            queueRepository: queueRepository,
            executedTradeRepository: executedTradeRepository,
            maxConcurrentOpenTrades: 3,
            queueConcurrentRequestLimit: 3,
            queueCooldownMilliseconds: 500);

        var snapshot = await sut.GetSnapshotAsync(25, CancellationToken.None);

        snapshot.ActiveTradeCount.Should().Be(2);
        snapshot.MaxConcurrentOpenTrades.Should().Be(3);
        snapshot.QueueConcurrentRequestLimit.Should().Be(3);
        snapshot.QueueCooldownMilliseconds.Should().Be(500);
        snapshot.QueuedCount.Should().Be(2);
        snapshot.ProcessingCount.Should().Be(1);
        snapshot.Entries.Should().ContainSingle();
        snapshot.Entries[0].QueueEntryId.Should().Be(42);
        snapshot.Entries[0].SignalId.Should().Be(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        snapshot.Entries[0].Status.Should().Be(TradeExecutionQueueStatus.Queued);
        snapshot.Entries[0].CreatedAtUtc.Should().Be(createdAtUtc);
        snapshot.Entries[0].UpdatedAtUtc.Should().Be(updatedAtUtc);
        snapshot.Entries[0].AgeSeconds.Should().BePositive();
        snapshot.Entries[0].WaitSeconds.Should().BePositive();
    }

    private static TradeExecutionQueueService CreateSut(
        Mock<ITradeExecutionQueueRepository>? queueRepository = null,
        Mock<IExecutedTradeRepository>? executedTradeRepository = null,
        Mock<ITradeExecutionService>? executionService = null,
        int maxConcurrentOpenTrades = 3,
        int queueConcurrentRequestLimit = 3,
        int queueCooldownMilliseconds = 500,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.GetSettings()).Returns(new TradeExecutionPolicySettings
        {
            Enabled = true,
            MaxConcurrentOpenTrades = maxConcurrentOpenTrades,
            QueueConcurrentRequestLimit = queueConcurrentRequestLimit,
            QueueCooldownMilliseconds = queueCooldownMilliseconds,
            DemoOnly = true,
            AllowedSourceTypes = new HashSet<SignalExecutionSourceType>
            {
                SignalExecutionSourceType.Recommended,
                SignalExecutionSourceType.Generated,
                SignalExecutionSourceType.Blocked
            }
        });

        var accountSnapshotService = new Mock<IAccountSnapshotService>();
        accountSnapshotService.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot
            {
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                IsDemo = true,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });

        return new TradeExecutionQueueService(
            queueRepository?.Object ?? Mock.Of<ITradeExecutionQueueRepository>(),
            executedTradeRepository?.Object ?? Mock.Of<IExecutedTradeRepository>(),
            policy.Object,
            executionService?.Object ?? Mock.Of<ITradeExecutionService>(),
            accountSnapshotService.Object,
            NullLogger<TradeExecutionQueueService>.Instance,
            delayAsync);
    }

    private static TradeExecutionCandidate CreateCandidate(SignalExecutionSourceType sourceType, string signalId) => new()
    {
        SignalId = Guid.Parse(signalId),
        EvaluationId = Guid.NewGuid(),
        SourceType = sourceType,
        Symbol = "ETHUSD",
        Timeframe = "5m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        Direction = SignalDirection.BUY,
        RecommendedEntryPrice = 2500m,
        TpPrice = 2510m,
        SlPrice = 2490m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 80,
        Regime = Regime.BULLISH,
        Reasons = ["test"]
    };

    private static QueuedTradeExecution CreateQueueEntry(string signalId, long queueId = 1, DateTimeOffset? createdAtUtc = null) => new()
    {
        QueueEntryId = queueId,
        SignalId = Guid.Parse(signalId),
        EvaluationId = Guid.NewGuid(),
        SourceType = SignalExecutionSourceType.Generated,
        RequestedBy = "test",
        RequestedSize = 0.05m,
        CandidateJson = System.Text.Json.JsonSerializer.Serialize(CreateCandidate(SignalExecutionSourceType.Generated, signalId)),
        Status = TradeExecutionQueueStatus.Queued,
        CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow,
        UpdatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow
    };
}
