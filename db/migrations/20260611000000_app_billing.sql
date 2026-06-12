-- App-owned billing persistence (schema "app" — never extension-owned "laplace").
-- Backs the endpoint's billing stores when LAPLACE_BILLING_STORE=postgres; the
-- in-memory implementations remain the test/dev default.

CREATE SCHEMA IF NOT EXISTS app;

CREATE TABLE IF NOT EXISTS app.billing_quotes (
    quote_id            text PRIMARY KEY,
    tenant              text NOT NULL,
    service_id          text NOT NULL,
    units               int NOT NULL,
    amount_cents        bigint NOT NULL,
    currency            text NOT NULL,
    status              text NOT NULL,
    consumed            boolean NOT NULL DEFAULT false,
    stripe_session_id   text,
    stripe_checkout_url text,
    created_at          timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS billing_quotes_tenant_created
    ON app.billing_quotes (tenant, created_at DESC);

CREATE TABLE IF NOT EXISTS app.billing_usage (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    quote_id     text NOT NULL,
    tenant       text NOT NULL,
    service_id   text NOT NULL,
    units        int NOT NULL,
    amount_cents bigint NOT NULL,
    executed_at  timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS billing_usage_tenant_executed
    ON app.billing_usage (tenant, executed_at DESC);

-- monthly_credits/used_credits are per-service maps (service_id -> int), mirroring
-- BillingEntitlement's dictionaries; credit consumption mutates used_credits atomically.
CREATE TABLE IF NOT EXISTS app.billing_entitlements (
    tenant                 text NOT NULL,
    plan_id                text NOT NULL,
    status                 text NOT NULL,
    period_start           timestamptz NOT NULL,
    period_end             timestamptz NOT NULL,
    monthly_credits        jsonb NOT NULL DEFAULT '{}'::jsonb,
    used_credits           jsonb NOT NULL DEFAULT '{}'::jsonb,
    stripe_customer_id     text,
    stripe_subscription_id text,
    updated_at             timestamptz NOT NULL,
    PRIMARY KEY (tenant, plan_id)
);
CREATE INDEX IF NOT EXISTS billing_entitlements_subscription
    ON app.billing_entitlements (stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS app.billing_webhook_events (
    event_id    text PRIMARY KEY,
    status      text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.stripe_price_map (
    lookup_key      text PRIMARY KEY,
    stripe_price_id text NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now()
);
