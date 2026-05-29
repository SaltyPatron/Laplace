# Database — DbUp orchestration layer (Layer 1)

Per [ADR 0021](../docs/adr/0021-dbup-for-migrations.md) (DbUp + Npgsql), [ADR 0023](../docs/adr/0023-extension-owns-schema-dbup-orchestrates.md) (extension owns schema; DbUp orchestrates), and [ADR 0025](../docs/adr/0025-pg-extension-modularization.md) (two extensions: `laplace_geom` + `laplace_substrate`), this directory holds **extension-lifecycle migrations** — NOT substrate schema. The substrate's three core tables (`entities`, `physicalities`, `attestations`), custom types, GIST opclasses, SRFs, and aggregates live in the modular `.sql.in` sources under `extension/laplace_substrate/sql/` (and the 4D/geometry layer under `extension/laplace_geom/sql/`), built into `laplace_geom--<ver>.sql` / `laplace_substrate--<ver>.sql` and owned by those extensions themselves.

## Scope

DbUp migrations in `db/migrations/` do exactly four things:

1. **Ensure prerequisite extensions** — `CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS laplace_geom; CREATE EXTENSION IF NOT EXISTS laplace_substrate;` (in that order; `laplace_geom` requires postgis, `laplace_substrate` requires `laplace_geom` and creates the `laplace` schema)
2. **Apply role grants** at the schema level — USAGE for `laplace_app` and `laplace_readonly` on the `laplace` schema (created by the extension itself)
3. **Set default privileges** so future extension-owned tables pick up the right grants automatically
4. **Trigger extension upgrades** — `ALTER EXTENSION laplace_geom UPDATE TO 'A.B.C';` / `ALTER EXTENSION laplace_substrate UPDATE TO 'A.B.C';` (lands in a new migration file when an extension's `default_version` bumps)

**Substrate schema changes do NOT go here.** They go in the extension's `<name>--<from>--<to>.sql` upgrade scripts (generated from the modular `.sql.in` sources under `extension/<name>/sql/`).

If you find yourself writing `CREATE TABLE laplace.entities (...)` in a `db/migrations/` file, **stop** — that table belongs to the `laplace_substrate` extension. Move it to that extension's `.sql.in` sources.

## Layout

```
db/
├── migrations/                       Extension-lifecycle migrations (DbUp)
│   └── YYYYMMDDHHMMSS_<name>.sql
└── README.md                         This file
```

## Migration file format

Plain SQL. Idempotent (`CREATE ... IF NOT EXISTS`, `DO $$ ... END $$` for conditional logic).

Filename: `YYYYMMDDHHMMSS_<snake_case_name>.sql` — sortable.

## Workflow

### Locally

```sh
# Layer 0 must be done first (one-time, per ADR 0018):
sudo scripts/bootstrap-laplace-runner.sh bootstrap

# Layer 1 — DbUp orchestration (idempotent, agent-driven):
just db-up         # EnsureDatabase('laplace') + apply pending migrations
just db-status     # Show applied + pending
just db-reset      # Drop SchemaVersions (re-applies migrations; preserves data)
just db-nuke       # DROP DATABASE laplace + re-create empty

# Create a new timestamped migration file (snake_case name required):
just migrate-new add_pg_extension_postgis_topology
```

`DATABASE_URL` can be set to override the default connection (see `app/Laplace.Migrations/Program.cs`); otherwise peer auth resolves `laplace-runner` (or `ahart` for interactive work) to the `laplace_admin` PG role on the local socket.

### In CI

The `integration.yml` workflow's `db-ensure` job runs:

```yaml
- run: |
    cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_INSTALL_PREFIX=/opt/laplace \
      -DLAPLACE_PG_PREFIX=/usr/lib/postgresql/18
    cmake --build build
    cmake --install build      # no sudo — installs into laplace-runner-owned /opt/laplace
- run: |
    cd app
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- up
```

Idempotent on every push to `main`. Failures in `db-up` block the smoke test.

## Conventions

- **Idempotent operations only**: `CREATE EXTENSION IF NOT EXISTS`, `DO $$ ... END $$` blocks for conditional grants, `IF NOT EXISTS` on any tables (the rare operational table that lives outside the extension).
- **One concern per migration**: don't bundle unrelated DDL.
- **Never edit a migration after it lands on `main`**: create a new migration to amend.
- **Reference an ADR** in the migration's header comment when implementing an architectural decision.
- **No data migrations in the same file as schema migrations**: keep DDL files DDL-only; data migrations get their own file.
- **Don't create substrate tables here.** They belong in the `laplace_substrate` extension's `.sql.in` sources.

## How DbUp tracks state

DbUp creates a `SchemaVersions` table in the target database with columns `(SchemaVersionsID, ScriptName, Applied)`. Each successful migration's filename is recorded with the timestamp it was applied. On subsequent runs, DbUp queries this table and skips already-applied files.

If a migration file is deleted (do not do this on `main`; always add a new migration to revert), DbUp will not detect that. The migration files are the source of truth alongside the SchemaVersions table.

## Reset semantics

Two reset levels, both idempotent and safe to repeat:

- **`just db-reset`** — drops `SchemaVersions` only. Migrations re-apply on next `db-up`, but the substrate data, extensions, and database all stay. Use when migration tracking gets confused but the data is fine.
- **`just db-nuke`** — `DROP DATABASE laplace` + re-create empty. Loses all substrate data + extensions; requires typing `NUKE` to confirm. Use when starting fresh from a clean PG state without touching Layer-0 (system account, roles, peer auth).

For a true clean slate (including the runner identity itself), `sudo scripts/bootstrap-laplace-runner.sh reset` tears down Layer 0 as well.

## Why DbUp and not dbmate / EF Core / FluentMigrator

See [ADR 0021](../docs/adr/0021-dbup-for-migrations.md).

## Why does the extension own the schema instead of DbUp

See [ADR 0023](../docs/adr/0023-extension-owns-schema-dbup-orchestrates.md).
