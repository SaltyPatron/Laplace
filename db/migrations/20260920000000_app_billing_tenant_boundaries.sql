-- Billing and credentials are workspace-owned state. These tables predate the
-- durable identity catalog, so bind them to real Laplace tenants now.
DO $migration$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'billing_quotes_tenant_fk'
                   AND conrelid = 'app.billing_quotes'::regclass) THEN
        ALTER TABLE app.billing_quotes
            ADD CONSTRAINT billing_quotes_tenant_fk FOREIGN KEY (tenant)
            REFERENCES app.tenants(tenant_id) ON DELETE CASCADE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'billing_usage_tenant_fk'
                   AND conrelid = 'app.billing_usage'::regclass) THEN
        ALTER TABLE app.billing_usage
            ADD CONSTRAINT billing_usage_tenant_fk FOREIGN KEY (tenant)
            REFERENCES app.tenants(tenant_id) ON DELETE CASCADE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'billing_entitlements_tenant_fk'
                   AND conrelid = 'app.billing_entitlements'::regclass) THEN
        ALTER TABLE app.billing_entitlements
            ADD CONSTRAINT billing_entitlements_tenant_fk FOREIGN KEY (tenant)
            REFERENCES app.tenants(tenant_id) ON DELETE CASCADE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'api_keys_tenant_fk'
                   AND conrelid = 'app.api_keys'::regclass) THEN
        ALTER TABLE app.api_keys
            ADD CONSTRAINT api_keys_tenant_fk FOREIGN KEY (tenant)
            REFERENCES app.tenants(tenant_id) ON DELETE CASCADE;
    END IF;
END;
$migration$;

-- Usage is a child of the exact quote in the same workspace; a quote id from
-- another workspace can never be written into its ledger.
CREATE UNIQUE INDEX IF NOT EXISTS billing_quotes_quote_tenant_unique
    ON app.billing_quotes (quote_id, tenant);
DO $migration$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'billing_usage_quote_tenant_fk'
                   AND conrelid = 'app.billing_usage'::regclass) THEN
        ALTER TABLE app.billing_usage
            ADD CONSTRAINT billing_usage_quote_tenant_fk FOREIGN KEY (quote_id, tenant)
            REFERENCES app.billing_quotes(quote_id, tenant) ON DELETE CASCADE;
    END IF;
END;
$migration$;

-- A Stripe subscription is one provider object and cannot authorize two
-- workspaces, including under concurrent webhook delivery.
CREATE UNIQUE INDEX IF NOT EXISTS billing_entitlements_subscription_unique
    ON app.billing_entitlements (stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;

-- Stripe customer identity is also a workspace boundary. Several plan rows may
-- legitimately reference one customer, so keep the one-to-one binding here.
CREATE TABLE IF NOT EXISTS app.billing_customers (
    stripe_customer_id text PRIMARY KEY,
    tenant              text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1 FROM app.billing_entitlements
        WHERE stripe_customer_id IS NOT NULL
        GROUP BY stripe_customer_id HAVING count(DISTINCT tenant) > 1
    ) THEN
        RAISE EXCEPTION 'A Stripe customer is bound to more than one Laplace tenant';
    END IF;
END;
$migration$;

INSERT INTO app.billing_customers (stripe_customer_id, tenant)
SELECT stripe_customer_id, min(tenant)
FROM app.billing_entitlements
WHERE stripe_customer_id IS NOT NULL
GROUP BY stripe_customer_id
ON CONFLICT (stripe_customer_id) DO NOTHING;

CREATE OR REPLACE FUNCTION app.bind_billing_customer()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, app
AS $function$
DECLARE
    bound_tenant text;
BEGIN
    IF NEW.stripe_customer_id IS NULL THEN RETURN NEW; END IF;
    INSERT INTO app.billing_customers (stripe_customer_id, tenant)
    VALUES (NEW.stripe_customer_id, NEW.tenant)
    ON CONFLICT (stripe_customer_id) DO UPDATE SET updated_at = now()
    WHERE billing_customers.tenant = EXCLUDED.tenant
    RETURNING tenant INTO bound_tenant;
    IF bound_tenant IS NULL OR bound_tenant <> NEW.tenant THEN
        RAISE EXCEPTION 'Stripe customer is already bound to another tenant'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS bind_billing_customer ON app.billing_entitlements;
CREATE TRIGGER bind_billing_customer
    BEFORE INSERT OR UPDATE OF tenant, stripe_customer_id
    ON app.billing_entitlements
    FOR EACH ROW EXECUTE FUNCTION app.bind_billing_customer();
