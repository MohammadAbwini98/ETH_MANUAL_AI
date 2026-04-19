-- Regime-specific sub-models migration
-- Adds regime_scope to ml_models so each trained model can be scoped
-- to a specific market regime (BEARISH / BULLISH / NEUTRAL) or the
-- global fallback (ALL).  The C# inference layer loads all four slots
-- and routes inference by the current regime label.

ALTER TABLE "ETH".ml_models
    ADD COLUMN IF NOT EXISTS regime_scope VARCHAR(10) NOT NULL DEFAULT 'ALL';

-- Index: active model lookup per type + scope (used on every bar close)
CREATE INDEX IF NOT EXISTS idx_ml_models_type_scope_status
    ON "ETH".ml_models (model_type, regime_scope, status)
    WHERE LOWER(status) = 'active';

-- Verify
SELECT column_name, data_type, column_default
FROM information_schema.columns
WHERE table_schema = 'ETH' AND table_name = 'ml_models' AND column_name = 'regime_scope';
