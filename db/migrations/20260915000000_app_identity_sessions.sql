CREATE SCHEMA IF NOT EXISTS app;

-- A Laplace tenant is an authorization and billing boundary. External provider
-- directories are evidence about an identity; they do not silently become a
-- Laplace tenant or grant their members access to one.
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

-- Provider subject is the durable external identity. Email is profile data and
-- may change or be absent, so it is deliberately not a key.
CREATE TABLE IF NOT EXISTS app.external_identities (
    identity_id uuid PRIMARY KEY,
    user_id     uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    provider    text NOT NULL,
    issuer      text NOT NULL,
    subject     text NOT NULL,
    email       text,
    linked_at   timestamptz NOT NULL DEFAULT now(),
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

-- Client secrets remain in the host secret store. This catalog records the
-- public client registration actually active for an installed Laplace service.
CREATE TABLE IF NOT EXISTS app.auth_clients (
    provider   text PRIMARY KEY,
    client_id  text NOT NULL,
    authority  text NOT NULL,
    enabled    boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- The browser cookie carries only an encrypted reference to this row. Revoking
-- the row invalidates the session without waiting for the cookie to expire.
CREATE TABLE IF NOT EXISTS app.web_sessions (
    session_id text PRIMARY KEY,
    user_id    uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    tenant_id  text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    ticket     bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz
);
CREATE INDEX IF NOT EXISTS web_sessions_user_active
    ON app.web_sessions (user_id, expires_at DESC)
    WHERE revoked_at IS NULL;

-- The substrate remains the turn/order authority. This table is the user-owned
-- catalog that makes those durable session handles discoverable after refresh or
-- sign-in on another device.
CREATE TABLE IF NOT EXISTS app.conversation_sessions (
    tenant_id   text NOT NULL REFERENCES app.tenants(tenant_id) ON DELETE CASCADE,
    session_key text NOT NULL,
    user_id     uuid NOT NULL REFERENCES app.users(user_id) ON DELETE CASCADE,
    title       text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    last_turn_at timestamptz NOT NULL DEFAULT now(),
    archived_at timestamptz,
    PRIMARY KEY (tenant_id, session_key)
);
CREATE INDEX IF NOT EXISTS conversation_sessions_user_recent
    ON app.conversation_sessions (user_id, last_turn_at DESC)
    WHERE archived_at IS NULL;
