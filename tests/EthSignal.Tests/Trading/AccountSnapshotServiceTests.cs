using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Moq;

namespace EthSignal.Tests.Trading;

public sealed class AccountSnapshotServiceTests
{
    [Fact]
    public async Task GetLatestAsync_PersistsDemoAiSnapshotMetadata()
    {
        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalAccountInfo
            {
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                Balance = 10000m,
                Available = 9500m,
                ProfitLoss = 125m,
                Equity = 10125m,
                HedgingMode = false,
                IsDemo = true,
                ResolutionSource = "accounts.exact-name+explicit-demo",
                ResolvedAtUtc = new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero)
            });
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new CapitalPositionSnapshot
                {
                    DealId = "deal-1",
                    Epic = "ETHUSD",
                    Direction = SignalDirection.BUY,
                    Size = 0.1m,
                    Level = 2500m,
                    Currency = "USD"
                }
            });

        AccountSnapshot? insertedSnapshot = null;
        var repository = new Mock<IExecutedTradeRepository>();
        repository.Setup(r => r.InsertAccountSnapshotAsync(It.IsAny<AccountSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback((AccountSnapshot snapshot, CancellationToken _) => insertedSnapshot = snapshot)
            .ReturnsAsync(7);

        var runtimeState = new TradeExecutionRuntimeState();
        var sut = new AccountSnapshotService(capitalClient.Object, repository.Object, runtimeState);

        var snapshot = await sut.GetLatestAsync(CancellationToken.None);

        snapshot.SnapshotId.Should().Be(7);
        snapshot.AccountId.Should().Be("demo-1");
        snapshot.AccountName.Should().Be("DEMOAI");
        snapshot.IsDemo.Should().BeTrue();
        insertedSnapshot.Should().NotBeNull();
        insertedSnapshot!.AccountId.Should().Be("demo-1");
        insertedSnapshot.AccountName.Should().Be("DEMOAI");
        insertedSnapshot.IsDemo.Should().BeTrue();
        runtimeState.ActiveAccountName.Should().Be("DEMOAI");
        runtimeState.ActiveAccountIsDemo.Should().BeTrue();
    }
}