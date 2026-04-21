# Title
runtime schema guard and higher tf exit fix

## Date
2026-04-21 23:23 +03

## User Request
Fix the runtime DB/schema path first, then rework the pricing/exit path so higher timeframes stop collapsing under `structure too close` rejections.

## Scope Reviewed
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `src/EthSignal.Infrastructure/Engine/StructureAnalyzer.cs`
- `tests/EthSignal.Tests/Infrastructure/ParameterRepositoryTests.cs`
- `tests/EthSignal.Tests/Infrastructure/SignalRepositoryTests.cs`
- `tests/EthSignal.Tests/Engine/ExitEngineTests.cs`
- prior memory files for schema drift, pricing realism, and adaptive/runtime parameter behavior

## Key Findings
- The repository already had the migration logic for the missing `signals` columns, but the runtime path was still fragile because repository calls assumed startup migration had already run successfully.
- The visible `evaluation_id` insert failures in the log were therefore best fixed by making critical repositories self-heal the runtime DB through the existing `DbMigrator`.
- The higher-timeframe exit collapse was not only a structure-vs-RR issue. `ExitEngine` also claimed to clamp ATR TP distances to the ATR band, but the code was not actually clamping the initial TP distance before structure handling.
- `StructureAnalyzer` only exposed nearest/second structure levels, which made it hard for the exit engine to step past a micro-resistance/support level and use the next meaningful structure target.

## Root Cause
- Runtime schema drift: core repository operations relied on process startup migration and had no repository-level guard when the runtime database lagged behind the expected schema.
- Pricing/exit collapse: `ExitEngine` was treating a very near structure target as a hard blocker for higher timeframes instead of trying the next meaningful structure level or falling back to a volatility-realistic ATR TP band.

## Files Reviewed
- `src/EthSignal.Infrastructure/Db/DbMigrator.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `src/EthSignal.Infrastructure/Engine/StructureAnalyzer.cs`
- `tests/EthSignal.Tests/Engine/ExitEngineTests.cs`
- `tests/EthSignal.Tests/Infrastructure/ParameterRepositoryTests.cs`
- `tests/EthSignal.Tests/Infrastructure/SignalRepositoryTests.cs`

## Files Changed
- `src/EthSignal.Infrastructure/Db/RuntimeDbSchemaGuard.cs`
- `src/EthSignal.Infrastructure/Db/ParameterRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/StructureAnalyzer.cs`
- `src/EthSignal.Infrastructure/Engine/ExitEngine.cs`
- `tests/EthSignal.Tests/Engine/ExitEngineTests.cs`
- `tests/EthSignal.Tests/Infrastructure/ParameterRepositoryTests.cs`
- `tests/EthSignal.Tests/Infrastructure/SignalRepositoryTests.cs`

## Implementation / Outcome
- Added `RuntimeDbSchemaGuard` so repository calls can invoke the existing `DbMigrator` once per runtime/connection-string before critical DB usage.
- Wired that guard into `ParameterRepository` and `SignalRepository`, covering the parameter-set and signal persistence/read paths that were failing in the live log.
- Extended `StructureAnalyzer.StructureLevels` to carry full support/resistance zone lists and updated `FindStructureTarget(...)` to support a minimum-distance requirement.
- Reworked `ExitEngine.Compute(...)` so:
  - ATR TP distances are clamped to the timeframe-aware ATR band,
  - higher timeframes can try the next meaningful structure target when the nearest one is only micro-structure,
  - higher timeframes can fall back to the ATR TP band when no meaningful alternate structure exists,
  - `1m` scalp behavior remains strict and still rejects unrealistic micro-structure targets.
- Added regression tests for:
  - repository self-healing against stale/missing runtime schema,
  - next-structure selection,
  - ATR fallback on higher timeframes,
  - continued scalp rejection when no realistic target exists,
  - ATR TP capping.

## Verification
- `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --filter "FullyQualifiedName~SignalRepositoryTests|FullyQualifiedName~ParameterRepositoryTests|FullyQualifiedName~ExitEngineTests|FullyQualifiedName~StrategyParametersTests"`
- `dotnet test ETH_MANUAL.sln --no-restore`
- Final result: `447` passed, `0` failed.

## Risks / Notes
- The old active parameter-set row created by legacy adaptive persistence was not auto-retired in this step because changing the live baseline parameter set automatically would be too risky.
- The new repository schema guard fixes runtime drift on fresh app execution, but an already-running old process still needs to be restarted to pick up the new code.
- Backtest/historical replay paths still deserve a separate parity pass if they need to exactly mirror the new exit behavior under all conditions.

## Current State
- Runtime repository calls now proactively ensure the expected DB schema exists through the existing migrator.
- Higher-timeframe signals are less likely to collapse just because the nearest detected structure level is a tiny local pivot.
- TP distances are now kept inside the intended ATR-based realism band before final R:R enforcement.

## Next Recommended Step
- Restart the application against the target runtime database so the new schema guard can execute on that environment.
- Then monitor fresh `5m/15m/30m` logs to confirm `structure too close` rejections drop materially and that accepted signals persist cleanly without `evaluation_id` column errors.
