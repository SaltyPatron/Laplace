# ADR 0045: `laplace_admin` is a SUPERUSER; supersedes the `laplace_priv` SECURITY DEFINER wrapper pattern

## Status

**Accepted** — 2026-05-23
**Amended** — 2026-05-24: cluster-runtime framing superseded. This ADR originally described the substrate running against the **system** PG cluster (`/usr/lib/postgresql/18`) with extensions staged under `/opt/laplace/{lib,share}/postgresql/18` via `extension_control_path` + `dynamic_library_path` GUCs. Per Anthony's 2026-05-24 architectural clarification, that framing was an interim model. **The substrate's runtime cluster is the custom-built PG 18.3 + PostGIS 3.6.3 at `/opt/laplace/pgsql-18/`**, built from the `external/postgresql/` + `external/postgis/` git submodules with the Intel toolchain per [ADR 0028](0028-custom-built-pg-postgis-intel.md) + [ADR 0033](0033-all-deps-as-submodules.md) + [ADR 0038](0038-unified-deps-cmake-pipeline-gcc-toolchain.md). The system `postgresql-18` apt package may exist on the host for unrelated reasons but is NOT the runtime. The substrate-owns-its-runtime invariant (because the substrate IS the product, not "an app that lives inside someone else's PG") motivates the custom-build path. Bootstrap commit `67c3808 feat(bootstrap): substrate runs against /opt/laplace/pgsql-18 cluster — system PG retired` reflects the current direction. The `laplace_admin` SUPERUSER + no-SECURITY-DEFINER-wrapper decision below is unchanged — it applies to whichever cluster the substrate runs against. What's superseded is the *which-cluster* part of the original "Decision" section, not the role-privilege part. [OPERATIONS.md](../../OPERATIONS.md) has been updated to reflect the corrected cluster-runtime framing.

Supersedes the SECURITY DEFINER wrapper architecture introduced piecemeal in [ADR 0019](0019-laplace-runner-system-account.md), [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md), [ADR 0025](0025-pg-extension-modularization.md), and [ADR 0027](0027-separation-of-concerns-invariants.md). Those ADRs are amended (not retracted) — the substrate-owns-schema invariant in 0023 stands; only the role-privilege and CREATE-EXTENSION-orchestration mechanisms change.

## Context

Prior architecture (commit history through `bf89939`):

- `laplace_admin` was created with `CREATEDB CREATEROLE` but **not** `SUPERUSER` (ADR 0019).
- A `laplace_priv` schema (owned by `postgres`) held SECURITY DEFINER wrapper functions `install_extension(text)` + `drop_extension(text)` with a hardcoded allowlist. The wrappers existed because PostGIS is not a trusted extension — `CREATE EXTENSION postgis` requires `SUPERUSER`, which `laplace_admin` did not have.
- DbUp's initial migration called `SELECT laplace_priv.install_extension('postgis')` to clear the privilege gate via SECURITY DEFINER.
- After commit `b94bde1` (transient), a custom `template_laplace` database was added so `just db-nuke` + `just db-up` could survive per-DB state loss without a Layer-0 re-bootstrap. The template approach worked but added a second piece of bespoke machinery.

Empirical pain points that surfaced this iteration:

1. **`just db-nuke` broke the substrate** until template_laplace was added. The `laplace_priv` schema lived inside `laplace`, so dropping the DB destroyed the wrapper, and `laplace_admin` could not reinstall it (the only role that could was `postgres` via sudo).
2. **Trusted-extension contained-object ownership** stays with the bootstrap superuser unless the install script explicitly issues `ALTER ... OWNER TO @extowner@` (per PG docs + verified against `src/backend/commands/extension.c`). `laplace_substrate.control` declares `trusted = true`, so its `entities`, `physicalities`, `attestations` tables ended up owned by `postgres` — `laplace_admin` got `permission denied for table entities` even though it owned the schema. Would have required `ALTER TABLE ... OWNER TO @extowner@` in every `.sql.in` module.
3. **The wrapper allowlist was project-local policy compiled into a runtime SQL function.** Adding a new allowed extension required a sudo re-bootstrap. Bad ergonomics for substrate growth.

Web-search confirms the standard pattern (see References):

- PostGIS requires `SUPERUSER` because it is not trusted; this is documented PG behavior, not a bug.
- For a **dedicated cluster** (single-operator substrate dev box, the Laplace deployment shape), the standard pattern is for the operator role to be a `SUPERUSER`. AWS RDS uses `rds_superuser`; GCP Cloud SQL uses `cloudsqlsuperuser`; Severalnines / PG community refer to this as the "dedicated DB owner" pattern.
- The `pgextwlist` extension exists for shared-hosting situations where the operator role cannot be a superuser. We are not shared hosting.

The wrapper pattern was defense-in-depth designed for a multi-tenant shape we do not have. The single-user dev box already trusts the OS user `ahart` (who has full sudo on the host); restricting the PG role they peer-auth to provided no actual isolation, only architectural complexity.

## Decision

`laplace_admin` is a **PostgreSQL `SUPERUSER`**.

- `bootstrap_pg_roles` (in `scripts/bootstrap-laplace-runner.sh`) creates / alters `laplace_admin` `WITH LOGIN SUPERUSER CREATEDB CREATEROLE`.
- The `laplace_priv` schema, `install_extension(text)` wrapper, and `drop_extension(text)` wrapper are **removed**. The bootstrap actively cleans up any legacy `laplace_priv` schema and `template_laplace` database left by prior wrapper-pattern iterations.
- The DbUp initial migration uses plain `CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS laplace_geom; CREATE EXTENSION IF NOT EXISTS laplace_substrate;` — `laplace_admin` is `SUPERUSER` so all three install cleanly and own their contained objects.
- `laplace_app` and `laplace_readonly` remain ordinary roles. The migration grants them `USAGE` on the `laplace` schema + appropriate table privileges. Future least-privilege application access is not affected by `laplace_admin` being a superuser.
- `CMAKE_INSTALL_PREFIX=/opt/laplace` (laplace-runner-owned, mode 2775) plus `extension_control_path = '/opt/laplace/share/postgresql/18:$system'` + `dynamic_library_path = '$libdir:/opt/laplace/lib/postgresql/18'` in `postgresql.conf` keep the **filesystem install path** sudo-free regardless of role privileges. That part of the architecture (set up by `bootstrap_engine_lib_path` + `bootstrap_pg_extension_paths`) is unchanged by this ADR.

## Consequences

- **Net −250 lines** across `bootstrap-laplace-runner.sh`, `db/migrations/20260521000000_initial_extensions.sql`, and `app/Laplace.Migrations/Program.cs` (the SECURITY DEFINER wrappers + the custom-template-database machinery + the `EnsureDatabaseFromTemplate` C# helper all delete).
- **`just db-nuke && just db-up` works repeatedly without sudo** — `laplace_admin` drops, recreates, and reinstalls everything itself.
- **Substrate tables end up owned by `laplace_admin`** without `ALTER OWNER` workarounds. `SELECT count(*) FROM laplace.entities` from a `laplace_admin` session returns `46` immediately after `db-up`.
- **CI does not need a wrapper allowlist.** Adding a new extension is a single `CREATE EXTENSION` line in a DbUp migration; no sudo re-bootstrap required.
- **Security posture explicitly downscoped** to "dedicated substrate cluster, single OS-trusted operator." Re-introducing role separation for a future multi-tenant deployment would need to bring back something like the wrapper pattern OR move to per-tenant clusters; this ADR records that the wrapper approach was tried and abandoned for this scope.
- **Forward path**: if the substrate later needs least-privilege application roles for read-only or write-only workloads, `laplace_app` / `laplace_readonly` already exist and the migration's `ALTER DEFAULT PRIVILEGES` block extends to future tables.

## Alternatives considered

- **Keep `laplace_admin` non-superuser + custom template database.** Tried (commit `b94bde1`); worked but added a second piece of machinery beyond the wrapper, and contained-object ownership still required `ALTER OWNER` in the extension SQL. Strictly more complexity for a security model we don't need.
- **Keep `laplace_admin` non-superuser + add `ALTER TABLE ... OWNER TO @extowner@` to every substrate `.sql.in` module.** Would solve the ownership issue but leaves the `laplace_priv` wrapper requirement and its db-nuke fragility unsolved.
- **Use `pgextwlist`.** Designed for shared hosting providers that explicitly want to grant a curated extension set without superuser. Wrong tool for a dedicated cluster.
- **Per-database "deployer" superuser distinct from `laplace_admin`.** Would require either a second peer-auth mapping or a password-based connection just for migrations. Adds operational surface (two passwords / two roles to keep in sync) for no real isolation on a single-operator host.

## Amends to prior ADRs

- **[ADR 0019](0019-laplace-runner-system-account.md)** — "Permissions" section: `laplace_admin` is `SUPERUSER` (not `CREATEDB + CREATEROLE` only). Sudoers rule expanded from `make install*` to `cmake --install *` + legacy patterns per ADR 0032 (already in script).
- **[ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md)** — Decision section: DbUp migrations call plain `CREATE EXTENSION IF NOT EXISTS ...` directly. The "via `laplace_priv` wrapper" mechanism is removed. The substrate-owns-schema invariant stands.
- **[ADR 0025](0025-pg-extension-modularization.md)** — Step list: `SELECT laplace_priv.install_extension(...)` references in the migration block become plain `CREATE EXTENSION`. The "Bootstrap allowlist update" bullet is no longer applicable.
- **[ADR 0027](0027-separation-of-concerns-invariants.md)** — `SQL / DbUp migrations` row: drop the "via the `laplace_priv` wrapper" qualifier on CREATE EXTENSION orchestration.

## References

- PostgreSQL Documentation — [CREATE EXTENSION](https://www.postgresql.org/docs/current/sql-createextension.html): trusted-extension semantics + contained-object ownership.
- RustProof Labs — [Permissions required for PostGIS (2021)](https://blog.rustprooflabs.com/2021/12/postgis-permissions-required): PostGIS is not trusted, requires SUPERUSER.
- AWS RDS Documentation — [Managing spatial data with the PostGIS extension](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/Appendix.PostgreSQL.CommonDBATasks.PostGIS.html): `rds_superuser` pattern.
- Severalnines — [An Overview of Trusted Extensions in PostgreSQL 13](https://severalnines.com/blog/overview-trusted-extensions-postgresql-13/): trusted-vs-not extension list + ownership transfer semantics.
- PG 18 source `src/backend/commands/extension.c` — `execute_extension_script` (`switch_to_superuser = extension_is_trusted(control)`), `@extowner@` macro substitution at line 1354.
- Implementation: commit `36f0ed4` ("laplace_admin = SUPERUSER — collapse wrapper/template/owner-transfer bandaids").
- Reverts commit `b94bde1` (template_laplace approach) and the SECURITY DEFINER wrapper code added under [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md).
