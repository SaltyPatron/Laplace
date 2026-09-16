-- Company onboarding uses the existing identity and tenant tables. Possession
-- of an email domain is never implicit membership in a customer's workspace.
CREATE TABLE IF NOT EXISTS app.workspace_invitations (
    invitation_id uuid PRIMARY KEY,
    tenant_id text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
    role text NOT NULL CHECK (role IN ('admin', 'member')),
    created_by uuid NOT NULL REFERENCES app.users(user_id),
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    accepted_at timestamptz,
    accepted_by uuid REFERENCES app.users(user_id),
    CHECK (expires_at > created_at),
    CHECK ((accepted_at IS NULL) = (accepted_by IS NULL))
);
CREATE INDEX IF NOT EXISTS workspace_invitations_active
    ON app.workspace_invitations (tenant_id, expires_at)
    WHERE accepted_at IS NULL AND revoked_at IS NULL;
