CREATE SCHEMA IF NOT EXISTS app;

-- =============================================================================
-- 1. Tenants, Users, Identities, Memberships, Auth Clients & Sessions
-- =============================================================================

CREATE TABLE IF NOT EXISTS app.tenants (
    tenant_id    text PRIMARY KEY,
    display_name text NOT NULL,
    kind         text NOT NULL CHECK (kind IN ('personal', 'organization')),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.users (
    user_id      uuid PRIMARY KEY,
    display_name text,
    email        text,
    avatar_url   text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.external_identities (
    identity_id   uuid PRIMARY KEY,
    user_id       uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    provider      text NOT NULL,
    issuer        text NOT NULL,
    subject       text NOT NULL,
    email         text,
    linked_at     timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (provider, issuer, subject)
);
CREATE INDEX IF NOT EXISTS external_identities_user
    ON app.external_identities (user_id);

CREATE TABLE IF NOT EXISTS app.tenant_memberships (
    tenant_id  text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    user_id    uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    role       text NOT NULL CHECK (role IN ('owner', 'admin', 'member')),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, user_id)
);
CREATE INDEX IF NOT EXISTS tenant_memberships_user
    ON app.tenant_memberships (user_id, created_at);

CREATE TABLE IF NOT EXISTS app.auth_clients (
    provider   text PRIMARY KEY,
    client_id  text NOT NULL,
    authority  text NOT NULL,
    enabled    boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.web_sessions (
    session_id   text PRIMARY KEY,
    user_id      uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    tenant_id    text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    ticket       bytea NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    revoked_at   timestamptz
);
CREATE INDEX IF NOT EXISTS web_sessions_user_active
    ON app.web_sessions (user_id, expires_at DESC)
    WHERE revoked_at IS NULL;

CREATE OR REPLACE FUNCTION app.preserve_web_session_revocation()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, app
AS $function$
BEGIN
    IF NEW.user_id IS DISTINCT FROM OLD.user_id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id THEN
        RAISE EXCEPTION 'Browser session identity is immutable' USING ERRCODE = '23514';
    END IF;
    IF OLD.revoked_at IS NOT NULL THEN NEW.revoked_at := OLD.revoked_at; END IF;
    RETURN NEW;
END;
$function$;
DROP TRIGGER IF EXISTS preserve_web_session_revocation ON app.web_sessions;
CREATE TRIGGER preserve_web_session_revocation
    BEFORE UPDATE ON app.web_sessions
    FOR EACH ROW EXECUTE FUNCTION app.preserve_web_session_revocation();

CREATE OR REPLACE FUNCTION app.check_browser_ticket_identity()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, app
AS $function$
DECLARE
    bound_user uuid;
    bound_tenant text;
BEGIN
    SELECT user_id, tenant_id INTO bound_user, bound_tenant
    FROM app.web_sessions WHERE session_id = NEW.session_id FOR UPDATE;
    IF FOUND AND (bound_user IS DISTINCT FROM NEW.user_id
                  OR bound_tenant IS DISTINCT FROM NEW.tenant_id) THEN
        RAISE EXCEPTION 'A browser session cannot be rebound to another identity; sign out before signing in again'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$function$;
DROP TRIGGER IF EXISTS check_browser_ticket_identity ON app.web_sessions;
CREATE TRIGGER check_browser_ticket_identity
    BEFORE INSERT ON app.web_sessions
    FOR EACH ROW EXECUTE FUNCTION app.check_browser_ticket_identity();

CREATE TABLE IF NOT EXISTS app.conversation_sessions (
    tenant_id    text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    session_key  text NOT NULL,
    user_id      uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    title        text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_turn_at timestamptz NOT NULL DEFAULT now(),
    archived_at  timestamptz,
    PRIMARY KEY (tenant_id, session_key)
);
CREATE INDEX IF NOT EXISTS conversation_sessions_user_recent
    ON app.conversation_sessions (user_id, last_turn_at DESC)
    WHERE archived_at IS NULL;

CREATE TABLE IF NOT EXISTS app.workspace_invitations (
    invitation_id uuid PRIMARY KEY,
    tenant_id     text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    token_hash    bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
    role          text NOT NULL CHECK (role IN ('admin', 'member')),
    created_by    uuid NOT NULL REFERENCES app.users(user_id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    revoked_at    timestamptz,
    accepted_at   timestamptz,
    accepted_by   uuid REFERENCES app.users(user_id),
    CHECK (expires_at > created_at),
    CHECK ((accepted_at IS NULL) = (accepted_by IS NULL))
);
CREATE INDEX IF NOT EXISTS workspace_invitations_active
    ON app.workspace_invitations (tenant_id, expires_at)
    WHERE accepted_at IS NULL AND revoked_at IS NULL;

-- =============================================================================
-- 2. Platform Access & Role Audit Events
-- =============================================================================

CREATE TABLE IF NOT EXISTS app.platform_roles (
    user_id    uuid PRIMARY KEY REFERENCES app.users(user_id) ON DELETE CASCADE,
    role       text NOT NULL CHECK (role IN ('administrator', 'operator', 'observer')),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.platform_role_events (
    event_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id       uuid NOT NULL,
    actor         text NOT NULL,
    previous_role text,
    role          text,
    occurred_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.workspace_role_events (
    event_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    user_id       uuid NOT NULL,
    actor_id      uuid NOT NULL,
    previous_role text NOT NULL,
    role          text NOT NULL,
    occurred_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS workspace_role_events_tenant_idx
    ON app.workspace_role_events(tenant_id, event_id DESC);

-- =============================================================================
-- 3. Billing, Metered Usage, Mold-A-Model Royalties & API Keys
-- =============================================================================

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
    expires_at          timestamptz NOT NULL,
    CONSTRAINT billing_quotes_tenant_fk FOREIGN KEY (tenant)
        REFERENCES app.tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS billing_quotes_tenant_created
    ON app.billing_quotes (tenant, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS billing_quotes_quote_tenant_unique
    ON app.billing_quotes (quote_id, tenant);

CREATE TABLE IF NOT EXISTS app.billing_usage (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    quote_id     text NOT NULL,
    tenant       text NOT NULL,
    service_id   text NOT NULL,
    units        int NOT NULL,
    amount_cents bigint NOT NULL,
    executed_at  timestamptz NOT NULL,
    CONSTRAINT billing_usage_tenant_fk FOREIGN KEY (tenant)
        REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    CONSTRAINT billing_usage_quote_tenant_fk FOREIGN KEY (quote_id, tenant)
        REFERENCES app.billing_quotes(quote_id, tenant) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS billing_usage_tenant_executed
    ON app.billing_usage (tenant, executed_at DESC);

-- Two-sided "BitTorrent of AI" / Mold-A-Model micro-royalty attribution ledger:
-- records exact basis-point (share_bp, 10000 = 100%) contributor splits per metered quote.
CREATE TABLE IF NOT EXISTS app.billing_royalties (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    quote_id           text NOT NULL,
    payer_tenant       text NOT NULL,
    source_id          text NOT NULL,
    service_id         text NOT NULL,
    share_bp           int NOT NULL CHECK (share_bp > 0 AND share_bp <= 10000),
    gross_amount_cents bigint NOT NULL CHECK (gross_amount_cents >= 0),
    royalty_cents      bigint NOT NULL CHECK (royalty_cents >= 0),
    executed_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT billing_royalties_payer_tenant_fk FOREIGN KEY (payer_tenant)
        REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    CONSTRAINT billing_royalties_quote_tenant_fk FOREIGN KEY (quote_id, payer_tenant)
        REFERENCES app.billing_quotes(quote_id, tenant) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS billing_royalties_payer_executed
    ON app.billing_royalties (payer_tenant, executed_at DESC);
CREATE INDEX IF NOT EXISTS billing_royalties_source_executed
    ON app.billing_royalties (source_id, executed_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS billing_royalties_quote_source
    ON app.billing_royalties (quote_id, source_id);

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
    provider_event_created bigint NOT NULL DEFAULT 0,
    cancel_at_period_end   boolean NOT NULL DEFAULT false,
    PRIMARY KEY (tenant, plan_id),
    CONSTRAINT billing_entitlements_tenant_fk FOREIGN KEY (tenant)
        REFERENCES app.tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS billing_entitlements_subscription
    ON app.billing_entitlements (stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS billing_entitlements_subscription_unique
    ON app.billing_entitlements (stripe_subscription_id)
    WHERE stripe_subscription_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS app.billing_customers (
    stripe_customer_id text PRIMARY KEY,
    tenant             text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

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

CREATE OR REPLACE FUNCTION app.apply_subscription(
    p_tenant text, p_plan text, p_status text,
    p_start timestamptz, p_end timestamptz, p_credits jsonb,
    p_customer text, p_subscription text, p_event_created bigint,
    p_cancel_at_period_end boolean)
RETURNS SETOF app.billing_entitlements
LANGUAGE plpgsql
SET search_path = pg_catalog, app
AS $function$
DECLARE
    current_row app.billing_entitlements%ROWTYPE;
    result_row app.billing_entitlements%ROWTYPE;
    previous_used jsonb;
    subscription_id text := NULLIF(p_subscription, '');
BEGIN
    IF p_tenant IS NULL OR btrim(p_tenant) = '' OR p_plan IS NULL OR btrim(p_plan) = ''
       OR p_end <= p_start OR p_start IS NULL OR p_end IS NULL
       OR p_event_created IS NULL OR p_event_created < 0
       OR p_status NOT IN ('active', 'trialing', 'past_due', 'unpaid', 'incomplete',
                          'incomplete_expired', 'canceled', 'paused')
       OR p_status IS NULL OR jsonb_typeof(p_credits) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'Invalid subscription state' USING ERRCODE = '22023';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('laplace/billing/' || p_tenant, 0));
    PERFORM 1 FROM app.billing_entitlements WHERE tenant = p_tenant FOR UPDATE;
    IF subscription_id IS NOT NULL AND EXISTS (
        SELECT 1 FROM app.billing_entitlements
        WHERE stripe_subscription_id = subscription_id AND tenant <> p_tenant) THEN
        RAISE EXCEPTION 'Subscription is already bound to another tenant' USING ERRCODE = '23514';
    END IF;

    SELECT e.* INTO current_row FROM app.billing_entitlements e
    WHERE e.tenant = p_tenant AND (e.plan_id = p_plan OR e.stripe_subscription_id = subscription_id)
    ORDER BY e.provider_event_created DESC, e.updated_at DESC, e.plan_id
    LIMIT 1;
    IF current_row.tenant IS NOT NULL THEN
        IF current_row.provider_event_created > p_event_created
           OR (current_row.stripe_subscription_id = subscription_id
               AND current_row.status = 'canceled' AND p_status <> 'canceled') THEN
            RETURN NEXT current_row;
            RETURN;
        END IF;
        subscription_id := COALESCE(subscription_id, current_row.stripe_subscription_id);
    END IF;

    SELECT e.used_credits INTO previous_used FROM app.billing_entitlements e
    WHERE e.tenant = p_tenant
      AND e.stripe_subscription_id IS NOT DISTINCT FROM subscription_id
      AND e.period_start = p_start
    ORDER BY e.provider_event_created DESC, e.updated_at DESC, e.plan_id
    LIMIT 1;

    UPDATE app.billing_entitlements
    SET status = 'replaced', provider_event_created = p_event_created, updated_at = now()
    WHERE tenant = p_tenant AND stripe_subscription_id = subscription_id AND plan_id <> p_plan;

    INSERT INTO app.billing_entitlements
        (tenant, plan_id, status, period_start, period_end, monthly_credits, used_credits,
         stripe_customer_id, stripe_subscription_id, updated_at,
         provider_event_created, cancel_at_period_end)
    VALUES (p_tenant, p_plan, p_status, p_start, p_end, p_credits,
            COALESCE(previous_used, '{}'::jsonb),
            COALESCE(p_customer, current_row.stripe_customer_id), subscription_id, now(),
            p_event_created, COALESCE(p_cancel_at_period_end, false))
    ON CONFLICT (tenant, plan_id) DO UPDATE SET
        status = EXCLUDED.status,
        period_start = EXCLUDED.period_start,
        period_end = EXCLUDED.period_end,
        monthly_credits = EXCLUDED.monthly_credits,
        used_credits = EXCLUDED.used_credits,
        stripe_customer_id = EXCLUDED.stripe_customer_id,
        stripe_subscription_id = EXCLUDED.stripe_subscription_id,
        updated_at = EXCLUDED.updated_at,
        provider_event_created = EXCLUDED.provider_event_created,
        cancel_at_period_end = EXCLUDED.cancel_at_period_end
    RETURNING * INTO result_row;
    RETURN NEXT result_row;
END;
$function$;

CREATE TABLE IF NOT EXISTS app.billing_webhook_events (
    event_id    text PRIMARY KEY,
    status      text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.stripe_price_map (
    lookup_key      text PRIMARY KEY,
    stripe_price_id text NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.api_keys (
    key_hash     text PRIMARY KEY,
    key_prefix   text NOT NULL,
    tenant       text NOT NULL,
    label        text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    revoked_at   timestamptz,
    last_used_at timestamptz,
    CONSTRAINT api_keys_tenant_fk FOREIGN KEY (tenant)
        REFERENCES app.tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS api_keys_tenant
    ON app.api_keys (tenant, created_at DESC);

CREATE TABLE IF NOT EXISTS app.billing_config (
    key        text PRIMARY KEY,
    value      text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION app.consume_credit(
        p_tenant text,
        p_service text,
        p_units int)
    RETURNS TABLE(plan_id text, remaining int, period_end timestamptz)
    LANGUAGE sql
AS $$
    WITH candidate AS (
        SELECT e.tenant, e.plan_id,
               COALESCE((e.monthly_credits->>p_service)::int, 0) AS credit_limit,
               COALESCE((e.used_credits->>p_service)::int, 0) AS used,
               e.period_end
        FROM app.billing_entitlements e
        WHERE e.tenant = p_tenant
          AND e.status = 'active'
          AND e.period_end > now()
          AND COALESCE((e.monthly_credits->>p_service)::int, 0)
              - COALESCE((e.used_credits->>p_service)::int, 0) >= p_units
        ORDER BY COALESCE((e.monthly_credits->>p_service)::int, 0) DESC
        LIMIT 1
        FOR UPDATE
    )
    UPDATE app.billing_entitlements e
    SET used_credits = jsonb_set(
            e.used_credits,
            ARRAY[p_service],
            to_jsonb(c.used + p_units)),
        updated_at = now()
    FROM candidate c
    WHERE e.tenant = c.tenant AND e.plan_id = c.plan_id
    RETURNING e.plan_id,
              (c.credit_limit - c.used - p_units)::int,
              e.period_end;
$$;

-- =============================================================================
-- 4. Seeded Tenants & Default Platform Administrators
-- =============================================================================

INSERT INTO app.tenants (tenant_id, display_name, kind)
VALUES
    ('local-dev', 'Local development workspace', 'organization'),
    ('deployment-verification', 'Installation verification', 'organization')
ON CONFLICT (tenant_id) DO NOTHING;

DO $$
DECLARE
    rec RECORD;
    v_user_id uuid;
    v_tenant_id text;
    v_prev_role text;
BEGIN
    LOCK TABLE app.platform_roles IN SHARE ROW EXCLUSIVE MODE;

    FOR rec IN
        SELECT * FROM (VALUES
            (
                '51c8d4fc-a658-4907-b16a-1878e73b9190'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844101'::uuid,
                't-51c8d4fca6584907b16a1878e73b9190',
                'google',
                'https://accounts.google.com',
                '105368356205536670205',
                'Anthony Hart',
                'aharttn@gmail.com',
                '2026-10-15T18:00:50.389934Z'::timestamptz
            ),
            (
                'c3fbe492-4748-46e6-adfe-52fda500e882'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844102'::uuid,
                't-c3fbe492474846e6adfe52fda500e882',
                'google',
                'https://accounts.google.com',
                '105862061897402259594',
                'Salty Patron',
                'thesaltypatron@gmail.com',
                '2026-10-15T18:00:57.895750Z'::timestamptz
            ),
            (
                '48cf86ec-4db0-4bf4-9264-4fcf3b09482d'::uuid,
                '993b7b67-e2a1-4b2b-9f16-5fb3f4844103'::uuid,
                't-48cf86ec4db04bf492644fcf3b09482d',
                'microsoft',
                'https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0',
                'AAAAAAAAAAAAAAAAAAAAANrDmzXa_OckY6Ame0a1110',
                'Salty Patron',
                'thesaltypatron@gmail.com',
                '2026-10-15T21:07:08.745445Z'::timestamptz
            )
        ) AS t(default_user_id, default_identity_id, default_tenant_id, provider, issuer, subject, display_name, email, verified_at)
    LOOP
        SELECT user_id INTO v_user_id
        FROM app.external_identities
        WHERE provider = rec.provider
          AND issuer = rec.issuer
          AND subject = rec.subject;

        IF v_user_id IS NULL THEN
            v_user_id := rec.default_user_id;
            INSERT INTO app.users (user_id, display_name, email)
            VALUES (v_user_id, rec.display_name, rec.email)
            ON CONFLICT (user_id) DO NOTHING;

            INSERT INTO app.external_identities (identity_id, user_id, provider, issuer, subject, email, last_login_at)
            VALUES (rec.default_identity_id, v_user_id, rec.provider, rec.issuer, rec.subject, rec.email, rec.verified_at)
            ON CONFLICT (provider, issuer, subject) DO NOTHING;

            SELECT user_id INTO v_user_id
            FROM app.external_identities
            WHERE provider = rec.provider
              AND issuer = rec.issuer
              AND subject = rec.subject;
        END IF;

        IF NOT EXISTS (
            SELECT 1
            FROM app.tenant_memberships m
            JOIN app.tenants t USING (tenant_id)
            WHERE m.user_id = v_user_id
              AND t.kind = 'personal'
        ) THEN
            v_tenant_id := rec.default_tenant_id;
            INSERT INTO app.tenants (tenant_id, display_name, kind)
            VALUES (v_tenant_id, rec.display_name || '''s workspace', 'personal')
            ON CONFLICT (tenant_id) DO NOTHING;

            INSERT INTO app.tenant_memberships (tenant_id, user_id, role)
            VALUES (v_tenant_id, v_user_id, 'owner')
            ON CONFLICT (tenant_id, user_id) DO NOTHING;
        END IF;

        SELECT role INTO v_prev_role
        FROM app.platform_roles
        WHERE user_id = v_user_id;

        IF v_prev_role IS DISTINCT FROM 'administrator' THEN
            INSERT INTO app.platform_roles (user_id, role, updated_at)
            VALUES (v_user_id, 'administrator', now())
            ON CONFLICT (user_id) DO UPDATE
                SET role = EXCLUDED.role,
                    updated_at = now();

            INSERT INTO app.platform_role_events (user_id, actor, previous_role, role)
            VALUES (v_user_id, 'installation-owner', v_prev_role, 'administrator');
        END IF;
    END LOOP;
END $$;

-- =============================================================================
-- 5. Application Schema Grants
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA app TO laplace_app';
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA app TO laplace_app';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO laplace_app';
        EXECUTE 'GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA app TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT USAGE, SELECT ON SEQUENCES TO laplace_app';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT EXECUTE ON FUNCTIONS TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT USAGE ON SCHEMA app TO laplace_readonly';
        EXECUTE 'GRANT SELECT ON ALL TABLES IN SCHEMA app TO laplace_readonly';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT SELECT ON TABLES TO laplace_readonly';
        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA app
                 GRANT USAGE, SELECT ON SEQUENCES TO laplace_readonly';
    END IF;
END $$;
