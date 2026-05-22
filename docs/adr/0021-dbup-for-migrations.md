# ADR 0021: DbUp + Npgsql for migrations

## Status

**Accepted** — 2026-05-21

(Initial draft proposed dbmate; revised before any code committed to **DbUp** because the project's app layer is already in .NET / Npgsql.)

## Context

Database schema and extension installation were being managed by ad-hoc setup scripts using `CREATE ... IF NOT EXISTS` idioms. That's poor-man's migrations — no versioning, no rollback story, no audit trail, no diff between "deployed" and "intended" schema.

The Laplace stack already includes **.NET 10 + Npgsql** for the app layer (P/Invoke bindings to the C engine, plus future Synthesis CLI + endpoint plugins). Introducing a non-.NET migration tool (dbmate, sqitch, Flyway, Liquibase) would add a parallel toolchain.

## Decision

Use **[DbUp](https://dbup.readthedocs.io/)** with Npgsql:

- Migration files are plain `.sql` under `db/migrations/`.
- File naming: `YYYYMMDDHHMMSS_<snake_case_name>.sql` (sortable).
- A small **`Laplace.Migrations`** .NET console project in `app/Laplace.Migrations/` uses DbUp to discover and apply unapplied migrations.
- DbUp tracks applied migrations in a `SchemaVersions` table inside the `laplace` database.
- Migrations are SQL-only (no C# fluent API) — fits PG-extension-heavy DDL (custom GIST opclasses, geometry types, custom functions).
- Run via:
  - Local: `just migrate-up` → `dotnet run --project app/Laplace.Migrations`
  - CI: same `dotnet run` command in the `db-ensure` job of `integration.yml`

### Scope (narrowed by ADR 0023)

DbUp orchestrates **extension lifecycle and cross-extension setup**, not substrate schema:

- `CREATE EXTENSION IF NOT EXISTS postgis;`
- `CREATE EXTENSION IF NOT EXISTS laplace;` (or `ALTER EXTENSION laplace UPDATE;` once installed)
- Role grants on the `laplace` schema for `laplace_app` and `laplace_readonly`
- Any future non-extension operational tables

Substrate schema (the three core tables, types, opclasses, functions) is owned by the `laplace` extension itself via `laplace--A.B.C.sql` and upgraded via `laplace--A.B.C--D.E.F.sql` upgrade scripts triggered by `ALTER EXTENSION laplace UPDATE`. See [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md).

## Consequences

- All schema/DDL changes go through `db/migrations/`. No raw `CREATE TABLE` in setup scripts.
- CI applies pending migrations on every workflow run — agent-driven, idempotent (skip-if-applied is DbUp's job).
- No new toolchain dependency (Go binary, JVM, Perl) — we already have .NET 10.
- Local dev uses the same migrations via `just migrate-up`.
- Schema state at any point is recoverable from the migrations directory.
- Connecting to PG is via Npgsql using `DATABASE_URL` env var or peer auth (laplace-runner / ahart → laplace_admin per ADR 0019).

## Alternatives considered

- **dbmate** (initial draft choice): Go binary, single-file, language-agnostic. Rejected because it duplicates the role of .NET we already have.
- **EF Core Migrations**: code-first migrations in C#. Rejected because Laplace's schema is PG-specific (geometry types, custom extension functions, GIST opclasses); EF's abstraction layer obscures rather than helps.
- **FluentMigrator**: C# fluent API. Same downside as EF Core — moves SQL into C#, which is wrong for heavily PG-specific DDL.
- **Evolve**: similar to DbUp; smaller community.
- **sqitch / Flyway / Liquibase**: external toolchains; rejected to stay in .NET.
- **Raw SQL setup scripts with `IF NOT EXISTS`**: rejected — not real migrations.

## References

- [DbUp documentation](https://dbup.readthedocs.io/)
- ADR 0018 (three-layer architecture — migrations are Layer 1)
- ADR 0019 (laplace-runner peer-auth into laplace_admin — used by Migrations console app)
- ADR 0023 (extension owns substrate schema; DbUp orchestrates extension lifecycle — narrows this ADR)
- `app/Laplace.Migrations/`
- `db/migrations/`
