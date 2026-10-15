# IIS database maintenance

Use the installed IIS package and the existing Windows operator login. The command
reads the IIS installation selected by `LAPLACE_IIS_INSTALLATION` and the desktop
access declaration in `Laplace-Operations/development/installation.json`.

Installation owners are part of `Install-IisRuntime.ps1 -Operation Install` and
`-Operation Repair`. Setup discovers the host's existing private operator declaration
under `LAPLACE_SRC/Laplace-Operations/installation/<host>-operator.json`, or accepts
`-OwnerDeclaration`. It persists that declaration and the verified owner policy
under `state/platform-owners/`, outside the application database. Subsequent setup
uses the installed declaration. Repeating setup preserves the policy bytes and
verification timestamps when the declared bindings have not changed.

```powershell
./deploy/windows/Install-IisRuntime.ps1 `
  -Installation "$env:LAPLACE_IIS_INSTALLATION" `
  -Operation Repair
```

Setup authenticates with the installed workload AppRole and revokes its temporary
token after reading the identity record. It does not require personal development
DPAPI enrollment or a particular Windows SID. Initial workload enrollment retains
its existing provisioning contract. IIS setup requires administrator elevation;
local database maintenance separately requires the database-authorized SSPI
principal in the access declaration. Setup does not add a Windows or PostgreSQL
principal or change authentication rules.

Initial enrollment records exact previously authenticated provider bindings and
the current OAuth client context, or reuses a verified policy from the workload
identity record. Existing persisted bindings are sufficient after the database is
dropped. An unknown binding fails before policy replacement or database recreation.
No passwords, tokens, or sessions are seeded. Local SSPI setup repairs and checks
the application database before accepting the serving application; the owner
policy is already persisted at that point. Application `repair`, `migrate`,
`remigrate`, and `recreate` use that same policy before database mutation and verify
administrator restoration. `-InstalledCredentials` selects the protected installed
workload enrollment during setup; ordinary database maintenance retains its existing
development enrollment. `Set-IisPlatformOwners.ps1` remains a compatible explicit
policy-management entry point and shares setup's idempotent policy implementation.
Administrator includes operator permissions. Startup and validated sign-in also
reconcile the policy. Changing a configured owner's authority requires changing
the installation policy; ordinary account roles remain editable. Preserve the
private policy file with installation backups, outside database reset operations.

```powershell
./deploy/windows/Invoke-IisDatabase.ps1 -Database application -Operation status
./deploy/windows/Invoke-IisDatabase.ps1 -Database application -Operation repair
./deploy/windows/Invoke-IisDatabase.ps1 -Database substrate -Operation repair
./deploy/windows/Invoke-IisDatabase.ps1 -Database application -Operation recreate
```

`repair` and `migrate` create a missing selected database and apply its migrations.
`remigrate` clears the selected migration journal and reapplies migrations while
retaining data. `recreate` prepares a replacement, migrates it, restores the existing
database settings and grants, then replaces the selected database. It deletes the
selected database's data. Use `repair` after a database has already been dropped.
Interactive destructive commands request the selected database name; `-Yes` is the
explicit automation equivalent.

Application selection addresses accounts, sessions, provider bindings, API keys and
billing. Substrate selection addresses content. The Windows command validates both
declarations, the live PostgreSQL data directory and required roles before migration,
then reapplies the selected runtime role's declared access. It never creates another
cluster or changes HBA rules. Application checks verify the migrated tables and the
runtime role's reads. Receipts are under `LAPLACE_WORK/operations/iis-database-*`.

New IIS packages include `migrations/` and its SQL files. For a release installed
before this packaging change, `-MigrationRuntime` can explicitly select a prepared
migration executable from the corresponding source revision. Unsupported migration
commands are rejected before database work. The command changes database state;
IIS recycling and HTTP readiness verification are separate deployment operations.

An explicit `-MigrationRuntime` operates independently of IIS: it does not read
`web.config`, acquire the publication lock, or call IIS administration tools. Use
`-ReceiptDirectory <writable-directory>` when the shared work directory is not
writable. This path requires the database-authorized Windows login, not Windows
administrator elevation. It still validates the public installation and access
declarations, PostgreSQL data directory, and database roles. Coordinate the supplied
runtime's revision with deployments; it is not selected from the active IIS release.
The default installed-runtime path retains its publication lock and requires access
to that installation. Recycling an IIS application pool has separate IIS permissions.

After repairing a startup dependency, restart only the installed application with
the existing deployment-directory permissions:

```powershell
./deploy/windows/Restart-IisApplication.ps1 -ReceiptDirectory <writable-directory>
```

This uses the ASP.NET Core Module's `app_offline.htm` mechanism under the publication
lock. It preserves a pre-existing offline marker, waits until the site serves its own
marker, removes that marker, then verifies health, readiness and the HTML page. It
does not require IIS configuration access, invoke `appcmd`, or repeat database
maintenance. The installation grants its declared deployer Modify access to `web`.
Receipt success requires the actual HTTP and readiness checks to pass.
The receipt records the readiness response and its HTTP status. An empty substrate
can serve the website with working database and cache access while reporting
`product_ready: false`; content is not ingested by database repair. An unreachable
substrate or unavailable native cache still fails recovery.

`Build-IisPackage.ps1` builds both chess position and transition caches. Windows
publication includes both files, and package validation requires identical copies
for the API, CLI and UCI runtimes. Extension installation also copies the transition
file beside the shared position cache. This repairs an omitted delivery artifact;
it does not ingest a chess corpus or establish gameplay acceptance. Use the
`chess_perfcache` fields in the running application's readiness response to verify
activation after publication.

The GitHub **Database — maintenance** workflow has a `database` choice:
`application` runs application-only maintenance; `substrate` retains the existing
substrate workflow. The existing workflow targets remain staging and cloud; it does
not pretend to provide a Windows runner or a desktop deployment. Application
selection supports status, migrate, repair, remigrate and recreate. Its `reindex`
selection is rejected before changes because that implementation repairs substrate
indexes only. The shell wrapper and migration runtime must both come from a release
that supports the selected command.

Validation:

```powershell
pwsh -File tests/iis-database-maintenance.ps1
pwsh -File tests/iis-database-maintenance.ps1 -NpgsqlAssembly <built-Npgsql.dll>
pwsh -File tests/migration-command-routing.ps1 -Runtime <built-Laplace.Migrations.dll>
pwsh -File tests/iis-chess-cache-package.ps1
```

```sh
bash tests/database-application-maintenance.sh
```

These checks cover command routing, target validation, failure propagation and
Windows access orchestration. They use substitutes for database calls. They do not
establish a successful PostgreSQL reset or a healthy deployed IIS application.
The `-NpgsqlAssembly` check uses the packaged driver's real authentication parser
with the helper's connection strings, including the differing authentication-method
spelling accepted by Npgsql and libpq. Database calls remain substituted.
