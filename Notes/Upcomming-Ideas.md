-Remove Gold From project.[DONE]
-Live Decisioning Signal & Strategy Monitor.
-Update button in portal for updating the dashboard stop then start.  [DONE]

-Live Decisioning Signal, add section on dashboard and present a live signal tracking for it's lifecycle if there were more than one show at the same time live tracking for each signal.

-adjust the TP and SL boundries to be more realistc. suggest multiple TP levels. [IN-PORFRESS].

-ML Predictions are low. 


-Add instrument live price beside tab title.

-Add contolller in portal to control the playwright chrome mode (silent/visible). [check all places or configurations for the require behaviour].

-Add section in the portal in order to set the below signal blockers values as integer (number) or unlimited (by default), consider backend and BE to corresspoding parameter:

MaxOpenPositions.
MaxOpenPerTimeframe.
MaxOpenPerDirection.
DailyLossCapPercent.
MaxConsecutiveLossesPerDay.
ScalpMaxConsecutiveLossesPerDay.




-------------------
BEGIN;

LOCK TABLE
  "ETH".candles_1h,
  "ETH".candles_1m,
  "ETH".candles_4h,
  "ETH".candles_5m,
  "ETH".candles_15m,
  "ETH".candles_30m,
  "ETH".gap_events,
  "ETH".indicator_snapshots,
  "ETH".ingestion_audit,
  "ETH".ml_drift_events,
  "ETH".ml_feature_snapshots,
  "ETH".ml_predictions,
  "ETH".ml_training_runs,
  "ETH".optimizer_candidate_folds,
  "ETH".optimizer_candidates,
  "ETH".optimizer_runs,
  "ETH".parameter_activation_history,
  "ETH".regime_snapshots,
  "ETH".replay_runs,
  "ETH".signal_decision_audit,
  "ETH".signal_features,
  "ETH".signal_outcomes,
  "ETH".signals,
  "ETH".strategy_parameter_sets,
  "ETH".ui_tick_samples
IN ACCESS EXCLUSIVE MODE;

TRUNCATE TABLE
  "ETH".candles_1h,
  "ETH".candles_1m,
  "ETH".candles_4h,
  "ETH".candles_5m,
  "ETH".candles_15m,
  "ETH".candles_30m,
  "ETH".gap_events,
  "ETH".indicator_snapshots,
  "ETH".ingestion_audit,
  "ETH".ml_drift_events,
  "ETH".ml_feature_snapshots,
  "ETH".ml_predictions,
  "ETH".ml_training_runs,
  "ETH".optimizer_candidate_folds,
  "ETH".optimizer_candidates,
  "ETH".optimizer_runs,
  "ETH".parameter_activation_history,
  "ETH".regime_snapshots,
  "ETH".replay_runs,
  "ETH".signal_decision_audit,
  "ETH".signal_features,
  "ETH".signal_outcomes,
  "ETH".signals,
  "ETH".strategy_parameter_sets,
  "ETH".ui_tick_samples
RESTART IDENTITY CASCADE;

COMMIT;