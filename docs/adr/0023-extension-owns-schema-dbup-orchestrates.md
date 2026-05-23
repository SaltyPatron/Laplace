# ADR 0023: Laplace extension owns its schema; DbUp orchestrates extension lifecycle

## Status

**Accepted** — 2026-05-21 — **amended 2026-05-23 by [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md)**: the substrate-owns-schema invariant in this ADR stands; the "DbUp orchestrates via `laplace_priv.install_extension(...)` SECURITY DEFINER wrapper" mechanism is replaced by plain `CREATE EXTENSION IF NOT EXISTS ...` calls since `laplace_admin` is now a `SUPERUSER`.

Narrows the scope of [ADR 0021 — DbUp + Npgsql for migrations](0021-dbup-for-migrations.md) without superseding it.

## Context

ADR 0021 introduced DbUp as the migration tool. The initial draft of `db/migrations/20260521000000_initial_extensions.sql` did the right thing — `CREATE EXTENSION postgis; CREATE EXTENSION laplace;` — but the next obvious step (add migrations for `entities`, `physicalities`, `attestations`, types, indexes, functions) ran headlong into a question we had not answered:

> Should the substrate's three core tables be **owned by the `laplace` extension** (created by `laplace--X.Y.Z.sql` when `CREATE EXTENSION laplace` runs), or owned by **DbUp migrations** (created by `db/migrations/*.sql`)?

PostgreSQL extensions can create their own schemas, types, functions, operators, opclasses, AND tables. When the extension owns its objects:

- `pg_dump` writes a single `CREATE EXTENSION laplace` line instead of each member object. Re-creating the database is one command, not thousands of CREATE statements.
- `DROP EXTENSION laplace CASCADE` removes everything atomically.
- `ALTER EXTENSION laplace UPDATE TO 'A.B.C'` runs `laplace--prev--A.B.C.sql` upgrade scripts. PostgreSQL handles version sequencing.
- Tables containing user data can be marked with `pg_extension_config_dump(...)` so `pg_dump` includes their **data** (entities ingested, attestations recorded) while still emitting the schema via `CREATE EXTENSION`.

When DbUp owns those objects:

- Each table / index / function is a separate `CREATE TABLE` in a timestamped SQL file. The history of changes is visible in version control.
- Schema diff tools (Atlas, etc.) work normally.
- But `pg_dump` will dump every member object as its own statement (no extension-level abstraction).
- Upgrades are flat-file sequencing, not the PG-native extension upgrade machinery.

Doing **both** — extension creates some objects, DbUp creates others — produces a permission and ownership tangle and defeats both mechanisms' guarantees. We pick one.

For Laplace specifically:

- The three substrate tables (`entities`, `physicalities`, `attestations`), the custom types they use, the GIST opclasses, the SRFs, and the cascade-tier functions are **the extension**. They're not application-level schema that happens to live in Postgres — they ARE the substrate.
- Substrate versions correspond to substrate-binary versions (the `.so` and its SQL evolve together — a function pointer in `laplace.so` must match its declaration in `laplace--X.Y.Z.sql`).
- This is exactly what `extension--A.B.C.sql` + `extension--A.B.C--D.E.F.sql` is for.

DbUp still earns its place — but for a narrower role.

## Decision

**The `laplace` extension owns the substrate's schema.**

1. `laplace--A.B.C.sql` is the source of truth for: the `laplace` schema, the three core tables (`entities`, `physicalities`, `attestations`), all custom composite types, all GIST opclasses, all functions / SRFs / aggregates, all indexes on extension-owned tables.
2. The control file sets `relocatable = false` and the SQL uses `@extschema@` references where appropriate. The extension's objects live in the `laplace` schema by convention.
3. The three core tables are marked with `SELECT pg_extension_config_dump('laplace.entities', '');` (and similarly for `physicalities`, `attestations`) inside `laplace--A.B.C.sql` so `pg_dump` includes user-data rows.
4. Schema evolution between substrate versions uses **extension upgrade scripts**: `laplace--A.B.C--D.E.F.sql`. `ALTER EXTENSION laplace UPDATE TO 'D.E.F'` runs them.

**DbUp orchestrates extension lifecycle.**

DbUp migrations under `db/migrations/` are responsible for:

1. `CREATE EXTENSION IF NOT EXISTS postgis;`
2. `CREATE EXTENSION IF NOT EXISTS laplace;` — or, if installed, `ALTER EXTENSION laplace UPDATE;` to the current default version.
3. Role grants on the `laplace` schema for `laplace_app` and `laplace_readonly` (since these are NOT extension-owned roles).
4. Any future non-extension operational tables (e.g., ingestion-job ledgers, agent-tracking metadata) if and when those are needed. Currently: none.

DbUp's `SchemaVersions` table still records what's been applied, so re-runs are idempotent. The migration files are short — they orchestrate, they don't define substrate schema.

## Consequences

- **Substrate schema versioning lives in the extension.** Bumping `default_version` in `laplace.control` + adding `laplace--A.B.C--D.E.F.sql` IS the migration for substrate objects.
- **`pg_dump` of a Laplace database is small.** Schema portion is one `CREATE EXTENSION` line + role grants; data portion is the contents of the three tables.
- **CI's `db-ensure` job is simple**: build extension → `sudo make install` → `dotnet run --project app/Laplace.Migrations -- up` → done. DbUp ensures both extensions installed + at current version.
- **Adding a new core column** = bump extension version, write `laplace--A.B.C--D.E.F.sql` upgrade script, run `just migrate-up` (which DbUp resolves to `ALTER EXTENSION laplace UPDATE`). NOT a new DbUp migration file.
- **Adding an operational table** (someday, if needed) = new DbUp migration file. Substrate tables = extension upgrade scripts. The split is on data identity, not on tooling preference.
- **`relocatable = false`** in `laplace.control` (the extension references its own schema name in opclasses and FK targets; relocation would break those).

## Alternatives considered

- **DbUp owns everything (initial draft of ADR 0021).** Rejected — fights with PG's extension model, loses `pg_dump` compactness, loses `ALTER EXTENSION UPDATE` sequencing, makes substrate-binary/SQL version coupling implicit instead of explicit.
- **Extension owns everything; no DbUp at all.** Rejected — still need to orchestrate `CREATE EXTENSION` itself, plus future cross-extension grants. Better to have a single Layer-1 entry point (the .NET migrations app) than spread orchestration across shell scripts.
- **DbUp owns three core tables; extension owns types + functions only.** Rejected — splits the substrate definition across two version-control mechanisms, requiring agent attention to keep them in lockstep. Sabotage-shaped.

## References

- [PostgreSQL 18 — Packaging Related Objects into an Extension](https://www.postgresql.org/docs/current/extend-extensions.html)
- [PostgreSQL 18 — ALTER EXTENSION](https://www.postgresql.org/docs/current/sql-alterextension.html)
- [pg_extension_config_dump() — pgPedia](https://pgpedia.info/p/pg_extension_config_dump.html)
- ADR 0021 (DbUp + Npgsql for migrations) — narrowed by this ADR
- ADR 0001 (extend PostGIS via Z+M) — laplace extension depends on postgis being installed first
- ADR 0019 (laplace-runner system account) — bounded sudo for `make install*` runs the extension into PG's extension dir
- `extension/laplace.control`, `extension/laplace--*.sql`, `db/migrations/`
