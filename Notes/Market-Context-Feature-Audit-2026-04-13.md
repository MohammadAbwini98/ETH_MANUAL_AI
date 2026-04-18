# Market-Context Feature Audit — 2026-04-13

## Scope

Reviewed the attached implementation plan and implementation report against the current repository state, with focus on:

- market-context feature extraction and runtime wiring
- training/export feature-version alignment
- dashboard visibility of the new features
- ML Data Quality workflow and why it was showing sparse or misleading output

## Executive Summary

The feature expansion was only partially implemented end to end.

What was already implemented correctly:

- the expanded market-context fields exist in `MlFeatureVector`
- `MlFeatureExtractor` contains the added calculations and uses `FeatureVersion = "v2.0"`
- Python training scripts include the expanded feature-name lists
- extractor tests were updated for the expanded feature set
- BTC and derivatives scaffolding files exist

What was not actually wired correctly before this audit:

- the live runtime path did not pass recent signal history into `MlFeatureExtractor`, so signal-saturation features stayed zero in runtime snapshots
- the live runtime path did not inject or pass any BTC context provider, so BTC scaffolding existed but was not actually wired through the live inference path
- `ml/train_pipeline.sh` still hardcoded `--feature-version v1.0`, so the practical training workflow continued exporting legacy rows and training legacy models
- the dashboard had no endpoint or UI section that rendered the latest ML feature snapshot, so the new market-context features were not visible there
- ML diagnostics always queried the current extractor version (`v2.0`) even when the actual usable training workflow and active models were still on older feature data, which can make the panel look empty, sparse, or misleading

## Findings Against The Attached Report

### Implemented as reported

- category E market-structure features are present in C# and Python
- category F volatility-regime features are present in C# and Python
- category H BTC context is scaffolded rather than backed by real BTC ingestion
- float-array ordering and Python feature-name ordering were expanded consistently
- feature extractor tests cover the expanded feature vector and helper calculations

### Report overstated completion

1. Signal-saturation features were not truly active in live runtime use.

Reason:
`LiveTickProcessor.RunMlInferenceAsync()` called `MlFeatureExtractor.Extract(...)` without `recentSignals`, so the extractor defaulted those features to zero.

2. BTC context was scaffolded but not fully wired in runtime.

Reason:
`IBtcContextProvider` existed, but there was no DI registration and no provider call in the live inference path.

3. The practical training pipeline was not upgraded to v2.0.

Reason:
`ml/export_features.py` defaulted to `v2.0`, but `ml/train_pipeline.sh` overrode it with `--feature-version v1.0`.
This is the most important contract mismatch found in the audit.

4. Dashboard visibility of the new market-context features was not implemented.

Reason:
The dashboard only exposed ML health, diagnostics, and predictions. There was no endpoint or UI panel for latest feature snapshots.

## Why The New Market-Context Features Were Not Showing On The Dashboard

Primary cause:
there was no dashboard feature-snapshot view at all.

Secondary cause:
even if the dashboard had shown model metadata, the actual training workflow was still producing legacy models because `train_pipeline.sh` was exporting `v1.0` rows.

Practical effect:

- runtime snapshots could contain newer fields, but the dashboard had no place to display them
- newly trained models in `ml/models/*_meta.json` were still on the older feature contract
- this made it look like the feature expansion was absent, even though parts of it existed in code

## Why ML Data Quality Looked Low / Critical / Empty

The biggest workflow problem was feature-version misalignment.

Before the fix:

- runtime extractor version was `v2.0`
- diagnostics always queried `v2.0`
- training pipeline still exported `v1.0`
- active/recent models were still legacy-contract models

That combination can produce:

- weak or empty drift comparisons
- low labeled/trainable counts for the diagnostics version being inspected
- dashboard notes that do not explain the real reason clearly
- apparent emptiness even when older usable ML rows exist

A second issue was that signal-saturation features were staying zero at runtime because recent signals were not being passed into extraction.

## Fixes Applied In This Audit

### 1. Live signal-saturation wiring fixed

Updated the live inference path so `LiveTickProcessor` now fetches recent same-timeframe signals in a causal time window and passes them into `MlFeatureExtractor`.

Files:

- `src/EthSignal.Infrastructure/Db/ISignalRepository.cs`
- `src/EthSignal.Infrastructure/Db/SignalRepository.cs`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`

### 2. BTC context wiring completed to the scaffolded level

Registered the null BTC provider in DI and passed BTC context into the extractor from the live runtime path.

Files:

- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- `src/EthSignal.Web/Program.cs`

Note:
This does not create real BTC data. It just makes the scaffolding actually participate safely in runtime.

### 3. Training pipeline feature-version mismatch fixed

Changed the practical training workflow to export the current expanded feature version by default.

File:

- `ml/train_pipeline.sh`

Change:

- before: hardcoded `--feature-version v1.0`
- after: `FEATURE_VERSION="${ML_FEATURE_VERSION:-v2.0}"`

Impact:
new training runs will export the expanded contract unless explicitly overridden.

### 4. ML diagnostics made feature-version aware

Changed diagnostics so they no longer blindly assume the current extractor version is the only usable version.

Behavior now:

- prefer current feature version when it has enough usable data
- otherwise fall back to the best available feature version with actual trainable/labeled coverage
- expose the selected feature version and whether fallback was used
- expose model feature-count vs current feature-count so legacy-model mismatch is visible

Files:

- `src/EthSignal.Infrastructure/Db/IMlDataDiagnosticsRepository.cs`
- `src/EthSignal.Infrastructure/Db/MlDataDiagnosticsRepository.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MlDataDiagnosticsModels.cs`
- `src/EthSignal.Infrastructure/Engine/ML/MlDataDiagnosticsService.cs`
- `src/EthSignal.Web/Program.cs`

### 5. Dashboard visibility for market-context features added

Added a new dashboard API endpoint and UI section that shows the latest persisted market-context snapshot, grouped into:

- market structure
- volatility regime
- signal saturation
- BTC context

Files:

- `src/EthSignal.Web/Program.cs`
- `src/EthSignal.Web/wwwroot/index.html`
- `src/EthSignal.Web/wwwroot/js/dashboard.js`

Endpoint added:

- `/api/ml/features/latest`

The dashboard now also shows:

- snapshot feature version
- link status
- timestamp
- whether the snapshot appears to be on the current full feature contract

### 6. Diagnostics messaging improved

The dashboard note builder now explains two important hidden states:

- when diagnostics are using a fallback feature version
- when the active model still uses a legacy feature contract and therefore cannot use the new market-context fields yet

## Validation Performed

### Editor / compile validation

The modified files reported no editor errors after changes.

### Tests run

1. `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --filter MlFeatureExtractorTests --no-restore`

Result:

- passed
- 44 tests succeeded
- 0 failed

2. `dotnet test tests/EthSignal.Tests/EthSignal.Tests.csproj --filter ApiEndpointTests --no-restore`

Result:

- passed
- 21 tests succeeded
- 0 failed

### Environment limitation during audit

The local ETH web service on port `5234` was not running during the audit, and `psql` was not available in the shell. Because of that, the live dashboard payload and live database row counts could not be inspected directly during this session.

## Remaining Gaps / Follow-up

1. Existing trained ONNX models are still legacy-feature models until you retrain.

The code is now aligned for v2 training, but the existing model artifacts in `ml/models/*_meta.json` still show old feature counts.

2. BTC features remain zero until real BTC ingestion is implemented.

The runtime path is now wired to a provider, but the registered implementation is still the null provider.

3. Derivatives context is still scaffold only.

No real funding/open-interest data source was found or wired.

4. Dashboard visibility depends on running the ETH web service again.

The new API and UI changes are in code, but you will not see them until the service is restarted.

## Recommended Next Actions

1. Restart the ETH web service so the new dashboard endpoint and UI are loaded.
2. Run `ml/train_pipeline.sh` again so new exports/models use the v2 feature contract by default.
3. Confirm the dashboard’s new “Latest Market Context” panel is populated from fresh runtime snapshots.
4. After a fresh v2 model is trained and activated, re-check ML Data Quality to confirm the legacy-contract warning disappears.
