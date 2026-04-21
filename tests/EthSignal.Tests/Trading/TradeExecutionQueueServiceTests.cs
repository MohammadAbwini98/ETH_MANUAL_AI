using System.Collections.Concurrent;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Trading;

public sealed class TradeExecutionQueueServiceTests
{
    [Fact]
    public async Task EnqueueAsync_WhenSignalIsAccepted_SignalsImmediateWork()
    {
        var queueRepository = new InMemoryQueueRepository();
        var sut = CreateSut(queueRepository: queueRepository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var waitTask = sut.WaitForWorkAsync(cts.Token);

        var result = await sut.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate(SignalExecutionSourceType.Generated, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            RequestedBy = "test"
        }, CancellationToken.None);

        await waitTask;

        result.Accepted.Should().BeTrue();
        result.Status.Should().Be(TradeExecutionQueueStatus.Queued.ToString());
        queueRepository.GetEntries().Should().ContainSingle();
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
    public async Task DrainAsync_WhenCapacityExists_DispatchesQueuedTradesInFifoOrder()
    {
        var queueRepository = new InMemoryQueueRepository();
        await queueRepository.InsertAsync(CreateQueueEntry("11111111-1111-1111-1111-111111111111", 1, DateTimeOffset.UtcNow.AddMinutes(-2)));
        await queueRepository.InsertAsync(CreateQueueEntry("22222222-2222-2222-2222-222222222222", 2, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var processedSignals = new ConcurrentQueue<Guid>();
        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (TradeExecutionRequest request, CancellationToken _) =>
            {
                processedSignals.Enqueue(request.Candidate.SignalId);
                await Task.Delay(25);
                return new TradeExecutionResult
                {
                    Success = true,
                    ExecutedTradeId = request.Candidate.SignalId == Guid.Parse("11111111-1111-1111-1111-111111111111") ? 10 : 11,
                    Status = ExecutedTradeStatus.Open,
                    Message = "ok"
                };
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executionService: executionService,
            maxConcurrentOpenTrades: 3,
            maxConcurrentRequests: 3);

        var dispatched = await sut.DrainAsync(CancellationToken.None);
        await AwaitConditionAsync(() => queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Completed) == 2);

        dispatched.Should().Be(2);
        processedSignals.Should().Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Completed).Should().Be(2);
    }

    [Fact]
    public async Task DrainAsync_LimitsConcurrentDispatchToThree_AndFourthWaitsForNextSignal()
    {
        var queueRepository = new InMemoryQueueRepository();
        await queueRepository.InsertAsync(CreateQueueEntry("11111111-1111-1111-1111-111111111111", 1, DateTimeOffset.UtcNow.AddMinutes(-4)));
        await queueRepository.InsertAsync(CreateQueueEntry("22222222-2222-2222-2222-222222222222", 2, DateTimeOffset.UtcNow.AddMinutes(-3)));
        await queueRepository.InsertAsync(CreateQueueEntry("33333333-3333-3333-3333-333333333333", 3, DateTimeOffset.UtcNow.AddMinutes(-2)));
        await queueRepository.InsertAsync(CreateQueueEntry("44444444-4444-4444-4444-444444444444", 4, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var currentConcurrency = 0;
        var maxConcurrency = 0;

        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (TradeExecutionRequest request, CancellationToken _) =>
            {
                var now = Interlocked.Increment(ref currentConcurrency);
                while (true)
                {
                    var snapshot = Volatile.Read(ref maxConcurrency);
                    if (now <= snapshot)
                        break;
                    if (Interlocked.CompareExchange(ref maxConcurrency, now, snapshot) == snapshot)
                        break;
                }

                await Task.Delay(25);
                Interlocked.Decrement(ref currentConcurrency);
                return new TradeExecutionResult
                {
                    Success = true,
                    ExecutedTradeId = request.Candidate.SignalId.GetHashCode(),
                    Status = ExecutedTradeStatus.Open,
                    Message = "ok"
                };
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executionService: executionService,
            maxConcurrentOpenTrades: 10,
            maxConcurrentRequests: 3);

        var firstDispatch = await sut.DrainAsync(CancellationToken.None);
        firstDispatch.Should().Be(3);
        await AwaitConditionAsync(() => queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Completed) == 3);
        maxConcurrency.Should().Be(3);
        queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Queued).Should().Be(1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await sut.WaitForWorkAsync(cts.Token);
        var secondDispatch = await sut.DrainAsync(CancellationToken.None);
        secondDispatch.Should().Be(1);
        await AwaitConditionAsync(() => queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Completed) == 4);
        queueRepository.GetEntries().Count(entry => entry.Status == TradeExecutionQueueStatus.Completed).Should().Be(4);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsIdsTimesAndDynamicCapacity()
    {
        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var queueRepository = new InMemoryQueueRepository();
        await queueRepository.InsertAsync(CreateQueueEntry("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 42, createdAtUtc));

        var executedTradeRepository = new Mock<IExecutedTradeRepository>();
        executedTradeRepository.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);
        executedTradeRepository.Setup(r => r.GetPendingOrSubmittedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        executedTradeRepository.Setup(r => r.GetLatestAccountSnapshotAsync(It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountSnapshot?)null);

        var snapshotService = new Mock<IAccountSnapshotService>();
        snapshotService.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot
            {
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                OpenPositions = 1,
                IsDemo = true,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executedTradeRepository: executedTradeRepository,
            snapshotService: snapshotService,
            maxConcurrentOpenTrades: 3,
            maxConcurrentRequests: 3);

        var snapshot = await sut.GetSnapshotAsync(25, CancellationToken.None);

        snapshot.ActiveTradeCount.Should().Be(2);
        snapshot.BrokerOpenTradeCount.Should().Be(1);
        snapshot.PendingSubmissionCount.Should().Be(1);
        snapshot.AvailableDispatchSlots.Should().Be(1);
        snapshot.QueueConcurrentRequestLimit.Should().Be(3);
        snapshot.QueuedCount.Should().Be(1);
        snapshot.Entries.Should().ContainSingle();
        snapshot.Entries[0].QueueEntryId.Should().Be(42);
        snapshot.Entries[0].CreatedAtUtc.Should().Be(createdAtUtc);
        snapshot.Entries[0].AgeSeconds.Should().BePositive();
    }

    [Fact]
    public async Task DrainAsync_WhenExecutionRequestsForceMarketRetry_RequeuesEntryWithoutFailedStatus()
    {
        var queueRepository = new InMemoryQueueRepository();
        await queueRepository.InsertAsync(CreateQueueEntry("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeExecutionResult
            {
                Success = false,
                Status = ExecutedTradeStatus.Queued,
                FailureReason = "EntryDriftExceeded",
                Message = "retry with market",
                RetryRequested = true,
                RetryWithForceMarketExecution = true
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executionService: executionService);

        var dispatched = await sut.DrainAsync(CancellationToken.None);
        await AwaitConditionAsync(() => queueRepository.GetEntries().Single().Status == TradeExecutionQueueStatus.Queued
                                        && queueRepository.GetEntries().Single().ForceMarketExecution);

        dispatched.Should().Be(1);
        var entry = queueRepository.GetEntries().Single();
        entry.Status.Should().Be(TradeExecutionQueueStatus.Queued);
        entry.ForceMarketExecution.Should().BeTrue();
        entry.FailureReason.Should().Be("EntryDriftExceeded");
    }

    [Fact]
    public async Task DrainAsync_WhenExecutionEndsInValidationFailure_CompletesQueueEntry()
    {
        var queueRepository = new InMemoryQueueRepository();
        await queueRepository.InsertAsync(CreateQueueEntry("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 1, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var executionService = new Mock<ITradeExecutionService>();
        executionService.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeExecutionResult
            {
                Success = false,
                ExecutedTradeId = 77,
                Status = ExecutedTradeStatus.ValidationFailed,
                FailureReason = "InvalidTpSl",
                Message = "TP/SL invalid"
            });

        var sut = CreateSut(
            queueRepository: queueRepository,
            executionService: executionService);

        var dispatched = await sut.DrainAsync(CancellationToken.None);
        await AwaitConditionAsync(() => queueRepository.GetEntries().Single().Status == TradeExecutionQueueStatus.Completed);

        dispatched.Should().Be(1);
        var entry = queueRepository.GetEntries().Single();
        entry.Status.Should().Be(TradeExecutionQueueStatus.Completed);
        entry.ExecutedTradeId.Should().Be(77);
        entry.FailureReason.Should().Be("InvalidTpSl");
    }

    private static TradeExecutionQueueService CreateSut(
        InMemoryQueueRepository? queueRepository = null,
        Mock<IExecutedTradeRepository>? executedTradeRepository = null,
        Mock<ITradeExecutionService>? executionService = null,
        Mock<IAccountSnapshotService>? snapshotService = null,
        int maxConcurrentOpenTrades = 3,
        int maxConcurrentRequests = 3)
    {
        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.GetSettings()).Returns(new TradeExecutionPolicySettings
        {
            Enabled = true,
            MaxConcurrentOpenTrades = maxConcurrentOpenTrades,
            QueueConcurrentRequestLimit = maxConcurrentRequests,
            DemoOnly = true,
            AllowedSourceTypes = new HashSet<SignalExecutionSourceType>
            {
                SignalExecutionSourceType.Recommended,
                SignalExecutionSourceType.Generated,
                SignalExecutionSourceType.Blocked
            }
        });

        var executedRepo = executedTradeRepository ?? new Mock<IExecutedTradeRepository>();
        executedRepo.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);
        if (executedTradeRepository == null)
        {
            executedRepo.Setup(r => r.GetPendingOrSubmittedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            executedRepo.Setup(r => r.GetLatestAccountSnapshotAsync(It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountSnapshot
                {
                    AccountId = "demo-1",
                    AccountName = "DEMOAI",
                    Currency = "USD",
                    OpenPositions = 0,
                    IsDemo = true,
                    CapturedAtUtc = DateTimeOffset.UtcNow
                });
        }

        var accounts = snapshotService ?? new Mock<IAccountSnapshotService>();
        if (snapshotService == null)
        {
            accounts.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountSnapshot
                {
                    AccountId = "demo-1",
                    AccountName = "DEMOAI",
                    Currency = "USD",
                    OpenPositions = 0,
                    IsDemo = true,
                    CapturedAtUtc = DateTimeOffset.UtcNow
                });
        }

        return new TradeExecutionQueueService(
            queueRepository ?? new InMemoryQueueRepository(),
            executedRepo.Object,
            policy.Object,
            executionService?.Object ?? Mock.Of<ITradeExecutionService>(),
            accounts.Object,
            NullLogger<TradeExecutionQueueService>.Instance);
    }

    private static QueuedTradeExecution CreateQueueEntry(string signalId, long queueEntryId = 1, DateTimeOffset? createdAtUtc = null) => new()
    {
        QueueEntryId = queueEntryId,
        SignalId = Guid.Parse(signalId),
        EvaluationId = Guid.NewGuid(),
        SourceType = SignalExecutionSourceType.Recommended,
        RequestedBy = "test",
        RequestedSize = 0.05m,
        CandidateJson = System.Text.Json.JsonSerializer.Serialize(CreateCandidate(SignalExecutionSourceType.Recommended, signalId)),
        Status = TradeExecutionQueueStatus.Queued,
        CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow,
        UpdatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow
    };

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

    private static async Task AwaitConditionAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        predicate().Should().BeTrue();
    }

    private sealed class InMemoryQueueRepository : ITradeExecutionQueueRepository
    {
        private readonly object _gate = new();
        private readonly List<QueuedTradeExecution> _entries = [];
        private long _nextId = 1;

        public Task<long> InsertAsync(QueuedTradeExecution entry, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var queueEntryId = entry.QueueEntryId > 0 ? entry.QueueEntryId : _nextId++;
                _entries.Add(entry with { QueueEntryId = queueEntryId });
                return Task.FromResult(queueEntryId);
            }
        }

        public Task UpdateAsync(QueuedTradeExecution entry, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var index = _entries.FindIndex(item => item.QueueEntryId == entry.QueueEntryId);
                if (index >= 0)
                    _entries[index] = entry;
                return Task.CompletedTask;
            }
        }

        public Task<QueuedTradeExecution?> GetActiveBySourceSignalAsync(Guid signalId, SignalExecutionSourceType sourceType, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_entries
                    .Where(entry => entry.SignalId == signalId
                                    && entry.SourceType == sourceType
                                    && entry.Status is TradeExecutionQueueStatus.Queued or TradeExecutionQueueStatus.Processing)
                    .OrderByDescending(entry => entry.CreatedAtUtc)
                    .FirstOrDefault());
            }
        }

        public Task<QueuedTradeExecution?> TryClaimNextQueuedAsync(DateTimeOffset claimedAtUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var next = _entries
                    .Where(entry => entry.Status == TradeExecutionQueueStatus.Queued)
                    .OrderBy(entry => entry.CreatedAtUtc)
                    .ThenBy(entry => entry.QueueEntryId)
                    .FirstOrDefault();
                if (next == null)
                    return Task.FromResult<QueuedTradeExecution?>(null);

                var claimed = next with
                {
                    Status = TradeExecutionQueueStatus.Processing,
                    UpdatedAtUtc = claimedAtUtc
                };
                var index = _entries.FindIndex(entry => entry.QueueEntryId == claimed.QueueEntryId);
                _entries[index] = claimed;
                return Task.FromResult<QueuedTradeExecution?>(claimed);
            }
        }

        public Task<bool> HasQueuedAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_entries.Any(entry => entry.Status == TradeExecutionQueueStatus.Queued));
            }
        }

        public Task<IReadOnlyList<QueuedTradeExecution>> GetActiveEntriesAsync(int limit = 50, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<QueuedTradeExecution>>(_entries
                    .Where(entry => entry.Status is TradeExecutionQueueStatus.Queued or TradeExecutionQueueStatus.Processing)
                    .OrderBy(entry => entry.CreatedAtUtc)
                    .ThenBy(entry => entry.QueueEntryId)
                    .Take(limit)
                    .ToList());
            }
        }

        public Task<(int QueuedCount, int ProcessingCount, int CompletedCount, int FailedCount)> GetStatusCountsAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult((
                    _entries.Count(entry => entry.Status == TradeExecutionQueueStatus.Queued),
                    _entries.Count(entry => entry.Status == TradeExecutionQueueStatus.Processing),
                    _entries.Count(entry => entry.Status == TradeExecutionQueueStatus.Completed),
                    _entries.Count(entry => entry.Status == TradeExecutionQueueStatus.Failed)));
            }
        }

        public Task<int> RequeueStaleProcessingAsync(DateTimeOffset staleBeforeUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var recovered = 0;
                for (var index = 0; index < _entries.Count; index++)
                {
                    var entry = _entries[index];
                    if (entry.Status != TradeExecutionQueueStatus.Processing || entry.UpdatedAtUtc >= staleBeforeUtc)
                        continue;

                    _entries[index] = entry with
                    {
                        Status = TradeExecutionQueueStatus.Queued,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        FailureReason = "StaleProcessingRecovered",
                        ErrorDetails = "Recovered stale processing entry for re-dispatch."
                    };
                    recovered++;
                }

                return Task.FromResult(recovered);
            }
        }

        public IReadOnlyList<QueuedTradeExecution> GetEntries()
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }
}
