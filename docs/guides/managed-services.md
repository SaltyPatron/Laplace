# Managed MCP and Lichess on the legacy host

## Preserved baseline and delivery boundary

This applies to **SaltyPatron/Laplace**, not Laplace-Refactor. Before these changes,
`/opt/laplace/app/laplace-mcp` was a client-launched STDIO process, not an HTTP
listener or systemd unit. On hart-server, initialization advertised protocol
`2025-06-18` and 24 tools; `api` and shallow `health` succeeded against the local
database. `/mcp` and `/sse` on port 8080 returned SPA HTML. The no-argument STDIO
launcher, `.mcp.json`, `.cursor/mcp.json`, and local Codex configuration remain
compatible. Existing STDIO clients are not stopped by deployment.

The new services are not activated by a build or a test. Branch validation uses
the existing Actions workflow with `stage=test`; it stops before live installation.
The managed service tests use ephemeral loopback listeners and fake tools/bots.
Do not merge or perform the privileged bootstrap until those checks pass.

## Runtime and access contract

| Surface | Managed unit / backend | LAN access |
| --- | --- | --- |
| MCP | `laplace-mcp.service`, `127.0.0.1:5188` | `https://hart-server:8443/mcp` |
| Lichess | `laplace-lichess.service`, `127.0.0.1:5189` | Read status/chat through the API; no direct listener |
| Operator controls | Existing `laplace-api.service` | `https://hart-server:8443/v1/admin/services/...` |

The MCP transport implements the existing `2025-06-18` protocol using Streamable
HTTP POSTs with JSON responses. GET returns 405 because server-push streaming is
not offered; this is not the retired SSE transport. An initialization returns a
cryptographically random `Mcp-Session-Id`; subsequent requests carry that ID and
`MCP-Protocol-Version: 2025-06-18`. Notifications receive empty 202 responses.
Sessions serialize their canonical `McpServer`/`SubstrateTools` calls, are bounded
to 32, expire after 30 idle minutes, and can be closed with DELETE. Bodies are
limited to 1 MiB. Foreign/opaque origins and missing/wrong bearer tokens fail
closed. See the [MCP transport specification](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports).

Only nginx listens on the LAN: `192.168.1.2:8443`, restricted to
`192.168.1.0/24`, TLS 1.2/1.3. The existing port-8080 API is not repointed or
removed. Both new app listeners are explicitly loopback-only and ignore
`ASPNETCORE_URLS`. No PostgreSQL listener or pg_hba rule is changed. Dedicated
`laplace-mcp` and `laplace-lichess` OS accounts use the existing Unix socket and
local peer map to `laplace_admin`. That is privileged database access; the MCP
bearer belongs only to trusted operators, not anonymous/public clients.

The [PostgreSQL access audit](postgresql-access-audit.md) records the existing
LAN/loopback `trust` exposure and the staged migration required before claiming
least-privilege isolation. Managed hosts now reject TCP/password DB overrides;
the host-bootstrap renderer retains their peer mappings on re-provisioning.

## One-time privileged bootstrap

The existing runner's sudo policy cannot install new units/users. After the PR's
non-deploying CI checks pass, an administrator must run the checked-out, reviewed
installer once on hart-server:

```sh
sudo python3 deploy/linux/laplace-managed-deploy bootstrap \
  --address 192.168.1.2 --network 192.168.1.0/24 --hostname hart-server
```

This installs root-owned constrained helpers, creates the two service accounts,
extends only their local PostgreSQL peer mappings (reload, not restart), and adds
the HTTPS nginx virtual host. It does **not** start either bot/MCP unit. It creates
a private LAN CA and server certificate; private keys remain root-only under
`/var/lib/laplace-managed/tls`. The public CA is
`/opt/laplace/share/laplace/managed-services-ca.crt`. Transfer that public file over
the already-trusted remote connection, verify its SHA-256 fingerprint on both
hosts, and trust it in the local Windows host's certificate store before using
the browser UI. For Codex HTTPS clients, also set `CODEX_CA_CERTIFICATE` in the
desktop process environment to the absolute path of the verified PEM CA file (or
the existing trusted CA bundle with this CA added). This is the documented private
root mechanism; `SSL_CERT_FILE` is its fallback. See the
[official environment-variable reference](https://learn.chatgpt.com/docs/config-file/environment-variables).
Do not overwrite an existing corporate CA bundle or disable TLS verification.
The initial leaf certificate lasts one
year; renew through the same administrator-owned bootstrap before expiry, keeping
the CA stable. A blocked firewall needs a LAN-scoped 8443 rule, not public access.

The root helper accepts only the packaged two-unit contract. It rejects extra
directives, shell commands, alternate users, changed executables, missing sandbox
settings, or arbitrary unit names. An update to the root security policy itself
requires an explicit bootstrap upgrade; CI fails before live install if its
policy differs. Ordinary app/unit payload publication uses the installed helper.
No root script from a runner-writable payload is executed.

## Secrets and desktop configuration

Existing GitHub repository secrets remain the source of truth. Publish requires:

- `LAPLACE_MCP_TOKEN` → `/opt/laplace/secrets/mcp.env`.
- `LAPLACE_OPERATOR_TOKEN` → `/opt/laplace/secrets/operator.env`.
- Existing `LICHESS_API` → `lichess.env`; existing Stripe secrets are unchanged.

Use distinct high-entropy MCP/operator tokens (at least 32 token-safe characters).
CI never prints their values. Systemd loads the appropriate secret into each
process; the MCP OS account has no direct operator-token file access or sudo grant.
Its current database superuser role still carries server-side authority as the
PostgreSQL OS owner; it is not an isolation boundary from the runner's secrets or
privileges. See the access audit before enabling access for any untrusted caller. Optional
Lichess settings are documented in `deploy/linux/managed-services/lichess-service.env.example`.
An explicit settings change takes effect on service restart, not through a second
in-process API bot.

On the **local Windows Codex host**, make the MCP secret available as
`LAPLACE_MCP_TOKEN` in the desktop process's environment through the user's secure
secret mechanism, then configure:

```toml
[mcp_servers.laplace]
url = "https://hart-server:8443/mcp"
bearer_token_env_var = "LAPLACE_MCP_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 60
```

Restart/reload that client. The URL/bearer environment-variable fields are defined
in the [official Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
Do not copy database credentials or the operator control token into this MCP
entry. The ChatGPT desktop/Codex host configuration is distinct from hosted web
plugins. STDIO users on hart-server can keep launching the original command.

## Operational API

All routes below require `X-Laplace-Operator-Token`, regardless of `LAPLACE_AUTH_MODE`.
A customer API key or tenant header alone cannot authorize them. Remote control
requests require HTTPS. Only `mcp` and `lichess` are accepted:

```text
GET  /v1/admin/services/{mcp|lichess}
POST /v1/admin/services/{mcp|lichess}/start
POST /v1/admin/services/{mcp|lichess}/stop
POST /v1/admin/services/{mcp|lichess}/restart
```

Mutation returns 202 with the observed systemd state; acceptance is not readiness.
The helper submits fixed systemd jobs without a shell. Status reports load/active/
substate, result, PID, enablement and persistent stop intent. A concurrent deployment
returns 409; unavailable privilege/helper returns 503, never a false acceptance.
Actions are audited through `laplace-service-control`
in the auth journal, and authorization failures through the API log. The API can
control only these two units through this helper; MCP does not expose a service
control tool. The separate API remains running when MCP is stopped.

The old `/chess/lichess/start` and `/stop` routes delegate to the same operator
boundary. `/chess/lichess/status` and game-chat reads proxy the managed host.
Starting a `lichess-bot` through the lab API is rejected to prevent a second bot.
The UI keeps an entered operator token only in component memory and disables
token entry/control over HTTP. Removed in-process start knobs are shown read-only.

An operator stop creates a root-owned stop marker. CI and reboot respect that
choice; explicit API start/restart clears it. Do not use raw systemctl start to
override a marker. Both units are enabled and started by default when first
published. Lichess stops accepting new challenges, allows a 20-second game drain,
then cancels remaining games without synthesizing results. Systemd bounds total
shutdown at 45 seconds. Existing ad-hoc bot processes must finish before initial
managed activation; preflight rejects active legacy CLI/API bots without stopping
them or printing their arguments. Do not kill them to make deployment pass.

## CI/CD, readiness, and recovery

The existing `laplace.yml` owns delivery: policy and native/.NET tests precede any
installation. Managed policy/auth/transport tests run in the pre-install unit job.
Deploy fails early if privileged bootstrap is absent. Both install and publish
wait for active ingests. No seed, reset, database exposure, or cancellation is
introduced.

Publish snapshots the prior API payload and secrets into a mode-0700 backup,
records the prior unit/pointer/running state, and builds both managed hosts into
new immutable `app/releases/runtime.*/` directories. The original `mcp-runtime/`
and every prior release are preserved for running STDIO clients. The API stops
only for its payload replacement, then resumes through the existing readiness
gate. New units are reconciled/enabled and non-stopped services restart.

MCP `/health/ready` uses the API's typed estimated-inventory and perfcache probes,
with a five-second deadline, not the health tool's full entity-count scan. This
is serving readiness, not full-corpus integrity verification. Lichess readiness requires a configured
token, initialized chess host and live authenticated event stream. `/health/live`
only checks the process. CI additionally verifies MCP initialization and tool
discovery through the authenticated HTTPS nginx URL. This does not claim an
entire Lichess game has been played or a full corpus has been audited.

The transaction remains open through the existing smoke/eval jobs. Only their
successful conclusion commits/stamps publish. Failure restores the prior API
payload, secret files, managed unit definitions/enablement, API operator drop-in,
launch pointers and previously
running services, while preserving explicit operator stops. Root recovery receipts
and payload backups remain for audit. No release/backup garbage collection occurs
automatically; prune only after confirming no process still uses a release.

Manual recovery from the CI checkout uses:

```sh
bash deploy/linux/managed-publish.sh rollback
sudo systemctl start laplace-api
```

Focused non-activating checks:

```sh
python3 scripts/test-managed-services.py
python3 scripts/test-pg-access.py
bash scripts/test-deploy-payload-sync.sh
dotnet test app/Laplace.Endpoints.Mcp.Tests/Laplace.Endpoints.Mcp.Tests.csproj
dotnet test app/Laplace.Endpoints.Lichess.Tests/Laplace.Endpoints.Lichess.Tests.csproj
dotnet test app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj --filter FullyQualifiedName~ServiceControlTests
```

On a shared host, set `LAPLACE_BUILD_ROOT` to an isolated directory for local
checks. The tests do not start a real Lichess bot or mutate systemd/production DB.

## Branch validation evidence (2026-08-27)

On hart-server, with outputs isolated under `/tmp/laplace-managed-build.nB2AVV`:

- MCP transport tests: 14 passed; Lichess host tests: 5 passed; API service-control,
  chess-runtime and key-mode regression tests: 28 passed.
- Root-helper/deployment policy: 14 passed. Deliberately unsafe unit directives,
  a failed runtime copy, racing deployment lock and legacy bot process are rejected.
- The MCP bearer check was deliberately removed in the isolated source: the
  wrong-token test failed with expected 401 / actual 200. The check was restored
  immediately and the full transport suite rerun before commit.
- Both new hosts publish successfully in Release. The API builds with regenerated
  OpenAPI; `npm run build`, shellcheck, manifest validation and inventory checks pass.
- Installed and candidate STDIO launchers both initialize with protocol
  `2025-06-18`, return the same 24 tool names, and keep stdout JSON-only.
- Official Python MCP SDK 1.29.1 and 2.1.1 clients initialize the candidate HTTP
  process, list 24 tools and call `api` with `query=substrate_counts` successfully.
  SDK 2.1.1 uses automatic protocol negotiation. These probes use only a temporary
  loopback process and a catalog read, not a writer or a deployed service.
- The existing Lichess token passes a read-only `/api/account` check and identifies
  a bot account. No real bot, challenge, game or managed service is started by these checks.

These are local results, not a claim that the LAN TLS URL or new systemd units are
already deployed. PR/Actions evidence and the privileged bootstrap remain release gates.

Delivery: [PR #1331](https://github.com/SaltyPatron/Laplace/pull/1331), implementation
commit `7f7b38fe`; non-deploying
[Actions run 33117337062](https://github.com/SaltyPatron/Laplace/actions/runs/33117337062)
was dispatched with `stage=test`. Its live status is authoritative; dispatch is
not a passing result. No main merge or privileged activation was performed.
