using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Normalizes persisted recommended signals and reconstructed generated/blocked
/// decision histories into one dashboard-facing feed.
/// </summary>
public sealed class SignalHistoryService : ISignalHistoryService
{
    private static readonly TimeZoneInfo AmmanTimeZone = ResolveAmmanTimeZone();

    private readonly ISignalRepository _signalRepository;
    private readonly IBlockedSignalHistoryService _blockedHistory;
    private readonly IGeneratedSignalHistoryService _generatedHistory;
    private readonly IExecutedTradeRepository _executedTradeRepository;
    private readonly IExecutionCandidateMapper _mapper;

    public SignalHistoryService(
        ISignalRepository signalRepository,
        IBlockedSignalHistoryService blockedHistory,
        IGeneratedSignalHistoryService generatedHistory,
        IExecutedTradeRepository executedTradeRepository,
        IExecutionCandidateMapper mapper)
    {
        _signalRepository = signalRepository;
        _blockedHistory = blockedHistory;
        _generatedHistory = generatedHistory;
        _executedTradeRepository = executedTradeRepository;
        _mapper = mapper;
    }

    public async Task<SignalHistoryPage> GetHistoryAsync(SignalHistoryQuery query, CancellationToken ct = default)
    {
        var recommendedTask = IncludesSource(query, SignalExecutionSourceType.Recommended)
            ? _signalRepository.GetSignalHistoryWithOutcomesAsync(query.Symbol, int.MaxValue, 0, ct)
            : Task.FromResult<IReadOnlyList<SignalWithOutcome>>(Array.Empty<SignalWithOutcome>());
        var generatedTask = LoadGeneratedAsync(query, ct);
        var blockedTask = LoadBlockedAsync(query, ct);

        await Task.WhenAll(recommendedTask, generatedTask, blockedTask);

        var entries = new List<SignalHistoryEntry>();
        entries.AddRange(recommendedTask.Result.Select(MapRecommended));
        if (generatedTask.Result != null)
            entries.AddRange(generatedTask.Result.Signals.Select(MapGenerated));
        if (blockedTask.Result != null)
            entries.AddRange(blockedTask.Result.Signals.Select(MapBlocked));

        entries = entries
            .Where(item => MatchesBasicFilters(item, query))
            .ToList();

        entries = await AttachExecutionAsync(entries, ct);
        entries = entries
            .Where(item => MatchesOutcome(item, query.Outcome))
            .ToList();

        var ordered = ApplySorting(entries, query.SortBy, query.SortDirection).ToList();
        var total = ordered.Count;
        var pageSize = Math.Clamp(query.Limit, 1, 500);
        var pageItems = ordered
            .Skip(Math.Max(0, query.Offset))
            .Take(pageSize)
            .ToList();

        return new SignalHistoryPage
        {
            Signals = pageItems,
            Total = total,
            Page = (Math.Max(0, query.Offset) / pageSize) + 1,
            PageSize = pageSize
        };
    }

    public async Task<TradeExecutionCandidate?> GetExecutionCandidateAsync(
        string symbol,
        SignalExecutionSourceType sourceType,
        Guid signalId,
        CancellationToken ct = default)
    {
        return sourceType switch
        {
            SignalExecutionSourceType.Recommended => await GetRecommendedCandidateAsync(signalId, ct),
            SignalExecutionSourceType.Generated => await GetGeneratedCandidateAsync(symbol, signalId, ct),
            SignalExecutionSourceType.Blocked => await GetBlockedCandidateAsync(symbol, signalId, ct),
            _ => null
        };
    }

    private async Task<TradeExecutionCandidate?> GetRecommendedCandidateAsync(Guid signalId, CancellationToken ct)
    {
        var signal = await _signalRepository.GetSignalByIdAsync(signalId, ct);
        return signal == null ? null : _mapper.FromRecommended(signal);
    }

    private async Task<TradeExecutionCandidate?> GetGeneratedCandidateAsync(
        string symbol,
        Guid signalId,
        CancellationToken ct)
    {
        var signal = await _generatedHistory.GetBySignalIdAsync(symbol, signalId, ct);
        return signal == null ? null : _mapper.FromGenerated(signal.Signal);
    }

    private async Task<TradeExecutionCandidate?> GetBlockedCandidateAsync(
        string symbol,
        Guid signalId,
        CancellationToken ct)
    {
        var signal = await _blockedHistory.GetBySignalIdAsync(symbol, signalId, ct);
        return signal == null ? null : _mapper.FromBlocked(signal.Signal);
    }

    private async Task<List<SignalHistoryEntry>> AttachExecutionAsync(
        IReadOnlyList<SignalHistoryEntry> items,
        CancellationToken ct)
    {
        var executionMaps = new Dictionary<SignalExecutionSourceType, IReadOnlyDictionary<Guid, ExecutedTrade>>();
        foreach (var group in items.GroupBy(i => i.Signal.SourceType))
        {
            var signalIds = group.Select(item => item.Signal.SignalId).Distinct().ToArray();
            if (signalIds.Length == 0)
                continue;

            executionMaps[group.Key] = await _executedTradeRepository.GetLatestBySourceSignalsAsync(
                signalIds,
                group.Key,
                ct);
        }

        var withExecution = new List<SignalHistoryEntry>(items.Count);
        foreach (var item in items)
        {
            ExecutedTrade? execution = null;
            if (executionMaps.TryGetValue(item.Signal.SourceType, out var bySignal)
                && bySignal.TryGetValue(item.Signal.SignalId, out var latest))
            {
                execution = latest;
            }

            withExecution.Add(item with { Execution = execution });
        }

        return withExecution;
    }

    private static bool IncludesSource(SignalHistoryQuery query, SignalExecutionSourceType sourceType)
        => query.SourceType == null || query.SourceType == sourceType;

    private static bool MatchesBasicFilters(SignalHistoryEntry item, SignalHistoryQuery query)
    {
        if (query.Timeframe != null
            && !string.Equals(item.Signal.Timeframe, query.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Direction != null && item.Signal.Direction != query.Direction)
            return false;

        if (query.DateFrom == null && query.DateTo == null)
            return true;

        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(item.Signal.SignalTimeUtc, AmmanTimeZone).DateTime);
        if (query.DateFrom != null && localDate < query.DateFrom.Value)
            return false;
        if (query.DateTo != null && localDate > query.DateTo.Value)
            return false;

        return true;
    }

    private static bool MatchesOutcome(SignalHistoryEntry item, string? outcomeFilter)
    {
        if (string.IsNullOrWhiteSpace(outcomeFilter))
            return true;

        return string.Equals(
            GetOutcomeLabel(item),
            outcomeFilter.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SignalHistoryEntry> ApplySorting(
        IReadOnlyList<SignalHistoryEntry> items,
        SignalHistorySortColumn sortBy,
        SignalHistorySortDirection sortDirection)
    {
        var descending = sortDirection == SignalHistorySortDirection.Desc;
        Func<SignalHistoryEntry, object?> selector = sortBy switch
        {
            SignalHistorySortColumn.Source => item => item.Signal.Source,
            SignalHistorySortColumn.Timeframe => item => Timeframe.ByNameOrDefault(item.Signal.Timeframe).Minutes,
            SignalHistorySortColumn.Direction => item => item.Signal.Direction.ToString(),
            SignalHistorySortColumn.Entry => item => item.Signal.EntryPrice,
            SignalHistorySortColumn.Tp => item => item.Signal.TpPrice,
            SignalHistorySortColumn.Sl => item => item.Signal.SlPrice,
            SignalHistorySortColumn.Score => item => item.Signal.ConfidenceScore,
            SignalHistorySortColumn.Outcome => item => GetOutcomeLabel(item),
            SignalHistorySortColumn.Pnl => item => item.Outcome?.PnlR ?? 0m,
            _ => item => item.Signal.SignalTimeUtc
        };

        return descending
            ? items.OrderByDescending(selector).ThenByDescending(item => item.Signal.SignalTimeUtc)
            : items.OrderBy(selector).ThenBy(item => item.Signal.SignalTimeUtc);
    }

    private static string GetOutcomeLabel(SignalHistoryEntry item)
    {
        var pendingLabel = item.Signal.SourceType == SignalExecutionSourceType.Recommended
            ? "OPEN"
            : "PENDING";
        var executionLabel = item.Execution?.Status switch
        {
            ExecutedTradeStatus.Win => "WIN",
            ExecutedTradeStatus.Loss => "LOSS",
            ExecutedTradeStatus.Closed => "CLOSED",
            ExecutedTradeStatus.Failed or ExecutedTradeStatus.Rejected
                or ExecutedTradeStatus.ValidationFailed or ExecutedTradeStatus.CloseFailed => "FAILED",
            ExecutedTradeStatus.Open => "OPEN",
            ExecutedTradeStatus.CloseRequested => "CLOSE REQUESTED",
            ExecutedTradeStatus.Queued or ExecutedTradeStatus.Pending or ExecutedTradeStatus.Submitted => pendingLabel,
            _ => null
        };

        if (executionLabel != null)
            return executionLabel;

        if (item.Outcome != null)
            return item.Outcome.OutcomeLabel.ToString();

        if (item.Signal.SourceType == SignalExecutionSourceType.Recommended)
            return item.Signal.Status?.ToString() ?? "OPEN";

        return "PENDING";
    }

    private static SignalHistoryEntry MapRecommended(SignalWithOutcome item) => new()
    {
        Signal = new SignalHistorySignal
        {
            SignalId = item.Signal.SignalId,
            EvaluationId = item.Signal.EvaluationId,
            SourceType = SignalExecutionSourceType.Recommended,
            Source = SignalExecutionSourceType.Recommended.ToString(),
            Symbol = item.Signal.Symbol,
            Timeframe = item.Signal.Timeframe,
            SignalTimeUtc = item.Signal.SignalTimeUtc,
            Direction = item.Signal.Direction,
            Status = item.Signal.Status,
            Regime = item.Signal.Regime,
            StrategyVersion = item.Signal.StrategyVersion,
            Reasons = item.Signal.Reasons,
            EntryPrice = item.Signal.EntryPrice,
            TpPrice = item.Signal.TpPrice,
            SlPrice = item.Signal.SlPrice,
            RiskPercent = item.Signal.RiskPercent,
            RiskUsd = item.Signal.RiskUsd,
            ConfidenceScore = item.Signal.ConfidenceScore,
            ExitModel = item.Signal.ExitModel,
            ExitExplanation = item.Signal.ExitExplanation
        },
        Outcome = item.Outcome
    };

    private static SignalHistoryEntry MapGenerated(GeneratedSignalWithOutcome item) => new()
    {
        Signal = new SignalHistorySignal
        {
            SignalId = item.Signal.SignalId,
            EvaluationId = item.Signal.EvaluationId,
            SourceType = SignalExecutionSourceType.Generated,
            Source = SignalExecutionSourceType.Generated.ToString(),
            Symbol = item.Signal.Symbol,
            Timeframe = item.Signal.Timeframe,
            SignalTimeUtc = item.Signal.SignalTimeUtc,
            DecisionTimeUtc = item.Signal.DecisionTimeUtc,
            BarTimeUtc = item.Signal.BarTimeUtc,
            Direction = item.Signal.Direction,
            LifecycleState = item.Signal.LifecycleState,
            Regime = item.Signal.Regime,
            StrategyVersion = item.Signal.StrategyVersion,
            Reasons = item.Signal.Reasons,
            EntryPrice = item.Signal.EntryPrice,
            TpPrice = item.Signal.TpPrice,
            SlPrice = item.Signal.SlPrice,
            RiskPercent = item.Signal.RiskPercent,
            RiskUsd = item.Signal.RiskUsd,
            ConfidenceScore = item.Signal.ConfidenceScore,
            ExitModel = item.Signal.ExitModel,
            ExitExplanation = item.Signal.ExitExplanation,
            ExpiryBars = item.Signal.ExpiryBars,
            ExpiryTimeUtc = item.Signal.ExpiryTimeUtc
        },
        Outcome = item.Outcome
    };

    private static SignalHistoryEntry MapBlocked(BlockedSignalWithOutcome item) => new()
    {
        Signal = new SignalHistorySignal
        {
            SignalId = item.Signal.SignalId,
            EvaluationId = item.Signal.EvaluationId,
            SourceType = SignalExecutionSourceType.Blocked,
            Source = SignalExecutionSourceType.Blocked.ToString(),
            Symbol = item.Signal.Symbol,
            Timeframe = item.Signal.Timeframe,
            SignalTimeUtc = item.Signal.SignalTimeUtc,
            DecisionTimeUtc = item.Signal.DecisionTimeUtc,
            BarTimeUtc = item.Signal.BarTimeUtc,
            Direction = item.Signal.Direction,
            LifecycleState = item.Signal.LifecycleState,
            Regime = item.Signal.Regime,
            StrategyVersion = item.Signal.StrategyVersion,
            Reasons = item.Signal.Reasons,
            EntryPrice = item.Signal.EntryPrice,
            TpPrice = item.Signal.TpPrice,
            SlPrice = item.Signal.SlPrice,
            RiskPercent = item.Signal.RiskPercent,
            RiskUsd = item.Signal.RiskUsd,
            ConfidenceScore = item.Signal.ConfidenceScore,
            BlockReason = item.Signal.BlockReason,
            ExitModel = item.Signal.ExitModel,
            ExitExplanation = item.Signal.ExitExplanation,
            ExpiryBars = item.Signal.ExpiryBars,
            ExpiryTimeUtc = item.Signal.ExpiryTimeUtc
        },
        Outcome = item.Outcome
    };

    private static TimeZoneInfo ResolveAmmanTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private Task<GeneratedSignalHistoryPage?> LoadGeneratedAsync(SignalHistoryQuery query, CancellationToken ct)
        => IncludesSource(query, SignalExecutionSourceType.Generated)
            ? LoadGeneratedInternalAsync(query, ct)
            : Task.FromResult<GeneratedSignalHistoryPage?>(null);

    private async Task<GeneratedSignalHistoryPage?> LoadGeneratedInternalAsync(
        SignalHistoryQuery query,
        CancellationToken ct)
        => await _generatedHistory.GetHistoryAsync(query.Symbol, int.MaxValue, 0, hours: null, ct);

    private Task<BlockedSignalHistoryPage?> LoadBlockedAsync(SignalHistoryQuery query, CancellationToken ct)
        => IncludesSource(query, SignalExecutionSourceType.Blocked)
            ? LoadBlockedInternalAsync(query, ct)
            : Task.FromResult<BlockedSignalHistoryPage?>(null);

    private async Task<BlockedSignalHistoryPage?> LoadBlockedInternalAsync(
        SignalHistoryQuery query,
        CancellationToken ct)
        => await _blockedHistory.GetHistoryAsync(query.Symbol, int.MaxValue, 0, ct);
}
