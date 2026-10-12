-- Platform authority is explicitly assigned to a local identity. A workspace
-- owner or an external provider's directory role grants no platform authority.
CREATE TABLE IF NOT EXISTS app.platform_roles (
    user_id uuid PRIMARY KEY REFERENCES app.users(user_id) ON DELETE CASCADE,
    role text NOT NULL CHECK (role IN ('administrator', 'operator', 'observer')),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.platform_role_events (
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id uuid NOT NULL,
    actor text NOT NULL,
    previous_role text,
    role text,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS app.workspace_role_events (
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    previous_role text NOT NULL,
    role text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS workspace_role_events_tenant_idx ON app.workspace_role_events(tenant_id, event_id DESC);
