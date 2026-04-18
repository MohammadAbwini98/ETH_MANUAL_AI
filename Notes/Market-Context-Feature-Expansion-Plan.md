# Market-Context Feature Expansion Plan

You are Claude Code, working inside the `ETH_MANUAL` repository.

Your task is to implement a market-context feature expansion for the ML pipeline, with a focus on features that are most likely to improve prediction accuracy for ETH signal quality.

## Primary goal
Add high-value market-context features end-to-end across feature extraction, training/export, runtime inference compatibility, and tests.

## Important context
This project already includes some context features in `MlFeatureExtractor.cs`, including:
- regime label and regime score
- timeframe
- hour of day / session
- recent win rate / streaks
- ATR and volume z-scores
- regime changes and pullback depth

## The next useful market-context feature families

### Microstructure
- order-book imbalance
- bid/ask depth ratio
- spread widening rate
- short-term trade aggression
- slippage estimate

### Derivatives Context
- funding rate
- open interest level and change
- long/short ratio
- liquidation clusters
- basis between spot and perp

### Volatility Regime
- realized volatility over 15m, 1h, 4h
- volatility compression/expansion flags
- ATR percentile versus recent history
- breakout-failure frequency

### Cross-Asset Context
- BTC return / BTC regime
- ETH/BTC relative strength
- DXY or Nasdaq proxy regime if available
- correlation-to-BTC rolling window

### Event / Time Context
- day-of-month / week-of-month
- weekend vs weekday
- session transition windows
- pre/post major economic events
- exchange maintenance or rollover windows

### Recent Market Structure
- distance to prior day high/low
- distance to session VWAP
- distance to weekly high/low
- breakout above/below recent range
- range position percentile

### Signal Saturation / Crowd State
- number of recent signals in last N bars
- same-direction signals in last N bars
- recent false-breakout count
- recent stop-out cluster count

## Priority for this repo
Implement the highest-value subset first, in this order:
1. session range position and distance to prior day high/low
2. realized volatility percentile and volatility expansion/compression
3. BTC-led context like BTC return/regime and ETH/BTC relative strength
4. funding rate and open-interest change
5. signal saturation / recent false-breakout features

## Execution rules
- Start by inspecting the repo and confirming what data sources already exist.
- Do not invent external data that the repo cannot currently access.
- If a feature family depends on data not currently available, add clean optional scaffolding/interfaces/config for it, but do not fake the data.
- Preserve backward compatibility for existing models and old rows where possible.
- Keep the implementation safe for live runtime use.
- Prefer a coherent vertical slice over partially wiring many unfinished features.
- Run targeted tests/checks after changes and summarize results.

## Implementation requirements

### 1. Inspect and map the current ML feature pipeline
Review and understand the current end-to-end path:
- `MlFeatureExtractor.cs`
- `MlFeatureVector.cs`
- training feature name lists in Python
- export pipeline
- prediction/inference compatibility
- feature persistence and diagnostics

Confirm:
- where feature names are defined canonically
- how feature arrays are serialized to runtime inference
- how missing features are handled
- what candle/timeframe/history data is already available
- whether BTC or derivative context already exists in the repo

### 2. Implement the highest-value features that can be supported now
Add the following first-class features where feasible from existing project data:

#### A. Recent market structure / session context
- session range position percentile
- distance to prior day high
- distance to prior day low
- distance to session VWAP if session VWAP can be derived safely from existing candles
- optionally distance to rolling recent range high/low if prior-day/session data is incomplete

Use causal calculations only from data available at evaluation time.

#### B. Volatility regime
- realized volatility over recent 15m / 1h / 4h windows, using available candle history
- volatility expansion/compression flags
- ATR percentile versus recent rolling history
- optionally realized-vol ratio short-vs-long window

Keep definitions simple, causal, and stable.

#### C. Signal saturation / crowd-state
- signals in last N bars
- same-direction signals in last N bars
- opposite-direction signals in last N bars
- recent stop-out count in last N resolved signals
- recent false-breakout proxy if directly measurable from outcomes or signal history

Use existing signal/outcome repositories where possible.

### 3. Conditionally implement BTC-led context if supported
Inspect whether BTC candle/context ingestion already exists.
If it does:
- add BTC recent return
- BTC regime or proxy trend state
- ETH relative strength vs BTC
- rolling ETH/BTC divergence or correlation-style feature if feasible

If it does not:
- do not fabricate BTC data
- add a clean extension point or interface so BTC context can be added later without refactoring the feature pipeline
- keep the feature pipeline working without BTC inputs

### 4. Conditionally scaffold derivatives context if not already present
Inspect whether funding rate, open interest, or similar data already exists.
If these sources already exist in the repo, wire them properly.
If they do not:
- add a clear, optional provider abstraction/config path for future derivatives context
- do not block current ML flow on missing derivatives data
- do not create fake constants or dummy live values
- keep defaults safe and explicit

### 5. Update feature contracts consistently across C# and Python
Any feature added must be wired consistently through:
- `MlFeatureExtractor`
- `MlFeatureVector`
- feature-name ordering / float-array conversion
- Python `FEATURE_NAMES` lists
- export/training compatibility
- model metadata / diagnostics where relevant

Requirements:
- preserve deterministic feature ordering
- handle missing/legacy rows safely
- avoid breaking old training exports
- ensure inference still works if new models are not yet trained

### 6. Prefer causal, auditable feature definitions
For each added feature:
- use only information available at evaluation time
- avoid leakage from future candles or future outcomes
- keep the calculation understandable and auditable
- favor robust simple definitions over clever but brittle ones

### 7. Testing and verification
Add or update tests for:
- new feature extraction outputs
- feature vector ordering / serialization
- safe behavior when optional external context is unavailable
- signal saturation calculations
- volatility/session structure calculations
- training/export path compatibility with new features

Likely test locations:
- `tests/EthSignal.Tests/Engine/ML/*`
- any relevant infrastructure/repository tests

### 8. Documentation and rollout safety
Update relevant docs/comments where helpful so the new features are understandable.
If needed, add brief notes on:
- which new features are active now
- which are scaffolded for future data sources
- any config flags or assumptions

## Acceptance criteria
- The repo includes a meaningful market-context feature expansion implemented end-to-end.
- At minimum, the highest-value currently supportable features are added:
  - session range / prior-day structure features
  - volatility regime features
  - signal saturation / recent crowd-state features
- BTC-led features are implemented only if the repo has real BTC data support; otherwise clean scaffolding is added.
- Derivatives context is wired only if real data exists; otherwise clean optional scaffolding is added.
- C# and Python feature contracts remain aligned.
- Existing runtime inference remains safe and backward compatible.
- Relevant tests/checks pass.

## Suggested files to inspect first
- `src/EthSignal.Infrastructure/Engine/ML/MlFeatureExtractor.cs`
- `src/EthSignal.Domain/Models/MlFeatureVector.cs`
- `ml/train_outcome_predictor.py`
- `ml/train_recalibrator.py`
- `ml/export_features.py`
- `src/EthSignal.Infrastructure/Engine/LiveTickProcessor.cs`
- relevant repositories under `src/EthSignal.Infrastructure/Db/`
