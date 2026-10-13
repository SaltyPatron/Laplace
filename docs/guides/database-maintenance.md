# Monorepo database maintenance

Use **GitHub → SaltyPatron/Laplace → Actions → Database — maintenance → Run workflow**.
Choose `main`, a target, and an operation. The job uses that host's installed
monorepo migrator, database declaration and runner identity. It does not require
an interactive administrator login, compile the application, or ingest a corpus.

From any machine with an authenticated GitHub CLI:

```sh
gh workflow run db-ops.yml --repo SaltyPatron/Laplace --ref main -f target=staging -f operation=status
gh run list --repo SaltyPatron/Laplace --workflow db-ops.yml --limit 5
gh run view RUN_ID --repo SaltyPatron/Laplace --log-failed
```

| Target | Database host |
| --- | --- |
| `staging` | hart-server; also the database host used by prod while hart-cloud is offline |
| `cloud` | hart-cloud; must already be running, this workflow does not start it |

| Operation | Effect |
| --- | --- |
| `status` | Report installed migration state and database health |
| `migrate` | Reconcile installed extension paths, update extensions and apply pending installed migrations |
| `repair` | Run the same database/extension reconciliation and pending migrations after an interrupted operation |
| `reindex` | Rebuild invalid substrate indexes |
| `remigrate` | Clear the selected database's migration journal and reapply installed migrations; does not drop the database |
| `recreate` | Delete the selected database's data and rebuild its installed schemas and extensions |

`recreate` first creates and migrates a replacement database using the current
owner, encoding, locale, tablespace, connection limit and database/role settings.
It restores the database grants, explicit object grants and default privileges
for objects recreated by the installed migrations. A preparation failure leaves
the original database available. Cutover disconnects existing sessions, switches
the database names transactionally and removes the old database. Workload
passwords and OpenBao enrollment remain unchanged.

Data and custom objects outside the installed migrations are discarded. If the
application/account tables share the selected database, those tables are reset
too. A separately configured application database is not recreated. Production
using hart-server's substrate database sees the staging reset immediately; this
target is not isolated merely because the workflow calls it staging.

The success result includes database structure, extension and index checks. An
empty substrate database can be structurally healthy without having enough
admitted content for an operation. Recreate does not restore previous corpus
ingestions or declare the product ready.

Ordinary ingestion uses the [installed monorepo client](installed-mono-client.md).
Installation and credential enrollment remain host setup operations; maintenance
does not create a guessed administrator role or borrow a human's login.

## Highway reconciliation and chess tools

**Actions → Product — maintenance** uses the selected `staging`, `production`
or already-running `cloud` runner. `reconcile` drains the selected installed
database's highway work using that host's declared PostgreSQL role, then checks
the installed database. It does not build or publish the application. Production
may select hart-server's substrate; selecting a different application host does
not imply a separate database.

`chess-lab` updates the declared external chess tools and their runtime settings,
restarts the installed application through its setup-owned restart mechanism,
and executes the installed UCI dependency check. A GUI is included only when
declared or already installed. The operation uses configured build/work paths
and does not provision another PostgreSQL instance. Root-owned directories,
service units and credentials still belong to host setup.

```sh
gh workflow run product-operator.yml --repo SaltyPatron/Laplace --ref main -f target=staging -f operation=reconcile
gh workflow run product-operator.yml --repo SaltyPatron/Laplace --ref main -f target=staging -f operation=chess-lab
```

Both operations share the host deployment lock. Their Actions artifacts contain
the installed target before and after the operation and its execution log.
