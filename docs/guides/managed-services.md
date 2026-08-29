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
Do not merge or provision this branch's privileged host policy until those checks pass.

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

## Persistent host provisioning

`scripts/setup-host.sh` owns the privileged host configuration. Normal fresh-host
setup includes managed-service provisioning automatically. For an **existing**
host such as hart-server, its targeted, repeatable mode performs the same managed
configuration without rebuilding PostgreSQL, installing extensions, migrating the
database, cleaning build output, or restarting the API/bot/MCP processes:

```sh
sudo bash scripts/setup-host.sh managed-services
sudo bash scripts/setup-host.sh managed-services-status
```

Run these from the reviewed **legacy** checkout after CI passes, not from
Laplace-Refactor. Do not use full fresh-host setup as a service-only upgrade.
The status mode is read-only, emits a structured drift report and exits nonzero
on missing/invalid configuration. Both helper code files are version-checked by
CI. Privilege-policy upgrades go through this same setup entry point; CI cannot
install arbitrary root code from a runner-writable checkout.

This is not a forgotten manual bootstrap step. The installed root-owned policy
and desired LAN settings persist, and the same reconciliation runs through:

- Normal `setup-host`, including repeated targeted upgrades.
- `laplace-managed-host.service`, enabled at boot before the managed app units.
- `laplace-managed-host.timer`, enabled and running by default, hourly with
  persistent missed-run catch-up. Transient failures retry with bounded systemd
  rate limiting; lifecycle-lock conflicts defer without interrupting deployment.
- CI deploy/publish preflight, before live installation and after the ingest
  quiet gate; CI repairs expected drift and then requires a healthy status report.

Reconciliation preserves saved settings, creates missing dedicated accounts,
retains their local peer mappings, verifies constrained sudoers, repairs its
maintenance units/enablement and managed nginx configuration, and renews the TLS
leaf certificate before its 30-day renewal threshold. It does not start/restart
the app or PostgreSQL services, remove explicit operator stop markers, change
HBA/listen/firewall rules, or replace the CA identity. Unchanged configuration
does not trigger PostgreSQL/nginx reloads. Changed peer mappings use a reload,
not a PostgreSQL restart; nginx is syntax-checked and gracefully reloaded only
when its managed configuration/certificate changes.

Desired settings live in root-owned `/var/lib/laplace-managed/lan.json`. Omitted
setup arguments preserve them; explicit `--address`, `--network`, `--hostname`
arguments update them through `setup-host.sh managed-services`. Both address and
allowlist must remain within an RFC1918 LAN range. CI's fixed reconciliation
verb accepts no configuration overrides. Conflicting existing user identities or
untrusted policy files fail closed, rather than reassigning another account.
The same hostname generates TLS identity, proxy configuration and the MCP origin
environment file; a hostname change takes effect in the MCP process at its next
CI deployment/operator restart, without a separate hand-edited environment value.

Policy-upgrade receipts remain root-only under `host-policy-before-*`; failures
restore prior policy/settings, maintenance definitions/enablement and previous
proxy/certificate where changed. Failed peer reloads restore the identity file
and its ownership/mode. Existing accounts/CA material are retained for safe
retry, not deleted or regenerated. Application payload rollback remains separate.

The private LAN CA and server key remain root-only under
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
Leaf certificates last one year and renew automatically through the installed
maintenance policy, keeping the CA and existing leaf private key stable. Missing
or expiring CA material raises an explicit recovery/trust-migration failure; the
timer never silently changes desktop trust. A blocked firewall needs a reviewed
LAN-scoped 8443 rule, not public access.

The root helper accepts only the packaged two-unit contract. It rejects extra
directives, shell commands, alternate users, changed executables, missing sandbox
settings, or arbitrary unit names. An update to the root security policy itself
requires the standard setup-host policy upgrade; CI fails before live install if its
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
Deploy reconciles and checks installed host policy, failing early if it is missing
or differs from the reviewed source. Both install and publish
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
python3 scripts/test-managed-host.py
python3 scripts/test-pg-access.py
python3 scripts/test-pg-machine-tuning.py
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
already deployed. PR/Actions evidence and standard privileged host provisioning remain release gates.

Delivery: [PR #1331](https://github.com/SaltyPatron/Laplace/pull/1331), implementation
commit `7f7b38fe`; non-deploying
[Actions run 33117337062](https://github.com/SaltyPatron/Laplace/actions/runs/33117337062)
was dispatched with `stage=test`. Its live status is authoritative; dispatch is
not a passing result. No main merge or privileged activation was performed.

## Persistent provisioning follow-up (2026-08-28)

The setup-host integration and boot/timer/CI reconciliation replace the earlier
standalone manual-bootstrap instructions. Tests execute targeted setup dispatch,
repeated configuration, drift repair, read-only status, transaction exclusion,
peer reload rollback, real OpenSSL certificate renewal/name binding, preservation
of CA/private-key identity, nginx failure recovery and late policy-upgrade recovery
against isolated files and fake OS actions. `systemd-analyze verify` checks the
generated maintenance units. These are provisioning contract tests, not evidence
that this host's root policy or timer has been installed yet.
The 14 host tests passed locally. Deliberately omitting maintenance installation
and bypassing the renewal deadline each caused the corresponding test to fail;
the unmodified implementation was then rerun successfully. The existing 14
deployment-policy and 9 PostgreSQL audit/map tests also pass.

## Full setup tuning validation repair (2026-08-28)

A full `setup-host.sh` run reached PostgreSQL tuning but stopped before managed
provisioning because the validator compared requested bytes against PostgreSQL's
rounded native units. On hart-server, `shared_buffers=32949789kB` correctly became
`32949792kB`, and `effective_cache_size=65899578kB` became `65899576kB`. These are
8kB-block conversions, not unapplied settings. The validator now compares the
requested value after conversion to `pg_settings.unit`, using PostgreSQL's
nearest-even rounding rule. It still rejects a real whole-block difference,
disabled required features and any pending restart, including unlisted settings.
Failed SQL/connection queries and empty/incomplete results also fail closed.

The regression suite starts its own temporary, socket-only PostgreSQL with a
64MB buffer pool and no TCP listener, runs the actual shell validator, then stops
and removes that test cluster. It tests the reported sizes, rounding boundaries,
kB/MB units, genuine mismatches, query failures and pending restarts. A disposable
copy with the original byte comparison restored must fail the rounding regression.
The same suite runs in the existing CI unit-test stage before live installation.

The corrected validator passed all 18 expectations against the live cluster using
read-only peer queries and the bootstrap's CPU environment (without this agent
session's `OMP_NUM_THREADS`/`OMP_THREAD_LIMIT` overrides). No tuning was applied,
no production service was restarted, and no HBA/listener/credential was changed
by that validation. This verifies the reported failing step, not completion of
the remaining privileged full-host setup or managed-service deployment.

## Completed host setup and install error propagation (2026-08-28)

The subsequent operator-run full setup built/installed the native and managed
components, found no pending migrations, and installed the managed root policy,
peer identities, LAN TLS configuration and enabled maintenance timer. Read-only
checks confirmed the timer is active, the API returns readiness HTTP 200, and the
live `dynamic_library_path` already matches the canonical staged extension path.
MCP/Lichess application units still await CI publish; setup completion does not
mean those services are running.

That setup output exposed two SQL peer-authentication failures in the install
phase that were incorrectly followed by a success message. The pipeline was
running as root, which is intentionally not a database peer identity, and bash
disabled automatic error exits inside a function used as an `if` condition.
The pipeline now runs root-invoked SQL clients as `laplace-runner` on a local
socket, without adding a root peer mapping or using password/TCP fallback.
Library-path read/write/reload errors are distinct from an unchanged path and
abort installation. Failed copies or post-install SQL probes restore a previously
active API; an explicitly inactive API stays stopped. Full setup also stops
before install when its preceding build fails.

`python3 scripts/test-pipeline-install.py` executes these shell functions with
isolated artifacts and fake OS/database calls. Fifteen tests cover identity,
error propagation, reload acknowledgement, API restoration and build/install
ordering. Deliberately restoring swallowed preflight/build errors reproduces the
bad success paths. The suite runs in the existing CI deploy-payload contract gate.
No corrective live database write was needed for the already-correct library path.

## Integration connection-pool repair (2026-08-28)

Guarded rollout run `33136694752` passed host preflight, installation, database
synchronization and PostgreSQL regression tests, then stopped before publication:
seven synthetic-ingest tests reported PostgreSQL `53300` (too many clients).
The synthetic class passed by itself. `LocalPgFixture` shared one database but
allocated an independent default 100-connection datasource per fixture, retaining
separate idle connection pools during concurrent tests against a cluster whose
live connection limit was 37.

The fixture now shares one reference-counted datasource, created with the existing
production ingest connection budget. Initialization/disposal are idempotent, failed
initialization does not acquire a reference, and only the last owner closes the
pool/database. Three new database-backed regressions failed against the old fixture
and pass with the repair. The complete substrate suite passed locally: 820 passed,
one existing corpus-dependent skip. No production connection limit was raised and
no test was disabled or serialized to obtain that result. Follow-up CI run
`33137456589` passed installation, database synchronization and the full concurrent
integration stage, then restored the API. It intentionally stopped at integration;
managed-service publication still requires a successful publish-stage rollout.

## LAN TLS verification repair (2026-08-28)

On hart-server, the machine's own hostname resolves to `127.0.1.1`, while the
managed nginx listener deliberately binds only `192.168.1.2:8443`. Deployment
verification now connects to the provisioned LAN address while retaining the
configured hostname for TLS SNI, certificate verification and the HTTP Host
header. It does not change DNS, widen the listener, disable certificate checks,
use environment proxies, or follow redirects with the bearer token.

`python3 scripts/test-managed-tls.py` exercises real loopback TLS with disposable
certificates, including wrong-host and untrusted-certificate rejection before
HTTP credentials are sent, redirect rejection, and MCP initialization, discovery
and session cleanup. A deliberately restored hostname-resolution defect fails
the regression check. A read-only request using the candidate verifier against
the actual LAN listener returned readiness HTTP 200 with hostname verification;
this does not establish Windows reachability or MCP application deployment.

This repair changes the root-owned deployment policy. After its CI checks pass,
an administrator must upgrade that policy using the existing targeted entrypoint:

```bash
sudo bash /home/ahart/Projects/Laplace-Legacy/scripts/setup-host.sh managed-services
```

The targeted mode does not rebuild the application or run database migrations.
CI intentionally refuses application deployment when the installed policy differs
from the reviewed source; it cannot grant itself permission to replace root code.
Once upgraded, the enabled boot/timer reconciliation and CI preflight continue
maintaining the provisioned configuration. Rerun the normal CI deployment rather
than manually copying helpers, editing service files, or bypassing that check.

## First complete rollout and automatic rollback (2026-08-28)

The operator upgraded the root policy through the targeted setup-host command.
Both installed helpers matched commit `2b4273a1`; no further policy upgrade was
needed for rollout run `33137962132`.

That full Actions run passed policy, build, unit tests, host preflight, install,
database synchronization, integration, managed publication/readiness, and live API
smoke checks. Both `laplace-mcp.service` and `laplace-lichess.service` were observed
loaded, enabled and running; each readiness endpoint returned `ready:true`.
The publish verifier completed authenticated MCP initialization and tool discovery
through hostname-verified LAN TLS. Independent requests without credentials to
MCP and the operator stop route returned HTTP 401 without stopping a service.

The final existing semantic evaluation failed, so the workflow automatically
restored the previous API and removed the newly introduced managed units. API
readiness returned HTTP 200 after restoration. **MCP/Lichess are therefore not
currently deployed as managed services.** The host policy and maintenance timer
remain installed. No merge, data reset, secret rotation, or gate bypass occurred.

The failing operation was `converse.prompt_coherence`. API logs recorded a
15,054ms request followed by HTTP 503; PostgreSQL logged client cancellation of
the same statement at `2026-08-28 03:11:11 UTC`. The generic API message reported
the substrate as unreachable despite successful readiness requests during that
interval. `pg_stat_statements` is installed in the `laplace` schema; its completed
statement statistics and PostgreSQL's cancellation log were both inspected.

The evaluation also failed independently against the restored API with MCP and
Lichess absent. Command:

```bash
python3 scripts/eval-generation.py --api http://127.0.0.1:8080 \
  --probes scripts/eval-probes.json --baseline scripts/eval-baselines.json
```

This replay completed with exit 1: only `dog` passed the six topic-election
checks; pawn/chess and glacier selected `a` (rendered as `LATIN SMALL LETTER A`),
and France/water/hot selected `of`. The existing forward-hygiene checks passed,
but the expected opposite `cold` was not reached. Warming the read path therefore
does not resolve the semantic failure. The native election implementation and
expected-answer files are unchanged by this managed-services branch.

The current release policy couples application publication to this existing
semantic gate. Changing that policy is a separate release decision; this rollout
did not weaken it or change expected answers. Windows reachability, authenticated
live operator status after a committed release, and persistent MCP/Lichess
activation remain unverified until a release is committed.
