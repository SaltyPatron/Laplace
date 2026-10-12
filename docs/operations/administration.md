# Accounts and administration

Sign in with a configured Microsoft or Google client, then open **Admin**. The
workspace picker uses names and the current membership role. Selecting another
workspace reloads the page so drafts and data from the previous workspace cannot
be submitted under the new selection. Requests include the expected workspace;
the API refuses a stale selection.

An email is a way to find a person, not an authentication key. Microsoft and Google
sign-ins may resolve to separate Laplace users even when their email matches.
Platform grants attach to the local user identified by the validated provider,
issuer, and subject. A matching email never automatically links accounts or grants
authority. A person must sign in before an administrator can assign their role.

## Workspace access

Each personal or organization workspace has its own membership, invitations,
billing, and API clients. In **Settings**, select a section instead of loading all
account operations at once.

On a phone, use the **Settings page** selector. Search members by email or name,
and API clients by application name or key prefix. Revoked keys remain available
through **Include revoked keys**. Removing a member, revoking a key or invitation,
and ending a sign-in session each show the affected target before submission.

An invitation first shows its workspace, inviter, role, and expiration. Accepting
one preserves an existing membership's role. If acceptance succeeds but workspace
selection fails, **Open workspace** retries selection without consuming the link
again. The workspace picker refreshes after acceptance. Creating a workspace uses
the same recovery behavior: **Open created workspace** does not create another.

API keys are masked until revealed or copied. Save a new key before creating the
next one; the secret cannot be retrieved after leaving the page. Revoking a
different key does not discard the new key waiting to be saved. Clipboard failures
leave a selectable field for manual copying.

| Role | Account operations |
| --- | --- |
| Member | Use workspace tools, inspect usage, and manage own sign-in sessions |
| Administrator | Member access plus workspace naming, member invitations/removal, billing, and API keys |
| Owner | Administrator access plus administrator invitations and membership/ownership role changes |

Only an owner may change membership roles. The server checks the current actor
and selected workspace inside a transaction and locks the workspace while
changing ownership. It refuses to demote or remove the last owner. Role changes
appear in the workspace's history. API keys are separate credentials; remove or
rotate keys given to a departing member.

Workspaces are account and billing boundaries over the shared knowledge base.
They do not create private copies of admitted content or imply private data
isolation.

## Platform access

Workspace ownership and external directory roles confer no platform authority.
**Admin → People & roles** searches existing sign-ins by email or name. Review the
provider and permission description before saving access.

| Platform role | Permissions |
| --- | --- |
| Observer | View ingestion journals and process status, database activity, and managed service status |
| Operations manager | Observer access plus start/stop ingestion, close run journals, and manage MCP/Lichess services |
| Platform administrator | Operations access plus maintenance, writable operations, agent configuration, and platform access management |

The API reads the platform grant on every protected request. A revoked grant
takes effect without signing out. Client API keys cannot inherit a browser
administrator's role. Browser mutations must originate from the same host.
Unknown administration routes require maintenance authority; read access to
ingestion/services does not expose agent configuration or account directories.

Platform role changes serialize in the application database, recheck the actor,
protect the last administrator, and record the actor, target, previous role, new
role, and time atomically. The directory is capped at 100 people per search and
200 workspaces. Role history shows the latest 50 events.

`GET /v1/access` describes the signed-in user's workspace role, platform role,
permissions, and platform role definitions. Administrative endpoints return
`platform_permission_required` when the caller lacks the required permission.
The same checks cover writable calls through `POST /v1/op`.

## Ingestion and services

**Admin → Ingestion** lists registered providers from the installed decomposer
registry. Select a source, optionally give its location **on the server**, and
review the host and shared destination before starting. Special CLI recipes are
available through **Other CLI source** and **Advanced options**. The provider list
is not an exhaustive list of every CLI recipe. Local files use **Data** uploads.

A launch receipt means the CLI process started. Completion comes from the
ingestion journal. The active-job list contains only processes owned by the
current API process; externally started runs and earlier runs remain in the
journal. Stopping a job preserves already committed data. Closing a journal row
does not stop a running process.

**Admin → Services** displays status for the installed MCP and Lichess services.
Start, stop, and restart require a review dialog. An accepted request does not
prove readiness; the page refreshes service status independently.

## Installation and initial administrator

Apply `20261012000000_app_platform_access.sql` through the normal migration
runner before deploying this API. Its `app_` prefix routes it to `LAPLACE_APP_DB`
when account storage is separate. It adds platform grants and access history;
it does not grant anyone access automatically.

An installation administrator can bootstrap an existing user with the existing
installation credential over HTTPS:

```http
PUT /v1/admin/access/people/{existing-user-id}/role
X-Laplace-Operator-Token: <installation credential>
Content-Type: application/json

{"role":"administrator"}
```

Use `GET /v1/admin/access/people?search=<email>` to verify the existing account and
provider before granting. The bootstrap event records `installation-credential`.
After bootstrap, use signed-in **People & roles**. The web UI never requests or
stores that shared installation credential. Do not enable a development
principal on a host serving untrusted users.

Workspace roles use `PUT /v1/account/members/{user-id}/role` with `owner`, `admin`,
or `member`. Platform roles use the endpoint above with `administrator`,
`operator`, `observer`, or `null` to remove platform access. Configured sign-in
client metadata is visible to platform administrators; client secrets stay in
installation configuration.
