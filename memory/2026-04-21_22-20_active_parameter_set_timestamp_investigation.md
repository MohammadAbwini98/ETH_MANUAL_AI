# Title
active parameter set timestamp investigation

## Date
2026-04-21 22:20:00 +03

## User Request
Explain why the dashboard shows the last Active Parameter Set activation at `04/21, 08:47 PM` when it should update frequently and each timeframe should have its own Active Parameter Set.

## Scope Reviewed
- `memory/2026-04-21_22-13_adaptive_timeframe_profiles_dashboard_history.md`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Domain/Models/StrategyParameterSet.cs`
- `src/EthSignal.Infrastructure/Engine/ParameterProvider.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`

## Key Findings
- The dashboard card labeled `Active Parameter Set` still reads from the legacy/global parameter-set endpoint.
- That endpoint returns a single active row from `ETH.strategy_parameter_sets` for the active strategy version.
- `activated_utc` only changes when `ActivateAsync(...)` is called, such as manual activation, startup seeding, or similar promotion flow.
- Adaptive per-timeframe changes are stored separately in adaptive tables and do not update `strategy_parameter_sets.activated_utc`.
- Current system behavior is:
  - one global base active parameter set
  - per-timeframe resolved/adaptive active runtime setups
- So the timestamp the user is seeing is the base-set activation time, not the most recent per-timeframe adaptive change.

## Root Cause
- `GET /api/admin/parameter-sets/active` is wired to `IParameterRepository.GetActiveAsync(...)`, which selects one global `Active` parameter set row for the strategy version.
- The dashboard binds the `Activated` field directly to `activatedUtc` from that global row.
- Per-timeframe adaptive evolution was implemented in `MarketAdaptiveParameterService`, but the `Active Parameter Set` card was not redefined to use those per-timeframe runtime states.

## Files Reviewed
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Domain/Models/StrategyParameterSet.cs`
- `src/EthSignal.Infrastructure/Engine/ParameterProvider.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MarketAdaptiveParameterService.cs`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`

## Files Changed
- none

## Implementation / Outcome
- No code changed in this investigation step.
- Confirmed the dashboard is showing the last activation time of the global base parameter set, not the latest per-timeframe adaptive setup change.
- Confirmed that each timeframe currently has its own adaptive effective setup/state, but not its own row in `strategy_parameter_sets`.

## Verification
- Code-path review only.
- No build or test run in this investigation step.

## Risks / Notes
- The current UI wording is misleading because `Active Parameter Set` sounds like the live per-timeframe effective setup, while the underlying endpoint still represents only the base global parameter set.
- If the intended behavior is truly “each timeframe has its own Active Parameter Set,” that is still not fully modeled in the parameter-set layer; current implementation isolates per-timeframe adaptive runtime state instead.

## Current State
- Base parameter activation is global and infrequent.
- Adaptive per-timeframe setup changes can happen frequently and independently.
- The dashboard currently exposes both concepts in different sections, but the global parameter-set card can still be misread as the live adaptive state.

## Next Recommended Step
- Redefine the dashboard/API contract so the existing parameter-set card is explicitly a `Base Active Parameter Set`, and add/merge a per-timeframe `Current Active Setups` view sourced from adaptive timeframe state.
