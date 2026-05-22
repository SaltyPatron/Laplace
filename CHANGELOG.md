# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Changelog entries are generated from [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) on `main` via [release-please](https://github.com/googleapis/release-please).

## [Unreleased]

### Added

- Project framework: CLAUDE.md, AGENTS.md, README.md, GLOSSARY.md, RULES.md, STANDARDS.md, DESIGN.md, OPERATIONS.md, CONTRIBUTING.md, Justfile
- Specialized Claude Code agents (`.claude/agents/`): substrate-architect, postgres-extension, cpp-performance, type-taxonomy, ingestion-pipeline, verification, conventional-ai-skeptic
- Agent territory (`.agent/`): plan.md, STATE.md, decisions.md, blockers.md, retros/ (with chunk-0 retro template + first retro)
- GitHub Actions CI/CD: `ci.yml` (hosted PR validation), `integration.yml` (self-hosted on `hart-server`), `release-please.yml`
- GitHub Project Board #1 (Laplace Roadmap)
- Engine skeleton (CMake + Ninja, Eigen + Spectra + BLAKE3 via FetchContent)
- Extension skeleton (PGXS Makefile, non-relocatable control file, `laplace--0.1.0.sql`, C source)
- C# app skeleton (.NET 10 .slnx + Laplace.Engine class library with P/Invoke stub)
- C# Layer-1 migrations runner (`app/Laplace.Migrations`) — DbUp + Npgsql with `up`/`status`/`reset`/`nuke` modes
- Scripts: `check-prereqs.sh` (real), `bootstrap-laplace-runner.sh` (Layer-0 root setup with idempotent + resettable modes), `setup-host.sh` (one-shot Layer 0 + Layer 1 wrapper), other operational stubs
- Issue + PR templates
- LICENSE (proprietary — Anthony Hart sole inventor)
- SDLC framework: this CHANGELOG, ADR directory under `docs/adr/` (37 ADRs covering every architectural decision through this session), release-please workflow, Conventional Commits enforcement, pre-commit hooks
- Definition of Done in CONTRIBUTING.md (Code / Documentation / Migrations / Commits / Retros sections — no partials)
- Chunk-retro discipline: `.agent/retros/chunk-<N>-retro.md` per chunk, written before the chunk closes
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
- **Memory notes:** `feedback_conventional_db_reflex.md`, `project_code_against_repo.md`, `feedback_no_bandaids_on_chunk0.md`, `feedback_setup_host_is_one_time.md`, `feedback_transparency_over_workarounds.md`
- **End-to-end CI green** on commit `ab8f62b` — capabilities → build → db-ensure (DbUp) → smoke-test all passing
- **Concept-capture ADRs (0035-0037):** prompt ingestion + compiled cascade traversal, arena/source-trust consensus, layered seed ingestion + model-codec fidelity.

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

### Fixed

- Bootstrap script: `svc.sh install` made idempotent (skips when systemd unit already exists); `register` step uses `.runner` file as canonical idempotency check; sudoers verification probe uses `sudo -ln` (list-mode) instead of running a non-matching command.
- PG peer-auth probe in CI + bootstrap: explicit `-U laplace_admin` (was defaulting to OS user as PG role, which is `laplace-runner` and isn't a PG role).
- Postgis package mismatch on hart-server: `update-alternatives --auto postgresql-18-postgis.control` restored the symlink chain after `postgis.control` had been pinned to a stale `3.7.0dev` from a prior install.
- `laplace_priv.install_extension` wrapper search_path reordered from `pg_catalog, public` → `public, pg_catalog` so postgis's `CREATE TYPE geometry_dump` (no schema qualifier) resolves to `public` instead of `pg_catalog`.
- Orphan `laplace` schema on hart-server (owned by `postgres` from a transitional commit's pre-create step) — manually dropped via `sudo -u postgres psql -d laplace -c "DROP SCHEMA laplace CASCADE"`; future installs use the `trusted = true` path which creates the schema with `laplace_admin` as owner.

### Decisions

See [docs/adr/](docs/adr/) for architectural decision records (current count: 37, with explicit supersedence chains for 0003→0015 and 0014→0019, narrowed scope from 0021→0023, expanded from 0023→0025 with PG extension split, ADR 0029 amends RULES.md R1, ADR 0032 locks ADR 0028 as prerequisite, ADRs 0035-0037 lock prompt/cascade, source-trust, and model-codec semantics).

[Unreleased]: https://github.com/SaltyPatron/Laplace/compare/HEAD...HEAD
