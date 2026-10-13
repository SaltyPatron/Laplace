# Monorepo IIS installation

The monorepo owns this installer and its API, UI, CLI and chess UCI package. It does not install
Laplace-Refactor or replace the modular `laplace` implementation. The UCI connector
has its own installation in Laplace-UCI.

`deploy/windows/Install-LaplaceSite.ps1 -Installation <installation.json>` installs
the declared IIS site, application pool, TLS binding and OpenBao credential agent.
Run installation as Administrator. After installation, the declaration is recorded
in `LAPLACE_IIS_INSTALLATION` in the machine environment file; repeating
`Install-LaplaceSite.ps1` uses that declaration.
Repair preserves the last successfully selected release in `web/active.json`;
it does not revert an ordinary publication to the initial setup package.
Setup also retains read-only IIS application-pool grants enrolled by companion
installers on the public runtime. It collects them before replacing directory ACLs
and applies them to `web`, so subsequent releases inherit the same access.
The optional `public_runtime_readers` declaration adds explicit account names or
SIDs. These readers receive only read/execute access to public runtime files;
`agent` and `state` retain their separate permissions.

The declaration specifies the host, HTTPS origin, runtime and published package
directories, site/pool names, deployer SID, database selection and connection budget,
public environment settings, certificate trust, controller address and exact OpenBao
record references. Paths are data. The installed process has no source/build mount
dependency. Database passwords, provider secrets and controller credentials are not
part of the declaration or application package.

| Installed location, relative to `directory` | Purpose and access |
| --- | --- |
| `web/releases/<revision>` | Complete immutable application/native/CLI/UI payload; pool reads, declared deployer publishes |
| `web/releases/<revision>/uci` | Monorepo chess executable, runtime libraries and engine/release profiles from the same source publication |
| `web/web.config` | Selects the executable in the release; contains no credentials |
| `state/keys` | Persistent browser data-protection keys; pool can write |
| `state/logs` and `state/output` | Application logs and declared output; pool can write |
| `state/postgres-root.crt` | Database trust; pool can read |
| `state/chess-client.json` and `state/control.token` | Controller selection client; pool can read |
| `agent` | Protected AppRole enrollment, agent executable/configuration and render log; SYSTEM and Administrators only |
| `receipts` | Successful setup/publication results |

`Enroll-IisRuntime.ps1` uses an authorized OpenBao enrollment operator once to create
an AppRole that can read only the selected records. Its enrollment file is
encrypted for the enrolling Windows user. Run elevated installation as that same
user. The installed OpenBao Agent authenticates independently and refreshes records;
it never invokes a human login. The renderer updates only this pool's environment,
controller client and HTTPS certificate, recycling the pool when environment values
change. IIS protects pool environment configuration; secrets are not embedded in
the deployer-writable package.

For a Windows database on the same machine, `database.authentication: "sspi"`
uses the process's Windows identity without fetching or rendering a database
password. The database host must name this machine or its loopback address.
The CLI profile uses the same field; it needs no `credential` enrollment in this
mode. IIS still uses its existing workload enrollment for the API, identity,
certificate and chess records. The substrate and application password records
are omitted from its agent template and enrollment policy.

The selected PostgreSQL server must already accept the operator or application
pool's exact Windows identity for the declared roles and databases. Database
setup owns those mappings and grants. These client installers do not create a
cluster, grant database access, or migrate data. Both authentication modes require
verified database TLS with a certificate matching the declared hostname. Omitting
`authentication`, or setting it to `scram`, retains the scoped password-record
contract for existing installations.

Build and ordinary publication, in an unelevated PowerShell session:

```powershell
# LAPLACE_API_KEY is a workload API key used to verify authenticated operations.
& .\deploy\windows\Build-IisPackage.ps1 -Installation $installation -Publish
```

The existing `deploy/windows/publish.ps1`, `scripts/win/publish-deploy.cmd` and
`scripts/win/deploy-api.cmd` select this installation when
`LAPLACE_IIS_INSTALLATION` is configured. The first two build the application/CLI
and UI; `deploy-api.cmd` packages the already staged output. Their managed IIS path
does not use `appcmd`, mirror over a running release, or require elevation.

The package builder publishes the chess UCI application alongside API and CLI.
Its managed compiler outputs use `iis-publish` below the configured
`LAPLACE_BUILD_ROOT` (overridable with `-ManagedBuild`), so building an IIS release
does not overwrite DLLs used by an active local development API. Native inputs
still come from the selected `LAPLACE_ENGINE_BUILD`; installed execution requires
neither compiler directory. Existing older packages remain valid rollback inputs.

Chess catalog discovery and engine launch honor the same `LAPLACE_UCI` selection.
An explicit missing executable is reported unavailable instead of replaced by a
different installation. Otherwise the application uses its packaged `uci`
directory before older tool/build discovery. The separate Laplace-UCI connector
retains its own declared engine profile; publication does not silently repoint it.
A connector profile that explicitly follows `web/active.json` uses each published
UCI release. Publication preserves the selection file's ACL during atomic replacement
and grants the enrolled runtime readers to a new release before copying its files.
This prevents API setup or publication from breaking the separately installed UCI
connector. Run `laplace-uci-connect verify --profile <installed-profile>` after a
publication to exercise that connector and its selected engine, not only API health.

To publish an already prepared package:

```powershell
& .\deploy\windows\Publish-IisRuntime.ps1 -Installation $installation -Package $package
```

Release identifiers are derived from the packaged file paths and hashes; the source
commit is recorded separately. Rebuilding a changed workspace does not masquerade
as the same package merely because its Git HEAD is unchanged.

Publication verifies the file hashes and referenced UI assets, selects the release through `web.config`,
then checks HTTPS, the UI, both provider redirects, authenticated SQL, native storage
invariants and the declared Lichess controller. Failed verification restores the
previous release selection. Setup also verifies again after recycling the real pool
and retires an explicitly declared legacy site only after those checks pass.
Actual Google/Microsoft account interaction remains a separate browser check.

Before privileged installation, `Test-IisPackage.ps1` can run the package in IIS
Express with the installed ASP.NET Core module against its real selected databases.
It authenticates using the new AppRole, checks access denial to a supplied unrelated
record, runs the authenticated operations and revokes its temporary token. This
pre-install check does not establish machine IIS ACLs, HTTP.sys TLS binding, service
startup or the real named HTTPS origin; setup verifies those on the installed host.

Microsoft documents the [ASP.NET Core IIS module configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/web-config)
and [application pool environment variables](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/environmentvariables/).
OpenBao documents [Agent as a Windows service](https://openbao.org/docs/agent-and-proxy/agent/winsvc/)
and [template refresh and execution](https://openbao.org/docs/agent-and-proxy/agent/template/).
