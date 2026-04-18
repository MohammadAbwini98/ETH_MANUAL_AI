what is heuristic-v1 model and ONNX models are they pre-defined models or created by this project ?



Worked for 20s
heuristic-v1 in this repo is a project-defined fallback, not a pretrained external model.

In this codebase, heuristic-v1 is created inside MlInferenceService.cs. It is a simple hand-written formula that uses a few existing features:

rule_based_score
adx14
rsi14
macd_hist
It turns those into an estimated win probability with a few bonuses/penalties. It exists so the app can still produce ML-style predictions in SHADOW mode when no real trained model is available.

The ONNX models are also created by this project, but in a different way:

train_outcome_predictor.py trains the actual model from your exported project data.
It uses LightGBM by default, with XGBoost fallback.
After training, it exports the trained model to .onnx.
The runtime then loads that .onnx file in MlInferenceService.cs.
So the short version is:

heuristic-v1 = hardcoded fallback logic written in this project.
.onnx model = trained by this project from your DB data, then exported in ONNX format.
ONNX itself is just the model file format/standard, not a predefined model.
There are also two related project-created artifacts:

recalibrator JSON from train_recalibrator.py
threshold lookup JSON from train_threshold_model.py
If you want, I can also show you the exact formula used by heuristic-v1 in plain English.


are there external models that may be more helpful than the current ?



Worked for 13s
Yes, but probably not in the way most people expect.

For this project, the most helpful “external” upgrade is usually not a pretrained finance model. With your current setup, better results are more likely to come from a stronger tabular model or better external features than from dropping in a generic pretrained model.

The best candidates to try are:

CatBoost
Often the best next model for small-to-medium noisy tabular datasets like yours. It usually handles mixed feature scales and nonlinear interactions very well.
XGBoost
Good option if your current LightGBM models are unstable. It can sometimes rank signals better under class imbalance.
Logistic Regression as a calibration baseline
Not stronger by itself, but very useful as a sanity-check model. If it performs close to your boosted trees, your feature quality is the bottleneck.
FT-Transformer or TabNet
Worth testing only if your clean labeled dataset becomes much larger. With a few hundred to a few thousand samples, they’re often worse than boosted trees.
Sequence models like TCN/LSTM/TFT
Only useful if you change the dataset to include bar sequences rather than one-row snapshots. That’s a bigger redesign.
What is usually less helpful here:

Generic pretrained “financial AI models”
LLMs for direct win/loss prediction
Large deep-learning models trained elsewhere on unrelated market data
For short-horizon ETH signal prediction, those usually do worse than a well-trained local tabular model.

What may help more than an external model:

external features, not external predictors
funding rate / open interest
liquidation data
order-book imbalance
macro calendar filters
crypto/news sentiment embeddings
My practical recommendation for this repo is:

Keep the current ONNX pipeline.
Add CatBoost as the first serious challenger to LightGBM.
Keep a logistic-regression baseline.
Only try transformer/sequence models after you have much cleaner and larger labeled data.
Prefer adding better market-context features before chasing exotic models.
If you want, I can write you a concrete comparison prompt for Claude/Codex to add CatBoost + logistic baseline + model bakeoff into this project.


what kind of market-context features ?



Market-context features are variables that describe the environment around a signal, not just the candle’s local indicators.

Your project already has some of these in MlFeatureExtractor.cs:

regime label and regime score
timeframe
hour of day / session
recent win rate / streaks
ATR and volume z-scores
regime changes and pullback depth
The next useful market-context features are usually these:

Microstructure

order-book imbalance
bid/ask depth ratio
spread widening rate
short-term trade aggression
slippage estimate
These help tell whether a setup is tradable or just looks good on indicators.

Derivatives Context

funding rate
open interest level and change
long/short ratio
liquidation clusters
basis between spot and perp
These are very useful for ETH because they show when futures positioning is crowded or unstable.

Volatility Regime

realized volatility over 15m, 1h, 4h
volatility compression/expansion flags
ATR percentile versus recent history
breakout-failure frequency
These help the model distinguish trend continuation from noisy chop.

Cross-Asset Context

BTC return / BTC regime
ETH/BTC relative strength
DXY or Nasdaq proxy regime if available
correlation-to-BTC rolling window
ETH often behaves differently depending on whether it is leading or lagging BTC.

Event / Time Context

day-of-month / week-of-month
weekend vs weekday
session transition windows
pre/post major economic events
exchange maintenance or rollover windows
A setup at London/NY overlap is not the same as the same setup in late Asia.

Recent Market Structure

distance to prior day high/low
distance to session VWAP
distance to weekly high/low
breakout above/below recent range
range position percentile
These are often stronger than raw EMA values.

Signal Saturation / Crowd State

number of recent signals in last N bars
same-direction signals in last N bars
recent false-breakout count
recent stop-out cluster count
This helps avoid taking the 4th weak continuation after the edge is already exhausted.

For this repo, the highest-value next additions are probably:

session range position and distance to prior day high/low
realized volatility percentile and volatility expansion/compression
BTC-led context like BTC return/regime and ETH/BTC relative strength
funding rate and open-interest change
signal saturation / recent false-breakout features
If you want, I can turn that into a concrete implementation prompt with exact feature definitions for this codebase.




check the attached plan and it's implementation and outcome report.
check if all mentioned functionalities and features were implemented as needed.
check why I'm not seeing the new market context features at the dashboard.
check the ML Data Quality functionality and configuration and if it's workflow working as expected because always I'm getting low and critical status also many values are empty.
put all observations in .md file.
take action and apply fixes if needed .