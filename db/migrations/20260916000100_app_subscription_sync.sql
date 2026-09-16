ALTER TABLE app.billing_entitlements
    ADD COLUMN IF NOT EXISTS provider_event_created bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS cancel_at_period_end boolean NOT NULL DEFAULT false;
ALTER TABLE app.billing_webhook_events
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

-- This is an account/payment projection, not a replacement for measured native
-- execution billing. Existing allowance data survives events in the same period.
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
    -- Lock the actual allowance rows as well, so a concurrent usage debit cannot
    -- be overwritten by a same-period provider refresh.
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

-- A stale sliding-cookie renewal must not resurrect a session revoked by a
-- member removal or another device. Session identity is immutable; switching
-- workspace creates a new session instead of retagging the previous ticket.
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
