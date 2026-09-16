# Browser identity, company accounts and subscriptions

Laplace accepts Microsoft and Google accounts through OpenID Connect. First login
creates a durable user, external identity, personal tenant and owner membership.
The browser receives an HTTP-only secure cookie containing an encrypted reference;
the separately encrypted authentication ticket is stored in `app.web_sessions`.
Conversation handles are indexed per user and workspace in
`app.conversation_sessions`; the substrate remains the ordered-turn authority.

## Company account workflow

Open **Settings** after signing in. Create a company workspace and select it before
purchasing a subscription. Workspace selection is a separate secure HTTP-only
cookie; it is not an authorization credential. The request resolver verifies
membership in PostgreSQL and derives the request's tenant. A caller-supplied
`X-Laplace-Tenant` or JSON tenant cannot override an authenticated workspace.

Settings exposes company naming, the membership roster, invitations, subscriptions,
API keys, and signed-in sessions. Owners and administrators manage subscription and
key controls. Administrators invite members; only owners invite administrators.
Invitation links are bearer capabilities: any signed-in holder can accept one.
Send them privately. They expire after seven days, are single-use, and can be
revoked. PostgreSQL stores only the token hash. The browser retains the token in
session storage during sign-in instead of adding it to an OAuth return-URL query.

Removing a member changes authorization on their next request. Workspace API keys
are independent service credentials, not personal browser sessions. Rotate any
service key that was shared with a departing employee. The last owner cannot be
removed through the membership endpoint.

**Billing** shows the selected workspace and its current catalog. Checkout requires
authenticated workspace authority and, in identity mode, durable PostgreSQL billing.
Existing recorded subscriptions are managed through Settings rather than starting
another checkout. Stripe returns to `/billing/success` or `/billing/cancel`.
The success page reads payment and subscription state; a redirect does not grant
access. The subscription projection follows signed Stripe events and reads the
current provider subscription and billing period. Repeated events in the same
period do not reset already-used allowances. A canceled subscription is not
reactivated by a late earlier event.

Use **Manage subscription and invoices** to open Stripe's customer portal. Its
customer ID comes from the selected workspace, never the browser payload. The
Stripe account needs a configured customer-portal configuration. This endpoint
reports an unavailable portal instead of silently pretending the change worked.
An active subscription permits the owner/administrator to create integration API
keys in Settings; secrets are shown once and are not retained in browser storage.

These account/payment changes do not redefine the historical catalog's arbitrary
service credits as measured native execution work. The resource-costing migration
remains governed by `docs/BILLING_PLACEHOLDER_MIGRATION.md` and issue #1425.

## Privacy boundary: not equivalent to login

Account isolation and substrate-data isolation are different requirements.
The legacy general Explore, geometry, and other substrate readers still address
shared data. The account endpoint therefore reports `privateDataIsolation: false`,
and Settings shows that actual limitation. There is no cosmetic privacy toggle.
Do not accept confidential company datasets into a shared deployment on the premise
that OAuth or an active subscription has already isolated all data paths.

Completing private-company-data service requires the read, write, native/perfcache,
export and MCP paths to enforce the same authorized data scope. This document and
these account changes do not claim that work has been completed or deployed.

## Register the deployment's identity providers

Microsoft and Google require the deployment to register its exact redirect URI and
obtain a client ID and server-side client secret. Do not use an unrelated app's
client identity or put client secrets in the web bundle.

For Microsoft, use the ordinary identity-platform web application registration
with organizational and personal accounts as the supported audience. The default
authority is `https://login.microsoftonline.com/common/v2.0`. Register:

```text
https://YOUR_HOST/signin-microsoft
```

For a Google Web application OAuth client, register:

```text
https://YOUR_HOST/signin-google
```

Registration references:

- https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- https://developers.google.com/identity/openid-connect/openid-connect

## Host configuration

Provider credentials belong in the existing `identity.env` secret file: Linux
`/opt/laplace/secrets/identity.env`, Windows `deploy/secrets/identity.env`.
Either provider may be configured independently; each configured provider needs
both halves of its credential pair.

```dotenv
LAPLACE_AUTH_MICROSOFT_CLIENT_ID=YOUR_REGISTERED_CLIENT_ID
LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET=YOUR_REGISTERED_CLIENT_SECRET
LAPLACE_AUTH_GOOGLE_CLIENT_ID=YOUR_REGISTERED_CLIENT_ID
LAPLACE_AUTH_GOOGLE_CLIENT_SECRET=YOUR_REGISTERED_CLIENT_SECRET
```

Public authenticated deployments use the existing host configuration names:

```dotenv
LAPLACE_AUTH_MODE=identity
LAPLACE_BILLING_STORE=postgres
LAPLACE_BILLING_BYPASS=false
LAPLACE_PUBLIC_BASE_URL=https://YOUR_HOST
LAPLACE_DATA_PROTECTION_KEYS=/opt/laplace/secrets/data-protection
```

Keep `STRIPE_API_SECRET` and, when explicitly configured, `STRIPE_WEBHOOK_SECRET`
in the existing server-side `stripe.env` secret flow. The Stripe webhook bootstrap
uses `/v1/billing/webhooks/stripe`, updates the required event set on an existing
endpoint, and keeps a stored signing secret associated with the endpoint that
issued it. An endpoint with an unknown signing secret needs the actual endpoint
secret; another endpoint's secret is not interchangeable.

Preserve the data-protection key directory across releases. Losing it invalidates
browser tickets even when their database rows still exist. Settings reports
configuration presence, not the values of any OAuth or Stripe secrets.
`header` mode remains an explicit development option; it is not a company identity
or privacy boundary. Unknown auth-mode names do not authorize header tenancy.

## Additive database/application update

The company-account changes use the existing `app` schema and normal migration
runner. They add workspace invitations, subscription event/period state, atomic
subscription application, and session-identity/revocation protections. They do not
recreate the substrate or reseed a corpus.

Apply pending migrations using the established lifecycle command before activating
the application release:

```sh
bash scripts/pipeline.sh migrate
```

Application deployment and provider configuration must be read back from the
installed host before reporting a live paid service. A source commit or successful
checkout-return page alone is not that confirmation.

## Principal routes

Browser identity: `GET /v1/auth/config`, `GET /v1/auth/me`,
`GET /v1/auth/login/{microsoft|google}`, `POST /v1/auth/logout`,
`GET /v1/auth/sessions`, `DELETE /v1/auth/sessions/{id}` and
`GET /v1/auth/conversations`.

Company accounts: `GET /v1/account`, `POST /v1/account/workspaces`,
`POST /v1/account/workspaces/{tenantId}/select`, `PUT /v1/account/workspace`,
`GET /v1/account/members`, `DELETE /v1/account/members/{userId}`,
`GET|POST /v1/account/invitations`, `DELETE /v1/account/invitations/{id}` and
`POST /v1/account/invitations/accept`.

Subscription controls: `POST /v1/billing/plans/{planId}/subscribe`,
`POST /v1/billing/checkout/status`, `POST /v1/billing/portal`,
`GET|POST /v1/billing/keys`, `POST /v1/billing/keys/revoke`,
`GET /v1/billing/entitlements` and `GET /v1/billing/usage`.
