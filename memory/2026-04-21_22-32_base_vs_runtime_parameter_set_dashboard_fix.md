# Title
base vs runtime parameter set dashboard fix

## Date
2026-04-21 22:32:15 +03

## User Request
Implement the dashboard/API fix so the active-parameter-set display no longer implies that one global activation timestamp is the live per-timeframe setup, while being careful about dependencies, especially signal generation.

## Scope Reviewed
- `memory/2026-04-21_22-20_active_parameter_set_timestamp_investigation.md`
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `src/EthSignal.Infrastructure/Engine/ParameterProvider.cs`
- `src/EthSignal.Infrastructure/Engine/SignalEngine.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`

## Key Findings
- Signal generation still depends on `IParameterProvider.GetActive()` plus `ResolveForTimeframe(...)`; it does not depend on the dashboard/admin endpoint.
- The misleading timestamp came from the global base parameter-set activation row, not from per-timeframe adaptive runtime state.
- The safest fix was to add a new read-only admin overview endpoint rather than changing the existing `/api/admin/parameter-sets/active` contract that other admin consumers already use.

## Root Cause
- The dashboard card was using the legacy global parameter-set endpoint and labeling it as if it represented the current live per-timeframe setup.
- Adaptive per-timeframe runtime state was already available through the adaptive service but had not been merged into the parameter-set overview.

## Files Reviewed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`
- `src/EthSignal.Infrastructure/Engine/ParameterProvider.cs`
- `src/EthSignal.Infrastructure/Engine/SignalEngine.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`

## Files Changed
- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`
- `tests/EthSignal.Tests/Web/ApiEndpointTests.cs`

## Implementation / Outcome
- Added `GET /api/admin/parameter-sets/overview` that returns:
  - the unchanged global base active parameter set
  - base activation time
  - current per-timeframe adaptive/runtime setups
  - latest adaptive change timestamp/timeframe
  - adaptive enablement and current primary adaptive condition
- Kept `/api/admin/parameter-sets/active` unchanged to avoid collateral contract breakage.
- Updated the dashboard card label from `Active Parameter Set` to `Base Active Parameter Set`.
- Added dashboard fields for:
  - `Base Activated`
  - `Latest TF Change`
  - `TF Setups`
- Added a compact `Current Timeframe Runtime Setups` table to the parameter-set card so operators can see live per-timeframe effective setups in the same area where they were previously reading only the global timestamp.
- Left signal-generation/runtime resolution unchanged:
  - base parameters still come from `IParameterProvider.GetActive()`
  - timeframe-specific behavior still comes from `ResolveForTimeframe(...)`
  - adaptive overlays still come from `MarketAdaptiveParameterService`

## Verification
- `dotnet build ETH_MANUAL.sln`
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --no-build --filter "FullyQualifiedName~ApiEndpointTests.Parameter_Set_Overview_Returns_Base_Set_And_Timeframe_Runtime_Setups|FullyQualifiedName~ApiEndpointTests.Adaptive_Status_Returns_Timeframe_Profiles_And_Recent_Changes|FullyQualifiedName~ApiEndpointTests.Admin_Global_Config_Returns_Recommended_Executor_Flag"`
- `dotnet test ETH_MANUAL.sln --no-build`
- Result: `441` passed, `0` failed.

## Risks / Notes
- This fixes the contract/display mismatch but does not yet convert `strategy_parameter_sets` into true per-timeframe activation rows. The runtime still uses one base set plus per-timeframe profile/adaptive overlays.
- The new dashboard view duplicates some adaptive information intentionally so the parameter-set area is no longer misleading.

## Current State
- The dashboard now distinguishes the global base parameter set from the live per-timeframe runtime setups.
- Operators can see the latest timeframe change without assuming the global base set is being re-activated on every adaptive update.
- Signal generation behavior remains unchanged and dependency-safe.

## Next Recommended Step
- If you want true per-timeframe persisted base activations, the next change should introduce a dedicated timeframe-aware parameter-set layer in the repository/provider path, then migrate signal generation to resolve the active base set by timeframe before adaptive overlays are applied.
