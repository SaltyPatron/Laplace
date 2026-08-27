# Legacy PostgreSQL access audit and staged migration

Scope: SaltyPatron/Laplace on hart-server, not the refactor checkout. Observed
2026-08-27, with a repeatable snapshot at **22:30:48 UTC**. No database password/hash was
queried, no credential was rotated, and no PostgreSQL, firewall, role, service or
router configuration was changed during this audit. This is not a completed
network-hardening or least-privilege migration.

## Findings

**The immediate risk is passwordless LAN superuser access, not only a weak
password.** The ordered HBA rules currently parse as:

| Connection | Database / role | Authentication |
| --- | --- | --- |
| Unix socket | all / laplace_admin | peer, laplace_map |
| Unix socket | all / all | peer |
| 127.0.0.1/32 | all / all | trust |
| ::1/128 | all / all | trust |
| 192.168.1.0/24 | all / all | trust |

`trust` accepts the supplied role name, including superusers; changing a password
alone does not protect these matches. See [PostgreSQL trust authentication](https://www.postgresql.org/docs/18/auth-trust.html).
An explicitly noninteractive, read-only probe from hart-server to its own
`192.168.1.2:5432` succeeded as `postgres`, with no supplied password, service file
or usable password file; the connection reported `ssl=false`. That verifies the
LAN-IP HBA path on this host, **not** an independent firewall/WAN test. The parent
desktop task separately reported TCP 5432 reachable from Windows.

Effective settings and host evidence:

- `listen_addresses='*'`; listeners `0.0.0.0:5432` and `[::]:5432`.
- `ssl=off`; `password_encryption=scram-sha-256` is the password-storage default,
  not enforcement of password authentication.
- Configuration: `/opt/laplace/pgdata/data/postgresql.conf`; HBA and identity maps:
  `/opt/laplace/pgsql-18/conf/pg_hba.conf` and `pg_ident.conf`.
- Unix sockets: `/var/run/postgresql,/tmp`. Existing mappings authorize OS users
  `laplace-runner`, `postgres`, `ahart` as `laplace_admin`.
- Both `laplace_admin` and `postgres` are login superusers; `laplace_admin` also
  has replication and bypass-RLS attributes. No password values/hashes inspected.
- Last configuration load: `19:05:27.739992 UTC`; HBA/ident modification:
  `2026-08-24 03:26:13 UTC`. Parsed files have no errors. These views describe
  **on-disk files**, not an in-memory rule dump. The probe adds runtime evidence.
- At 22:30:48, 13 other client backends: all Unix-socket `laplace_admin` connections
  to `laplace`, eight active, four idle, one idle in transaction; no application
  name set. No established TCP connection appeared in `ss`. Earlier snapshots
  also had no TCP clients. This does not rule out intermittent consumers.
- `pg_stat_statements` **is installed, version 1.12**. It aggregates SQL activity
  by role/database/query identity; it is not a client-address connection history.
  Its statistics were neither cleared nor read as statement text for this audit.
  See [the extension's documented fields](https://www.postgresql.org/docs/18/pgstatstatements.html).
- `log_connections=''`, `log_disconnections=off` in the inspected effective
  configuration. This is not a claim that there are no database/CI logs or no
  query telemetry; it limits what can be inferred about past successful clients.
- `eno1=192.168.1.2/24`, default gateway `192.168.1.1`; IPv6 ULA/link-local
  addresses exist, with no IPv6 default route observed. Non-loopback IPv6 has
  no matching allow rule in the inspected HBA; a wildcard listener is not proof
  of an accepted IPv6 login.
- UFW is enabled and its service is active. Actual UFW/nft/iptables rule reads
  require privileges unavailable to this task. **Effective firewall rules and
  router/NAT/WAN exposure remain unverified.** No external scan was performed.

## Consumers and privilege boundary

| Consumer | Observed/configured database route | TCP password required? |
| --- | --- | --- |
| Live Linux API env | `/var/run/postgresql`, laplace_admin, laplace; no password field | No |
| Linux shared resolver and Actions setup | Same Unix socket and role | No |
| Managed HTTP MCP / Lichess | Fixed local socket, service OS identities mapped to laplace_admin | No |
| Local STDIO MCP | Existing installed resolver, unchanged | No on current host; explicit client overrides retained |
| Windows CLI seed/gate workflows | Configurable LAPLACE_PGHOST / LAPLACE_PGUSER | TCP supported; auth must be migrated |
| Other/occasional consumers | Not established by snapshots | Unknown |

Trace points: `LaplaceInstall.PostgresConnectionString`, `LaplaceDataSource`,
`ChessLiveGameHost.CreateAsync`, `.github/actions/setup-laplace-env/action.yml`,
`scripts/pipeline.sh`, `scripts/win/env.cmd`, `scripts/win/gate-remote.cmd`.
The Windows defaults contain a checked-in credential example. Do not repeat it,
copy it into desktop MCP settings, or assume it is a security control under
`trust`. Removing defaults and rotating credentials belongs to the coordinated
client migration below; no Windows client was silently reconfigured here.

Dedicated service OS users and local peer auth **do not remove database
superuser authority**. PostgreSQL runs as `laplace-runner`, also used by API/CI.
A database superuser can request server-side file access/program execution with
that OS account's authority. Thus MCP process sandboxing and a separate operator
token are not proof of isolation from server secrets or the runner's constrained
sudo permissions. This is a residual trusted-operator boundary, not a public or
untrusted-tenant deployment. See [server-side COPY privileges](https://www.postgresql.org/docs/18/sql-copy.html).
The advertised MCP tools do not accept arbitrary SQL or expose service controls;
that does not make the underlying superuser role least-privileged.

## Safe protections implemented in this branch

- Managed MCP HTTP and Lichess validate their resolved DB route before listener
  startup: exact installed socket/database/role/port; TCP, alternate sockets and
  nonempty password/passfile options fail without echoing their values. Empty
  placeholders are normalized away by the connection-string parser. This
  also covers overrides from optional service environment files. STDIO and
  Windows/CLI behavior remains compatible.
- Both units remove inherited `PGPASSWORD` and `PGPASSFILE`. The root-owned
  installer rejects changes to these requirements or the socket configuration.
- Host bootstrap now renders peer mappings for both known managed accounts when
  installed, preserving their access across re-provisioning. It validates the
  operator before opening the identity file. It does **not** change TCP rules.
- The existing CI policy job executes the redaction/map/deployment tests; its
  pre-install unit job executes `ManagedServiceDatabaseTests`. These tests do not
  connect to production or alter users, files under `/opt`, HBA, or services.
- A real peer-access runtime probe exposed a separate readiness bug in the new
  MCP host: the shallow health function still counts every entity and exceeds
  the five-second readiness deadline on this corpus. Readiness now uses the
  same typed estimated-inventory/perfcache probes as the API. The explicit
  health/audit tool is unchanged; readiness is not a corpus-integrity claim.

Reproduce the non-mutating snapshot as the existing peer-authorized operator:

```sh
python3 scripts/audit-pg-access.py
python3 scripts/test-pg-access.py
python3 scripts/test-managed-services.py
```

The script forces the installed Unix socket, disables password prompting and
user startup files, uses a clean environment and bounded read-only metadata
queries. It omits statement text, application-name values, password hashes,
HBA option values, command diagnostics and firewall comments. Missing access is
an explicit `unavailable`, not an empty successful inventory. It makes no TCP
login probe. Do not publish host snapshots as public CI artifacts.

## Staged migration — not applied

1. **Establish recovery and consumers.** An administrator retains a working local
   peer session, backs up HBA/ident/config/firewall state with ownership/modes,
   and reviews effective `ufw status verbose`, `nft list ruleset`, IPv4/IPv6 rules
   and router port forwards. Inventory Windows seed/gate clients, scheduled CI,
   interactive tools and any non-Laplace consumers over a complete operating
   cycle; obtain owner confirmation, not just a quiet snapshot. If approved,
   enable PG18 `log_connections='receipt,authentication,authorization'` and
   `log_disconnections=on` with restricted log retention and client-address
   metadata. Do not enable full query/parameter logging for this purpose. These
   settings apply to new sessions; do not terminate current ones to collect data.
   See [PG18 connection logging](https://www.postgresql.org/docs/18/runtime-config-logging.html#GUC-LOG-CONNECTIONS).
2. **Prevent configuration rollback first.** The legacy host bootstrap still
   selects wildcard listening for a nonempty `LAPLACE_PG_LAN_CIDR`, defaults that
   variable to the LAN even when explicitly empty, and rewrites trust HBA rules.
   Before changing live networking, replace this behavior in a reviewed,
   explicitly selected host policy with configuration-preservation/idempotence
   tests. Otherwise a later bootstrap can silently undo manual hardening.
3. **Migrate service privileges on a disposable test database.** Create distinct
   non-superuser, no-replication/no-bypass-RLS roles for serving, bot and CI work;
   enumerate required typed functions, schema/table/sequence permissions and
   extension operations. Test MCP reads/writes, chess learning and CI migrations
   independently; keep installation authority separate from serving. Prove
   denials of server-file/program access and role escalation. Update peer maps,
   managed route guards and deployment policy together only after these tests.
   Evaluate a separate PostgreSQL OS account so DB authority is not CI/sudo
   authority. Do not revoke the current role speculatively during ingest.
4. **Migrate each necessary TCP consumer.** Prefer running ingestion over the
   existing authenticated remote host using local peer auth. If direct TCP is
   genuinely required, issue a dedicated limited role and a new high-entropy
   secret through its server/client secret manager, configure verified TLS and
   exact database/role/source-address `hostssl ... scram-sha-256` rules. Put a
   canary rule **before** the broad trust rule and test correct, wrong and absent
   credentials from that client. TLS setup alone is not authorization. Never
   put database credentials in the desktop MCP configuration.
5. **Cut over after sign-off.** After every consumer is tested, remove both LAN
   and loopback TCP trust; retain required local peer mappings. Validate HBA
   parse errors, reload and test new sessions (HBA changes do not evict existing
   sessions). Restrict the firewall to documented consumers; if none need TCP,
   schedule `listen_addresses=''` and a controlled PostgreSQL restart outside
   ingest windows. If TCP remains, bind only necessary addresses. Validate IPv6
   independently. Only then rotate the disclosed privileged credential or
   disable its password login according to the agreed administration policy.
6. **Verify and recover deliberately.** Test API readiness, MCP initialize/list/
   a bounded read through HTTPS, Lichess authenticated readiness, CI and every
   approved Windows workflow; confirm unapproved TCP and wrong-role logins fail.
   Re-run the snapshot and compare intended listeners/rules/roles. On a regression,
   restore only the affected approved configuration from its backup using the
   retained peer session, reload/restart as required, and report any restored
   exposure. Do not revert to a disclosed password or reopen the entire LAN as
   an automatic recovery action. Retain prior secrets only under the approved
   transition policy, never in Git or public CI logs.

The [managed-services guide](managed-services.md) defines the separate TLS/bearer
desktop connection and deployment gates. This audit does not authorize activation,
credential rotation, firewall restriction, or interruption of current ingests.

## Verification for this change

Local hart-server checks, with .NET outputs isolated under
`/tmp/laplace-managed-build.nB2AVV`:

- Access-audit/mapping contracts: 9 passed; root deployment/service policy: 14
  passed, including rejected TCP/password unit edits and missing environment
  hardening. Deliberate in-memory mapping loss and diagnostic leakage each made
  the corresponding test fail.
- Managed DB route tests: 16 passed. Removing the socket-host guard deliberately
  caused four failures (TCP, alternate socket, multi-host); the guard was restored
  and the complete suite passed again before commit.
- MCP transport/readiness/startup: 20 passed; Lichess host/startup: 6 passed;
  API control/chess-runtime/key-mode regression: 28 passed. Tests inject fake bot
  connections and do not start an authenticated Lichess stream.
- The original HTTP readiness runtime check failed on the exact-count health
  function; after the focused repair, readiness returned HTTP 200 and an
  authenticated loopback client initialized, listed 24 tools, made a catalog
  read and deleted its session. Both installed and candidate STDIO launchers
  also initialized and completed that catalog read with 24 tools.
- Shellcheck, deployment payload contracts, pipeline validation, inventory and
  whitespace checks passed. No assertion about whole-corpus integrity or ingest
  throughput follows from these checks.
- The temporary HTTP process was stopped; live API PID 598176 and PostgreSQL PID
  556054/start times remained unchanged. Their current runtime configuration was
  not changed. New systemd units and the LAN TLS endpoint remain undeployed.

PR #1331 remains draft. CI must test the final branch revision before any merge
or privileged activation; local results do not substitute for that release gate.
