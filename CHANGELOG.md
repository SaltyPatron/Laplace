# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Changelog entries are generated from [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) on `main` via [release-please](https://github.com/googleapis/release-please).

## 1.0.0 (2026-05-25)


### Features

* **app:** land Chunk C P/Invoke bindings for Chunk A+B engine primitives ([bbf8819](https://github.com/SaltyPatron/Laplace/commit/bbf88190d231e28db10de8f07aeebb4eef88bba2))
* **bootstrap:** substrate runs against /opt/laplace/pgsql-18 cluster — system PG retired ([67c3808](https://github.com/SaltyPatron/Laplace/commit/67c3808cef39709aeedf109d9edefb9985f685ab))
* **crud,extension:** land Chunk D — Laplace.SubstrateCRUD + entities_exist_bitmap SRF ([282dedc](https://github.com/SaltyPatron/Laplace/commit/282dedcf094b49b199878c4cdc7414f5351ef237))
* **decomposers,ingestion:** land Chunks E + F — Decomposers.Abstractions + Containers.Abstractions + Ingestion (Framework Epic [#232](https://github.com/SaltyPatron/Laplace/issues/232) complete) ([fae1bd5](https://github.com/SaltyPatron/Laplace/commit/fae1bd51f5e17cb410a319aefc5eb166efcc5f45))
* **engine,app:** land Story A.2 — full UAX[#29](https://github.com/SaltyPatron/Laplace/issues/29) + NFC TextDecomposer against UCD 18.0.0 ([4180de2](https://github.com/SaltyPatron/Laplace/commit/4180de2e5b81fa71c3c58436df78498fdd257fe0))
* **engine:** land Chunk A primitives + Chunk B sparsity streaming variants ([e82e068](https://github.com/SaltyPatron/Laplace/commit/e82e068be5b7cfe67bb32b8425ce632f2e34262c))
* **geom,engine:** foundation for ST_*_4d wrappers — liblwgeom + Frechet + Hausdorff ([eb18a2a](https://github.com/SaltyPatron/Laplace/commit/eb18a2a194853280ae3573ef703f8e81edef8c81))
* **geom:** Chunk 2 ST_*_4d wrappers — 8 PG functions + pg_regress + bootstrap dep-path fix ([d58776d](https://github.com/SaltyPatron/Laplace/commit/d58776d5eaaab943733dbf5522878232ae21aabd))


### Bug Fixes

* **bootstrap:** hba+ident outside 0700 data dir — group-editable, no sudo, no psql bandaids ([523bde9](https://github.com/SaltyPatron/Laplace/commit/523bde936d5e9bf9868561de79f32a5ce26334eb))
* **bootstrap:** laplace-postgresql.service Type=simple (custom PG lacks --with-systemd) ([c3b2097](https://github.com/SaltyPatron/Laplace/commit/c3b20970f5f158e5be93d3ac57c67a7ba2135652))
* **bootstrap:** race-tolerate chown/chmod sweep against /opt/laplace ([f3e8b2b](https://github.com/SaltyPatron/Laplace/commit/f3e8b2b6de7175a4b213c5baca1f2f9e37fdaa47))
* **bootstrap:** re-runnable /opt/laplace perm normalization — kills the recurring cmake-install EACCES class ([81afdee](https://github.com/SaltyPatron/Laplace/commit/81afdee3f70cc9b65b9437260be666c6d6eb5aba))
* **bootstrap:** set -e trap in bootstrap_disable_system_postgresql ([3147966](https://github.com/SaltyPatron/Laplace/commit/3147966fd75dcef3611ff3de376c6fd19af457a7))
* **bootstrap:** substrate pg_hba.conf — managed block as full file, not append ([d54b284](https://github.com/SaltyPatron/Laplace/commit/d54b284354856bcd9a8b0fb8d7d18fd08a63a963))
* **bootstrap:** system PG instance detection + group-readable substrate logs ([d16b9cc](https://github.com/SaltyPatron/Laplace/commit/d16b9cc7871afdc2ebdb6c853dddcbe5c062c336))
* **engine:** P/Invoke string-marshaller frees .rodata pointer ([558fe24](https://github.com/SaltyPatron/Laplace/commit/558fe24ddcf54e12c73bb0eaf84b5acd2973bac2))
* **install:** pre-wipe via install(CODE) — install is ownership-agnostic, no sudo, no fix-perms ([208f075](https://github.com/SaltyPatron/Laplace/commit/208f075f659ab51c9527093db87e9f730f95ec71))
* **install:** repo-wide group-writable install perms — install is permanently ownership-agnostic ([c9ff8a1](https://github.com/SaltyPatron/Laplace/commit/c9ff8a10cce4e9ba4bf668386087fa46027a4358))

## [Unreleased]

### Added

- Project framework: CLAUDE.md, AGENTS.md, README.md, GLOSSARY.md, RULES.md, STANDARDS.md, DESIGN.md, OPERATIONS.md, CONTRIBUTING.md, Justfile
- Specialized Claude Code agents (`.claude/agents/`): substrate-architect, postgres-extension, cpp-performance, type-taxonomy, ingestion-pipeline, verification, conventional-ai-skeptic
- GitHub Actions CI/CD: `ci.yml` (hosted PR validation), `integration.yml` (self-hosted on `hart-server`), `release-please.yml`
- GitHub Project Board #1 (Laplace Roadmap)
- Engine skeleton (CMake + Ninja, Eigen + Spectra + BLAKE3 via FetchContent)
- Extension skeleton (PGXS Makefile, non-relocatable control file, `laplace--0.1.0.sql`, C source)
- C# app skeleton (.NET 10 .slnx + Laplace.Engine class library with P/Invoke stub)
- C# Layer-1 migrations runner (`app/Laplace.Migrations`) — DbUp + Npgsql with `up`/`status`/`reset`/`nuke` modes
- Scripts: `check-prereqs.sh` (real), `bootstrap-laplace-runner.sh` (Layer-0 root setup with idempotent + resettable modes), `setup-host.sh` (one-shot Layer 0 + Layer 1 wrapper), other operational stubs
- Issue + PR templates
- LICENSE (proprietary — Anthony Hart sole inventor)
- SDLC framework: this CHANGELOG, ADR directory under `docs/adr/` (37 ADRs covering every architectural decision through this stage), release-please workflow, Conventional Commits enforcement, pre-commit hooks
- Definition of Done in CONTRIBUTING.md (Code / Documentation / Migrations / Commits / Retros sections — no partials)

- **Architectural overhaul (ADRs 0024–0032):**
  - Engine modularization: 3 shared libraries (`liblaplace_core`, `liblaplace_dynamics`, `liblaplace_synthesis`) per ADR 0024
  - PG extension modularization: `laplace_geom` + `laplace_substrate` per ADR 0025 (refines ADR 0023)
  - C# project structure: per-engine-lib bindings + functional + plugin projects per ADR 0026
  - Separation of concerns invariants codified into RULES.md R16 per ADR 0027
  - Custom-built PG 18 + PostGIS 3.6.3 with Intel toolchain locked as prerequisite per ADR 0028 (amended 2026-05-22)
  - Custom indexing strategy: 5 substrate-shaped opclasses (`laplace_btree_hash128_ops`, `laplace_gist_s3_ops`, `laplace_sp_trajectory_ops`, `laplace_brin_tier_ops`, Glicko-2-aware GIST stats) per ADR 0029 (amends RULES.md R1)
  - MKL/Eigen/Spectra/TBB integration regime + MKL_CBWR determinism per ADR 0030
  - Custom Access Method (perf-cache-backed) tracked as post-v0.1.0 research spike per ADR 0031
  - Unified CMake build pipeline (Path B) retiring PGXS per ADR 0032
- **GitHub issue refactor:** 5 new Epics (A, B, B′, D + Spike S1) + 50 new Stories created and wired to milestone v0.1.0 with module/area/priority labels; 5 opclass Stories slotted into existing Chunks 1, 2, 3, 5
- **End-to-end CI green** on commit `ab8f62b` — capabilities → build → db-ensure (DbUp) → smoke-test all passing
- **Concept-capture ADRs (0035-0037):** prompt ingestion + compiled cascade traversal, arena/source-trust consensus, layered seed ingestion + model-codec fidelity.
- **Schema reorganization ADRs (0039-0040, 2026-05-23):**
  - **ADR 0039** — entity is identity, physicality is representation. `entities` stripped to `id` + `tier` + `type_id` + light metadata; `physicalities` gains its own `id` PK, `kind` column (CONTENT / BUILDING_BLOCK / PROJECTION), and the trajectory column; one entity → many physicalities; column-role is `id` not `hash` (value IS a BLAKE3-128 hash).
  - **ADR 0040** — multi-modal entity types + universal T0 + lossless canonicalization. Entities carry `type_id` (Text / Pixel / Patch / Image / Audio_Frame / Model_Recipe / WordNet_Synset / ...); every modality's tier ladder bottoms at universal Unicode codepoints; canonical content is lossless per type; cross-format equivalence is attestation, not identity collapse; AI models aren't a special case (tokens are text entities with BPE markers stripped via canonicalization; tensor calculations are typed attestations of a fixed-vocabulary kind).
- **Engine core math primitives (Chunk 1 Stories 1.1–1.11, 2026-05-23):**
  - `math4d` (was Chunk 1 Stories 1.1/1.2/1.3 — coord4d) — operates on raw `double[4]` per R22 (no parallel datatype; POINT4D-shaped memory): `dot`, `norm`, `radius_from_origin`, `distance`, `distance_sq`, `angular_distance`, `add`/`sub`/`scale`, `centroid`.
  - `hash128` — BLAKE3-128 (via `external/blake3/` submodule per ADR 0033): `hash128_blake3`, `hash128_merkle` (tier-prefixed domain-separated composition), `hash128_compare` (byte-lex == memcmp), `hash128_equals`, `hash128_zero`.
  - `hilbert4d` — Skilling 2004 4D Hilbert encoder + decoder. **Bug found + fixed in commit 7ea3535** against the canonical galtay/hilbertcurve reference: dim-interleave must put X[0] at the HIGH bits of the index (Skilling's algorithm uses X[0] as a pivot and only produces a valid Hilbert curve when that convention holds). Definitive correctness via consecutive-cells test: 65535 consecutive indices map to single-cell-adjacent cells (>99.9%).
  - `mantissa` — 212-bit XYZ=entity_id + M=metadata layout per ADR 0012 v2. Full BLAKE3-128 entity_id (no truncation); fixed-exponent FP64 components in `[1, 2) ∪ (-2, -1]` so vertices are PG-geometry-valid; 16-bit ordinal + 16-bit run_length + 52 reserved flag bits.
  - `super_fibonacci` (Story 3.3) — Marc Alexa CVPR 2022 quasi-uniform unit quaternions on S³. Algebraically exact unit-norm. Verified at full Unicode codepoint scale (1.1M quaternions in ~67ms).
  - 54 engine-core unit tests; all pass.
- **Engine dynamics math primitives (Chunk 6 Stories 6.6/6.7/6.8, 2026-05-23):**
  - `eigenmaps` — Laplacian eigenmaps (Belkin & Niyogi 2003) via Spectra's `SymEigsShiftSolver` (sparse symmetric shift-invert + Lanczos) on regularized graph Laplacian. Ring-manifold recovery verified (60 points on 1D ring in 10D → 2D embedding with ≥90% neighbor-preservation).
  - `gram_schmidt` — via Eigen's `HouseholderQR` (oneMKL `dgeqrf`/`dorgqr` backed). Backward-stable; handles ill-conditioned bases that break classical/modified GS.
  - `procrustes` — Schönemann 1966 + Umeyama 1991 with rectangular-case correction (classic Umeyama scale formula assumes square R; corrected to use `‖Pc·R‖²` in the denominator). Eigen `JacobiSVD` (oneMKL `dgesdd` backed).
  - 12 dynamics unit tests; all pass.
- **GitHub issue tracking refresh:** 8 per-source IDecomposer specs opened (#183-#191): UnicodeUCDSource, WordNetSource, OMWSource, UDTreebankSource, WiktionarySource, TatoebaSource, Atomic2020Source, ConceptNetSource, TransformerModelSource. Architectural snapshot discussion #192.

### Changed

- Hash function: switched from XXH3-128 to BLAKE3-128 (truncated). Native SIMD; cryptographic strength; raw bytes end to end (no hex / no string casts). See [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md).
- CI runner identity: refactored from `ahart` (interactive user) to dedicated `laplace-runner` system account with bounded sudo + peer-auth into PG as `laplace_admin`. See [ADR 0019](docs/adr/0019-laplace-runner-system-account.md).
- Migration framework: DbUp + Npgsql replaces the initially-drafted dbmate. .NET-native, no separate toolchain. See [ADR 0021](docs/adr/0021-dbup-for-migrations.md).
- Database/extension ownership: the substrate's PG extensions own their schemas, tables, types, functions; DbUp narrows to extension-lifecycle orchestration only. See [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md).
- Extension default version aligned to `0.1.0` across `laplace.control`, `laplace--0.1.0.sql`, the C `laplace_version()`, and the CI smoke test (was inconsistent: 1.0.0 in some places, 0.1.0 in others).
- `extension/laplace.control`: `relocatable = false`, explicit `schema = 'laplace'`, `requires = 'postgis'`, `trusted = true` (so laplace_admin can install — with the default `superuser = true` letting PG's trusted-elevation actually fire; setting `superuser = false` AND `trusted = true` is incompatible and was a session-mid mistake).
- `db/migrations/20260521000000_initial_extensions.sql`: postgis installed via `laplace_priv.install_extension` SECURITY DEFINER wrapper; laplace installed directly as laplace_admin (the `trusted = true` flag makes the install script run as bootstrap-superuser, so LANGUAGE C functions work). Self-heal step drops mis-owned extension via existing `drop_extension` wrapper.
- Bootstrap script: renamed from `setup-laplace-runner.sh` to `bootstrap-laplace-runner.sh`; gained `bootstrap` / `status` / `reset` modes; idempotent at every step (system account, runner, PG roles, peer auth, sudoers, postgis-install-as-postgres).
- Justfile: replaced `create-db` / `apply-schema` with `db-up` / `db-status` / `db-reset` / `db-nuke` / `migrate-new`; added `bootstrap` / `bootstrap-status` / `bootstrap-reset` wrappers; `setup-host` shortcuts.
- `integration.yml`: `db-ensure` job installs the extensions + applies DbUp migrations against the agent-managed `laplace` database; smoke test verifies the deployed substrate using `psql -U laplace_admin` (peer auth).
- `RULES.md` R1: custom GIST/SP-GiST/BRIN opclasses ARE permitted with ADR justification (was a blanket prohibition; amended by ADR 0029).
- `RULES.md` R14: references all 3 engine libraries + 2 PG extensions (was singular "the C/C++ engine library").
- `RULES.md` R15: BLAKE3 replaces libxxhash on approved list; pgvector family explicitly banned.
- New `RULES.md` R16: separation-of-concerns invariants codified.
- New `RULES.md` R19-R21: prompt is ingestion and cascade is compiled; arena/source-trust semantics are mandatory; AI model ingest is a codec with source-scoped round-trip fidelity as a v0.1 proof target.
- Canonical docs and agent instructions refreshed to preserve prompt ingestion, compiled cascade, exact-zero sparse emission, source trust/effective mu, truth-clustering, seed-source order, and stock/substrate/export round-trip comparison.
- **ADR 0012 revised (2026-05-23):** mantissa-packing format. Original 8-tier / 12-position / 60-truncated-hash layout superseded by 212-bit XYZ=entity_id + M=metadata layout. The original layout conflated content-recording trajectories (metadata containers) with the entity-table canonical-coord layer (where 4D position WAS load-bearing under the original schema) and truncated the entity ID — violating STANDARDS.md ID discipline (no truncation). Revised: trajectory now lives on `physicalities` (specifically CONTENT-kind), not `entities`; vertices carry full 128-bit `entity_id`.
- **GLOSSARY rewritten (2026-05-23):** Entity/Physicality/Attestation/Trajectory entries reframed for the entity-as-pure-identity model. New entries: Entity Type, Universal T0, Canonicalization, Attestation Kind, Attestation Tuple Shape, Data Class (app/substrate/user). Per-source decomposition mini-specs added (WordNet, OMW, UD, Wiktionary, Tatoeba, Atomic2020, ConceptNet, TransformerModelSource).
- **STANDARDS.md** — "Hash discipline" renamed to "ID discipline" (column-role is `id`; value IS a BLAKE3-128 hash); added "Canonicalization discipline" (lossless per type) + "Attestation kind discipline" (parameter-free kind entities; fixed vocabularies per modality / per architecture family; forbids synthesizing per-(layer, head, position) kind IDs via hash-concatenation of architectural metadata).
- **DESIGN.md §I (Schema)** rewritten: entities = identity + tier + type_id; physicalities expanded with id PK + kind + trajectory. §V (Indexing) regenerated for the new schema. §IX (three-phase) updated with bootstrap responsibilities (substrate-canonical source + type entities + kind entities).

### Fixed

- Bootstrap script: `svc.sh install` made idempotent (skips when systemd unit already exists); `register` step uses `.runner` file as canonical idempotency check; sudoers verification probe uses `sudo -ln` (list-mode) instead of running a non-matching command.
- PG peer-auth probe in CI + bootstrap: explicit `-U laplace_admin` (was defaulting to OS user as PG role, which is `laplace-runner` and isn't a PG role).
- Postgis package mismatch on hart-server: `update-alternatives --auto postgresql-18-postgis.control` restored the symlink chain after `postgis.control` had been pinned to a stale `3.7.0dev` from a prior install.
- `laplace_priv.install_extension` wrapper search_path reordered from `pg_catalog, public` → `public, pg_catalog` so postgis's `CREATE TYPE geometry_dump` (no schema qualifier) resolves to `public` instead of `pg_catalog`.
- Orphan `laplace` schema on hart-server (owned by `postgres` from a transitional commit's pre-create step) — manually dropped via `sudo -u postgres psql -d laplace -c "DROP SCHEMA laplace CASCADE"`; future installs use the `trusted = true` path which creates the schema with `laplace_admin` as owner.
- **`hilbert4d` dim-interleave bug (commit 7ea3535):** Skilling 2004 transcription originally placed X[0] at the LOW bits of the 128-bit index. Encoder/decoder were inverse so the round-trip test passed, but the output was a self-consistent NON-Hilbert curve (consecutive indices ≠ adjacent cells). Found via the new ConsecutiveIndicesProduceAdjacentCells correctness test, fixed against the canonical galtay/hilbertcurve Python reference (Skilling's algorithm uses X[0] as a pivot and requires X[0] at the HIGH bits of the index for the curve to be valid).
- **Procrustes rectangular-case Umeyama scale (commit 275ba22):** classic Umeyama formula `s = Σ.sum() / ‖Pc‖²` assumes square R (d_src == d_tgt) where `‖Pc·R‖² = ‖Pc‖²` holds. For rectangular R (d_src ≠ d_tgt) the equality breaks and the formula gives the wrong scale. Corrected to use `‖Pc·R‖²` in the denominator.

### Decisions

See [docs/adr/](docs/adr/) for architectural decision records (current count: 37, with explicit supersedence chains for 0003→0015 and 0014→0019, narrowed scope from 0021→0023, expanded from 0023→0025 with PG extension split, ADR 0029 amends RULES.md R1, ADR 0032 locks ADR 0028 as prerequisite, ADRs 0035-0037 lock prompt/cascade, source-trust, and model-codec semantics).

[Unreleased]: https://github.com/SaltyPatron/Laplace/compare/HEAD...HEAD
