using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// B-07: Runtime parameter provider with in-memory cache and DB-backed source of truth.
/// Thread-safe. Falls back to last-known-good or defaults if DB load fails.
/// T3-14: Auto-refreshes from DB after CacheTtl (default 5 min) so parameter changes
/// in the DB are picked up without a restart. The background refresh is fire-and-forget
/// — GetActive() always returns immediately with the last-known-good value.
/// Portal overrides (ETH.portal_overrides) are applied on top of the active parameter
/// set so operator-controlled blocker values persist across optimizer runs.
/// </summary>
public sealed class ParameterProvider : IParameterProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IParameterRepository _repo;
    private readonly IPortalOverridesRepository? _overridesRepo;
    private readonly string _strategyVersion;
    private readonly ILogger<ParameterProvider> _logger;
    private volatile StrategyParameters _cached;
    private readonly object _lock = new();
    private DateTimeOffset _lastRefreshed = DateTimeOffset.MinValue;
    private volatile bool _refreshInFlight;

    public ParameterProvider(IParameterRepository repo, string? strategyVersion = null,
        ILogger<ParameterProvider>? logger = null,
        IPortalOverridesRepository? overridesRepo = null)
    {
        _repo = repo;
        _overridesRepo = overridesRepo;
        _strategyVersion = strategyVersion ?? StrategyParameters.Default.StrategyVersion;
        _logger = logger ?? NullLogger<ParameterProvider>.Instance;
        _cached = StrategyParameters.Default;
    }

    public StrategyParameters GetActive()
    {
        // If the cache is stale and no refresh is already in flight, fire a background refresh.
        // Return the current cached value immediately so callers are never blocked.
        if (!_refreshInFlight && DateTimeOffset.UtcNow - _lastRefreshed > CacheTtl)
        {
            _refreshInFlight = true;
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync(); }
                finally { _refreshInFlight = false; }
            });
        }
        return _cached;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var active = await _repo.GetActiveAsync(_strategyVersion, ct);
            if (active is not null)
            {
                // Apply portal overrides on top of the active parameter set.
                // This lets operator-set blocker values survive optimizer runs.
                PortalOverrides? overrides = null;
                if (_overridesRepo != null)
                {
                    try { overrides = await _overridesRepo.GetAsync(ct); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load portal overrides, using base params");
                    }
                }

                var parameters = ApplyPortalOverrides(active.Parameters, overrides);
                var normalizedParameters = parameters.EnsureProductionSafeDefaults();
                if (!string.Equals(normalizedParameters.ToJson(), parameters.ToJson(), StringComparison.Ordinal))
                {
                    parameters = normalizedParameters;
                    _logger.LogWarning(
                        "Active parameter set id={Id} required runtime normalization (legacy defaults repaired)",
                        active.Id);
                }
                lock (_lock)
                {
                    _cached = parameters;
                    _lastRefreshed = DateTimeOffset.UtcNow;
                }
                _logger.LogInformation(
                    "Refreshed active parameter set id={Id} version={Version} portalOverridesApplied={Applied}",
                    active.Id, active.StrategyVersion, overrides != null);
                return true;
            }

            _logger.LogDebug("No active parameter set found for {Version}, using current cache",
                _strategyVersion);
            lock (_lock) { _lastRefreshed = DateTimeOffset.UtcNow; }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh parameters, keeping last-known-good");
            return false;
        }
    }

    private static StrategyParameters ApplyPortalOverrides(StrategyParameters p, PortalOverrides? o)
    {
        if (o == null) return p;
        return p with
        {
            MaxOpenPositions = o.MaxOpenPositions ?? p.MaxOpenPositions,
            MaxOpenPerTimeframe = o.MaxOpenPerTimeframe ?? p.MaxOpenPerTimeframe,
            MaxOpenPerDirection = o.MaxOpenPerDirection ?? p.MaxOpenPerDirection,
            DailyLossCapPercent = o.DailyLossCapPercent ?? p.DailyLossCapPercent,
            MaxConsecutiveLossesPerDay = o.MaxConsecutiveLossesPerDay ?? p.MaxConsecutiveLossesPerDay,
            ScalpMaxConsecutiveLossesPerDay = o.ScalpMaxConsecutiveLossesPerDay ?? p.ScalpMaxConsecutiveLossesPerDay
        };
    }

    public void ForceOverrideMlMode(MlMode mode)
    {
        lock (_lock)
        {
            _cached = _cached with { MlMode = mode };
        }
    }
}
