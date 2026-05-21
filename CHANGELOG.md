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
- 8 GitHub Issues (#1–#8) for Chunks 1–8 with granular subtasks + acceptance criteria
- Discussions enabled with seed threads
- Engine skeleton (CMake + Ninja, Eigen + Spectra + BLAKE3 via FetchContent)
- Extension skeleton (PGXS Makefile, non-relocatable control file, `laplace--0.1.0.sql`, C source)
- C# app skeleton (.NET 10 .slnx + Laplace.Engine class library with P/Invoke stub)
- C# Layer-1 migrations runner (`app/Laplace.Migrations`) — DbUp + Npgsql with `up`/`status`/`reset`/`nuke` modes
- Scripts: `check-prereqs.sh` (real), `bootstrap-laplace-runner.sh` (Layer-0 root setup with idempotent + resettable modes), other operational stubs
- Issue + PR templates
- LICENSE (proprietary — Anthony Hart sole inventor)
- SDLC framework: this CHANGELOG, ADR directory under `docs/adr/` (23 ADRs covering every architectural decision so far), release-please workflow, Conventional Commits enforcement, pre-commit hooks
- Definition of Done in CONTRIBUTING.md (Code / Documentation / Migrations / Commits / Retros sections — no partials)
- Chunk-retro discipline: `.agent/retros/chunk-<N>-retro.md` per chunk, written before the chunk closes

### Changed

- Hash function: switched from XXH3-128 to BLAKE3-128 (truncated). Native SIMD; cryptographic strength; raw bytes end to end (no hex / no string casts). See [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md).
- CI runner identity: refactored from `ahart` (interactive user) to dedicated `laplace-runner` system account with bounded sudo + peer-auth into PG as `laplace_admin`. See [ADR 0019](docs/adr/0019-laplace-runner-system-account.md).
- Migration framework: DbUp + Npgsql replaces the initially-drafted dbmate. .NET-native, no separate toolchain. See [ADR 0021](docs/adr/0021-dbup-for-migrations.md).
- Database/extension ownership: the `laplace` extension owns its schema, tables, types, functions; DbUp narrows to extension-lifecycle orchestration only (`CREATE EXTENSION` + role grants + `ALTER EXTENSION laplace UPDATE`). See [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md).
- Extension default version aligned to `0.1.0` across `laplace.control`, `laplace--0.1.0.sql`, the C `laplace_version()`, and the CI smoke test (was inconsistent: 1.0.0 in some places, 0.1.0 in others).
- `extension/laplace.control`: `relocatable = false`, explicit `schema = 'laplace'`, `requires = 'postgis'`.
- Bootstrap script: renamed from `setup-laplace-runner.sh` to `bootstrap-laplace-runner.sh`; gained `bootstrap` / `status` / `reset` modes; stripped Layer-1 responsibilities (no longer creates the `laplace` database or schema — DbUp's `EnsureDatabase` + the extension itself do that).
- Justfile: replaced `create-db` / `apply-schema` with `db-up` / `db-status` / `db-reset` / `db-nuke` / `migrate-new`; added `bootstrap` / `bootstrap-status` / `bootstrap-reset` wrappers.
- `integration.yml`: added `db-ensure` job that installs the extension + applies DbUp migrations against the agent-managed `laplace` database (replaces the ephemeral-test-DB pattern); smoke test now verifies the deployed substrate, not an ad-hoc DB.

### Decisions

See [docs/adr/](docs/adr/) for architectural decision records (current count: 23, with explicit supersedence chains for 0003→0015 and 0014→0019, narrowed scope from 0021→0023).

[Unreleased]: https://github.com/SaltyPatron/Laplace/compare/HEAD...HEAD
