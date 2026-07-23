CREATE SCHEMA IF NOT EXISTS app;

-- API keys: the customer-facing credential. Only the SHA-256 hash is stored;
-- the plaintext key is shown once at issuance and never persisted.
CREATE TABLE IF NOT EXISTS app.api_keys (
    key_hash     text PRIMARY KEY,
    key_prefix   text NOT NULL,
    tenant       text NOT NULL,
    label        text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    revoked_at   timestamptz,
    last_used_at timestamptz
);
CREATE INDEX IF NOT EXISTS api_keys_tenant
    ON app.api_keys (tenant, created_at DESC);

-- Small key/value store for billing runtime state the app provisions itself
-- (e.g. the Stripe webhook endpoint id + signing secret created at bootstrap).
CREATE TABLE IF NOT EXISTS app.billing_config (
    key        text PRIMARY KEY,
    value      text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
