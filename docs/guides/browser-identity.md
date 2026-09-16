# Browser identity, SSO, tenants and durable sessions

Laplace accepts Microsoft and Google accounts through OpenID Connect. Each first
login creates a durable user, external identity, personal tenant and owner
membership. The browser receives an HTTP-only secure cookie containing an
encrypted reference; the separately encrypted authentication ticket lives in
`app.web_sessions`, so logout or session revocation takes effect server-side.
Conversation handles are indexed per user in `app.conversation_sessions`; the
substrate remains the authority for the ordered turns.

## Provider registration is still required

Microsoft and Google publish public authorization, token and discovery endpoints,
but they do not provide an anonymous shared client identity for arbitrary apps.
Both providers require Laplace's deployment to register its redirect URI and
obtain a client ID. A server-side web application also receives a client secret.

Azure AD B2C is not required. Register a normal Microsoft identity-platform web
application with the supported account type **Accounts in any organizational
directory and personal Microsoft accounts**. Laplace defaults to the `common` v2
authority, which accepts that audience. Register this redirect URI exactly:

```text
https://YOUR_HOST/signin-microsoft
```

For Google, create a Web application OAuth client and register:

```text
https://YOUR_HOST/signin-google
```

The provider documentation is authoritative for registration details:

- [Microsoft identity platform authorization-code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
- [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)

## Runtime configuration

Put the selected provider pairs in `identity.env` (installed Linux path:
`/opt/laplace/secrets/identity.env`; Windows path:
`deploy/secrets/identity.env`):

```dotenv
LAPLACE_AUTH_MICROSOFT_CLIENT_ID=...
LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET=...
LAPLACE_AUTH_GOOGLE_CLIENT_ID=...
LAPLACE_AUTH_GOOGLE_CLIENT_SECRET=...
```

Either provider may be configured independently. Supplying only one half of a
pair is a startup error. Client secrets are never written to PostgreSQL;
`app.auth_clients` records only the active public client ID and authority.

Production uses:

```dotenv
LAPLACE_AUTH_MODE=identity
LAPLACE_DATA_PROTECTION_KEYS=/opt/laplace/secrets/data-protection
```

`identity` mode accepts a signed-in browser session or a Laplace API key and
rejects a caller-controlled tenant header. `header` remains the explicit local
development mode. The data-protection directory preserves cookie decryption keys
across deployments; losing it invalidates existing cookies even though their
server-side rows remain.

## Product and API surfaces

- `GET /v1/auth/config` lists configured providers.
- `GET /v1/auth/login/{microsoft|google}` starts OIDC authorization-code + PKCE.
- `GET /v1/auth/me` returns the current user and tenant.
- `POST /v1/auth/logout` revokes the current server-side session.
- `GET /v1/auth/sessions` and `DELETE /v1/auth/sessions/{id}` enumerate/revoke sessions.
- `GET /v1/auth/conversations` returns the signed-in user's durable conversation handles.

The web header renders the configured login choices. After sign-in it displays
the canonical account and tenant, and Chat can start or continue a previously
cataloged substrate session after refresh or from another signed-in device.
