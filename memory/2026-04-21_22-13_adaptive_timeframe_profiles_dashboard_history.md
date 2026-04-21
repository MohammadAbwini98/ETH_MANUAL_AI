# Title
adaptive timeframe profiles dashboard history

## Date
2026-04-21 22:13:46 +03

## User Request
Implement adaptive strategy so each timeframe has its own dynamic profile and dynamic parameters, changes independently without overriding other timeframes, persists each setup change in database tables, and surfaces the latest per-timeframe setup plus change history on the dashboard.

## Scope Reviewed
- `memory/2026-04-21_21-19_phase1_stabilization_and_timeframe_profile_foundation.md`
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/TimeframeStrategyProfiles.cs`
- `src/EthSignal.Domain/Models/AdaptiveTimeframeProfiles.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Infrastructure/Db/IAdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/AdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Engine/StrategyParametersTests.cs`
- `tests/EthSignal.Tests/Engine/ML/AdaptiveAuditFixTests.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Key Findings
- The existing adaptive service still tracked runtime state too globally for cross-timeframe evolution. Outcome windows and retrospective overlays needed timeframe scoping to avoid 1m tuning bleeding into 1h/4h.
- The phase-3 foundation already had bucketed timeframe profiles, but exact timeframe overrides and adaptive runtime persistence were not fully surfaced end-to-end.
- Dashboard adaptive status did not previously expose active per-timeframe setups or recent setup changes.
- Recent adaptive changes were only being queued when the DB-backed adaptive state repository was present, which left the change-history panel empty in non-persistent/test runtimes.

## Root Cause
- Adaptive runtime state was originally designed around shared condition buckets and summary metrics instead of independent per-timeframe profile instances.
- Exact timeframe profile resolution and append-only adaptive profile change history were not previously modeled as first-class persisted entities.
- Dashboard contract only returned high-level adaptive summaries, not the currently active timeframe-specific effective setup.

## Files Reviewed
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/TimeframeStrategyProfiles.cs`
- `src/EthSignal.Domain/Models/AdaptiveTimeframeProfiles.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Infrastructure/Db/IAdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/AdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Engine/StrategyParametersTests.cs`
- `tests/EthSignal.Tests/Engine/ML/AdaptiveAuditFixTests.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Files Changed
- `src/EthSignal.Domain/Models/StrategyParameters.cs`
- `src/EthSignal.Domain/Models/TimeframeStrategyProfiles.cs`
- `src/EthSignal.Domain/Models/AdaptiveTimeframeProfiles.cs`
- `src/EthSignal.Infrastructure/Db/IAdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/AdaptiveStateRepository.cs`
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Engine/StrategyParametersTests.cs`
- `tests/EthSignal.Tests/Engine/ML/AdaptiveAuditFixTests.cs`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Implementation / Outcome
- Added exact timeframe profile support (`1m`, `5m`, `15m`, `30m`, `1h`, `4h`) layered over existing fast/mid/long bucket fallbacks.
- Added persisted adaptive profile state and append-only change history models for each symbol/timeframe pair.
- Extended adaptive state repository and DB migrator with `adaptive_timeframe_profile_states` and `adaptive_timeframe_profile_changes`.
- Refactored the adaptive service to:
  - scope outcome windows and retrospective overlays per timeframe
  - maintain independent runtime profile state per timeframe
  - persist current effective setup and each meaningful setup change
  - expose active per-timeframe setups and recent changes to the dashboard/API
  - keep recent in-memory change history even when DB persistence is unavailable
- Updated live outcome recording to preserve timeframe scoping when feeding adaptive retrospection.
- Extended dashboard adaptive section to show:
  - active timeframe setups
  - condition outcomes with timeframe
  - recent adaptive changes
- Added regression tests for exact timeframe profile precedence, timeframe-isolated adaptive runtime state, and adaptive admin API payload shape.

## Verification
- `dotnet build ETH_MANUAL.sln --no-restore`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --filter "FullyQualifiedName~StrategyParametersTests|FullyQualifiedName~AdaptiveAuditFixTests|FullyQualifiedName~ApiEndpointTests.Adaptive_Status_Returns_Timeframe_Profiles_And_Recent_Changes"`
- `dotnet test ETH_MANUAL.sln --no-build`
- Result: full suite passed, `440` tests passed, `0` failed.

## Risks / Notes
- Existing active parameter-set persistence is still one base set with embedded timeframe profiles; adaptive runtime state is intentionally stored separately to avoid destructive cross-timeframe activation churn.
- Dashboard now shows adaptive effective setups and change history, but there is still no dedicated admin editor for adjusting exact timeframe profiles interactively.
- The repository has other uncommitted changes from prior work; they were preserved and not reverted.

## Current State
- Each timeframe can resolve its own profile without overriding other timeframes.
- Adaptive runtime behavior and retrospective learning are isolated per timeframe and can run simultaneously.
- Each meaningful adaptive setup change is stored in DB-backed history and surfaced on the dashboard.
- The dashboard can show the latest active effective setup for each timeframe and the most recent setup transitions.

## Next Recommended Step
- Add an admin workflow for viewing and editing exact timeframe profile baselines so operators can tune base profiles intentionally while keeping adaptive runtime history separate.
