# ADR 0025: PG extension modularization — `laplace_geom` + `laplace_substrate`

## Status

**Accepted** — 2026-05-21

Narrows the implicit "one laplace extension" assumption in earlier ADRs (especially [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md)).

## Context

[ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md) decided the `laplace` extension owns the substrate's schema (tables, types, opclasses, functions). That extension was a single PG extension named `laplace`.

But two structurally different kinds of capability were both planned for that single extension:

1. **General-purpose 4D-PostGIS additions** — `ST_*_4d` family (distance, dwithin, centroid, frechet, hausdorff, radius_origin), Hilbert curve encoder/decoder for `[-1,1]^4`, mantissa pack/unpack helpers, a `hash128` type with B-tree + GIST opclasses. Any caller wanting 4D-extended PostGIS could use these without buying into the substrate's identity model.
2. **Substrate-domain schema and behavior** — the three core tables (entities / physicalities / attestations), the typed attestation kind hierarchy, GIST opclasses on entity geometry, BRIN opclasses on tier-clustered tables, SP-GiST opclasses on trajectory tier-prefixes (per [ADR 0029](0029-custom-indexing-strategy.md)), the Glicko-2 aggregate, cascade-tier SRFs.

Bundling these into one extension means anyone who wanted just the 4D-PostGIS primitives would also have to install the substrate tables they don't need. It also means version cadences are entangled: a bug fix to `ST_frechet_4d` (general-purpose) requires bumping the extension version that owns the substrate tables (domain-specific), forcing all substrate consumers to recompile/upgrade.

## Decision

Split into **two PG extensions** with an explicit `requires` chain:

### `laplace_geom`

**Purpose.** General-purpose 4D extension of PostGIS. Reusable for any 4D-PostGIS work. No substrate-specific dependencies; no substrate tables.

**Contents.**
- `ST_*_4d` family of functions: `ST_distance_4d`, `ST_dwithin_4d`, `ST_centroid_4d`, `ST_frechet_4d`, `ST_hausdorff_4d`, `ST_radius_origin`, `ST_length_4d`
- `hash128` PG type registered with `bytea(16)` storage
- `laplace_btree_hash128_ops` custom B-tree opclass for `hash128` ([ADR 0029](0029-custom-indexing-strategy.md))
- `laplace_gist_s3_ops` custom GIST opclass for S³-aware geometry indexes ([ADR 0029](0029-custom-indexing-strategy.md))
- `hilbert128` type + encoder/decoder
- `mantissa_pack` / `mantissa_unpack` SQL helpers (binding to engine kernels)

**Control file.** `relocatable = false` (uses `@extschema@` references), `schema = 'laplace_geom'`, `requires = 'postgis'`, `superuser = false`.

**Linkage.** Loads `liblaplace_core.so` (per [ADR 0024](0024-engine-modularization.md)) via `module_pathname`.

### `laplace_substrate`

**Purpose.** The substrate proper. Owns the three core tables, the typed attestation hierarchy, cascade SRFs, the Glicko-2 aggregate.

**Contents.**
- Schema: `entities`, `physicalities`, `attestations` tables (per DESIGN.md Section I)
- Composite types: attestation kind hierarchy, physicality bundle types
- GIST opclasses on entity/trajectory geometry columns (extending `laplace_gist_s3_ops` from `laplace_geom`)
- `laplace_sp_trajectory_ops` SP-GiST opclass for trajectory tier-prefix indexes ([ADR 0029](0029-custom-indexing-strategy.md))
- `laplace_brin_tier_ops` BRIN opclass for tier-clustered tables ([ADR 0029](0029-custom-indexing-strategy.md))
- Glicko-2 aggregate `laplace_glicko2_accumulate` (the only SQL-side computation per [RULES.md R6](../../RULES.md))
- Cascade SRFs (`laplace_astar_path`, `laplace_cascade_descend`)
- `pg_extension_config_dump('laplace_substrate.entities', '')` markers on the three core tables so substrate data survives `pg_dump`

**Control file.** `relocatable = false`, `schema = 'laplace'`, `requires = 'laplace_geom, postgis'`, `superuser = false`.

**Linkage.** Loads `liblaplace_core.so`.

### Per-extension upgrade chain

Each extension follows its own version trajectory and ships its own upgrade scripts:

- `extension/laplace_geom/laplace_geom--0.1.0.sql` (initial)
- `extension/laplace_geom/laplace_geom--0.1.0--0.2.0.sql` (upgrade)
- `extension/laplace_substrate/laplace_substrate--0.1.0.sql` (initial)
- `extension/laplace_substrate/laplace_substrate--0.1.0--0.2.0.sql` (upgrade)

`ALTER EXTENSION laplace_geom UPDATE` and `ALTER EXTENSION laplace_substrate UPDATE` are independent operations.

## Consequences

- **`laplace_geom` is potentially open-sourceable later.** It's a general-purpose 4D-PostGIS extension. Any researcher / scientific-viz / ML-feature-space user could install it without taking on the substrate's opinions. Drawing the boundary now means its API doesn't drift to depend on substrate-specific assumptions.
- **Substrate version evolves independently** from 4D-PostGIS primitives. A new attestation kind or cascade heuristic bumps `laplace_substrate`'s version without churning `laplace_geom`.
- **PG's `requires` chain** handles installation order. `CREATE EXTENSION laplace_substrate` automatically installs `laplace_geom` and `postgis` first.
- **DbUp migration calls `CREATE EXTENSION` directly** — `CREATE EXTENSION IF NOT EXISTS laplace_geom; CREATE EXTENSION IF NOT EXISTS laplace_substrate;`. `laplace_admin` is `SUPERUSER` per [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md), so no SECURITY DEFINER wrapper is needed. *(Pre-ADR-0045 this used `SELECT laplace_priv.install_extension(...)` — see ADR 0045 for why that was collapsed.)*
- **Folder restructure required.** `extension/` becomes `extension/{laplace_geom,laplace_substrate}/`, each with its own `Makefile` (PGXS), `.control`, `--A.B.C.sql`, `src/`, `tests/` subdirs.
- **CI build/install steps double up** — `sudo make install` runs in both extension subdirs. Bounded sudoers entry (`make install*`, per [ADR 0019](0019-laplace-runner-system-account.md)) already covers this.
- ~~**Bootstrap allowlist update** — `laplace_priv.install_extension` allowlist gets `'laplace_geom'` and `'laplace_substrate'`~~. *No longer applicable — wrapper + allowlist removed by [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md). `laplace_admin` as `SUPERUSER` can `CREATE EXTENSION` any installed extension directly.*

## Alternatives considered

- **Status quo: one `laplace` extension.** Rejected per the context analysis — entangles version cadences, makes `laplace_geom` a non-discrete artifact, hides the natural seam between general-purpose and substrate-domain code.
- **Three extensions (split `laplace_substrate` further into `laplace_substrate_tables` + `laplace_substrate_cascade`).** Rejected — substrate tables and cascade SRFs are tightly coupled (cascade walks attestation rows directly); separating them creates a fictional boundary.
- **`laplace_geom` as a separately-named, separately-released project.** Considered. Decided to keep it in the Laplace monorepo for now since it's not yet generally useful; could be lifted out into its own repo later if/when interest arises.

## References

- [PostgreSQL 18 — Packaging Related Objects into an Extension](https://www.postgresql.org/docs/current/extend-extensions.html)
- [PostgreSQL 18 — ALTER EXTENSION UPDATE](https://www.postgresql.org/docs/current/sql-alterextension.html)
- [PostGIS — Extension structure (postgis / postgis_topology / postgis_raster / postgis_sfcgal)](https://postgis.net/docs/postgis_installation.html) — established precedent for splitting a project across multiple PG extensions
- ADR 0023 (extension owns schema; DbUp orchestrates) — this ADR refines that one
- [ADR 0024](0024-engine-modularization.md) — engine modularization (each PG extension loads `liblaplace_core`)
- [ADR 0029](0029-custom-indexing-strategy.md) — custom opclasses, distributed across both extensions
- `extension/`, `extension/laplace_geom/`, `extension/laplace_substrate/`
- `db/migrations/20260521000000_initial_extensions.sql` — updated to install both extensions via the wrapper
