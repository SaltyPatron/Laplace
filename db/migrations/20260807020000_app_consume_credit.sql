-- app.consume_credit — money-path debit as one installed function (GH #531).
-- Was inline FOR UPDATE + jsonb_set CTE in PostgresBillingEntitlementStore.
-- Behaviour unchanged: pick the active entitlement with the most headroom for
-- the service, lock it, bump used_credits, return (plan_id, remaining, period_end)
-- or zero rows when insufficient / inactive / unknown service.

CREATE SCHEMA IF NOT EXISTS app;

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
