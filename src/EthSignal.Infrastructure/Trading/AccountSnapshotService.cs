using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;

namespace EthSignal.Infrastructure.Trading;

public sealed class AccountSnapshotService : IAccountSnapshotService
{
    private readonly ICapitalTradingClient _capitalClient;
    private readonly IExecutedTradeRepository _repository;
    private readonly TradeExecutionRuntimeState _runtimeState;

    public AccountSnapshotService(
        ICapitalTradingClient capitalClient,
        IExecutedTradeRepository repository,
        TradeExecutionRuntimeState runtimeState)
    {
        _capitalClient = capitalClient;
        _repository = repository;
        _runtimeState = runtimeState;
    }

    public async Task<AccountSnapshot> GetLatestAsync(CancellationToken ct = default)
    {
        var info = await _capitalClient.GetAccountInfoAsync(ct);
        var openPositions = await _capitalClient.GetOpenPositionsAsync(ct);
        _runtimeState.RecordAccountContext(info);

        var snapshot = new AccountSnapshot
        {
            AccountId = info.AccountId,
            AccountName = info.AccountName,
            Currency = info.Currency,
            Balance = info.Balance,
            Equity = info.Equity,
            Available = info.Available,
            Margin = Math.Max(0m, info.Equity - info.Available),
            Funds = info.Balance,
            OpenPositions = openPositions.Count,
            IsDemo = info.IsDemo,
            HedgingMode = info.HedgingMode,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };

        var snapshotId = await _repository.InsertAccountSnapshotAsync(snapshot, ct);
        _runtimeState.RecordSync(sessionReady: true, note: $"Account snapshot captured ({snapshot.AccountName} / {snapshot.Currency})");
        return snapshot with { SnapshotId = snapshotId };
    }
}
