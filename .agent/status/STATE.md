# Laplace — Project State

**Last updated:** 2026-05-22 (architectural overhaul session — ADRs 0024–0032 + 5 new Epics + 50 new Stories)
**Updated by:** claude (full audit + ADR batch + ticket refactor)

---

## Current phase

**Chunk 0 framework + architectural overhaul.** ✅ COMPLETE through commit `194ddae`.
**Awaiting:** hart-server bootstrap re-run to apply wrapper search_path fix + finish proving end-to-end CI green.

What landed this session:
- **8 new ADRs (0024–0031)** covering engine modularization, PG extension modularization, C# project structure, separation-of-concerns invariants, custom-built PG + PostGIS, custom indexing strategy (5 opclasses), MKL/Eigen/Spectra/TBB integration, custom AM research spike.
- **ADR 0032** locking ADR 0028 as a hard prerequisite and committing to the unified CMake build pipeline (Path B — PGXS retired once we own the PG build).
- **All user-authored docs refreshed** to reflect modularization + BLAKE3 (replacing inconsistent XXH3 references): CLAUDE.md, RULES.md, STANDARDS.md, DESIGN.md, GLOSSARY.md, OPERATIONS.md, README.md, .agent/status/plan.md.
- **5 new GitHub Epics + 50 new Stories** wired to milestone v0.1.0 with module/area/priority labels.
- **Bootstrap fixes**: postgis installed directly as postgres at first install (not via wrapper, since bootstrap runs as superuser already); wrapper's `search_path` reordered to `public, pg_catalog` so postgis `CREATE TYPE geometry_dump` resolves correctly.
- **DbUp migration**: uses `laplace_priv.install_extension('postgis')` wrapper for laplace_admin's recovery path (post-db-nuke) — works once bootstrap has installed postgis and updated the wrapper.

## Repository

- **GitHub:** https://github.com/SaltyPatron/Laplace
- **Default branch:** `main`
- **Recent commits (top first):**
  - `194ddae` docs(adr): 0032 unified CMake build pipeline; lock 0028 as prerequisite
  - `0293f59` docs: refresh user docs for modularization + BLAKE3; fix(bootstrap): postgis search_path
  - `020009b` docs(adr): batch ADRs 0024-0031 — modularization + custom PG + indexing + MKL + AM spike
  - `1034e76` fix(migrations): use laplace_priv wrapper for postgis install (unblock CI)
  - `9f883e0` fix(bootstrap): register-step idempotency via .runner file check
  - `5194b3a` feat(bootstrap): laplace_priv SECURITY DEFINER wrappers + substrate-honest allowlist

## Architectural overview (post-overhaul)

**Engine — 3 shared libraries (ADR 0024):**
- `liblaplace_core.so` — coord4d, hash128 (BLAKE3), hilbert4d, mantissa, geom serde, Glicko-2 fixed-point, A* primitives
- `liblaplace_dynamics.so` — Procrustes, eigenmaps, Gram-Schmidt, sparsity (links oneMKL + Spectra + TBB)
- `liblaplace_synthesis.so` — recipe extraction, architecture templates, GGUF writer

**PG extensions — 2 (ADR 0025):**
- `laplace_geom` — general-purpose 4D PostGIS additions (`ST_*_4d`, `hash128`, custom B-tree + GIST opclasses)
- `laplace_substrate` — substrate-domain schema (entities/physicalities/attestations, Glicko-2 aggregate, cascade SRFs, SP-GiST + BRIN opclasses)

**C# layer — multiple projects (ADR 0026):**
- `Laplace.Engine.{Core,Dynamics,Synthesis}` (one per engine `.so`) + `Laplace.Migrations` + `Laplace.Cli` + `Laplace.Endpoints[.*]` + `Laplace.Sources.*` + `Laplace.Decomposers.*`

**Build pipeline — unified CMake (ADR 0032):**
- Top-level CMakeLists drives `external/postgresql` + `external/postgis` submodules, engine/, extension/. Phase 1 bridge: PGXS still in extension/ while on stock PG; Phase 2: full CMake replacement once Epic B lands.

**Custom-built PG + PostGIS (ADR 0028, locked in by ADR 0032):**
- Git submodules under `external/` pinned to release tags
- Built with `icx`/`icpx`, installed to `/opt/laplace/pgsql-18`
- Required for performance regime + "code against the repo" correctness (eliminates apt-half-upgrade failure class)

## GitHub issue structure (post-refactor)

**Total open issues: 169** (155 Stories + 12 Epics + 2 Spike-labeled items)

**Epics (12):**
| # | Title | Status |
|---|---|---|
| #1–#8 | Chunks 1–8 | Untouched; Chunk 1 unblocked once CI green |
| #118 | Epic A — Modularization Refactor | Pending; code refactor blocked on CI green |
| #119 | Epic B — Custom-built PG + PostGIS with Intel toolchain | Pending; prerequisite for Path B |
| #120 | Epic B′ — Unified CMake build pipeline | Pending; depends on Epic B |
| #121 | Epic D — MKL/Eigen/Spectra/TBB integration | Pending; depends on Epic A's engine/dynamics scaffold |

**Spike (1):** #122 (Custom AM, post-v0.1.0)

**5 opclass Stories** slotted into existing Chunks #1, #2, #2, #3, #5 (#168–#172).

## Runner (per ADR 0019 + 0018)

- **Target identity:** `laplace-runner` system account
- **Installed at:** `/var/lib/laplace-runner/actions-runner`
- **Service:** `actions.runner.SaltyPatron-Laplace.hart-server.service`
- **Bootstrap status:** ✅ Layer-0 complete on hart-server (system account, runner, PG roles, peer auth, sudoers, postgis package re-installed via update-alternatives auto).
- **⚠️ Pending:** wrapper search_path update + postgis re-install via the new bootstrap step. `sudo scripts/bootstrap-laplace-runner.sh bootstrap` will do both (idempotent).

## Workflows

- **CI (GitHub-hosted):** `.github/workflows/ci.yml` — passing
- **Integration (self-hosted):** `.github/workflows/integration.yml` — currently failing at db-ensure; blocked on wrapper search_path fix being deployed to hart-server
- **Release Please:** `.github/workflows/release-please.yml` — passing

## Open blockers

See [blockers.md](blockers.md). Headline: hart-server needs `sudo scripts/bootstrap-laplace-runner.sh bootstrap` re-run to deploy:
1. The fixed `laplace_priv.install_extension` wrapper (search_path reordered to `public, pg_catalog`)
2. The postgis extension installed directly as postgres (bypassing the wrapper for first install)
After this, CI's db-ensure should pass.

## In progress

- Epic A — code refactor (engine/, extension/, app/ folder structure) — DEFERRED until after CI green confirms wrapper fix end-to-end.

## Recently completed (this session)

- 2026-05-21 → 2026-05-22:
  - SDLC framework: 24 ADRs initial + 8 new ADRs (32 total)
  - Conventional Commits + release-please + Definition of Done + retro discipline
  - Three-layer architecture (Layer 0/1/2) + dedicated `laplace-runner` system account + bounded sudoers + peer-auth model
  - DbUp + Npgsql Layer-1 migrations runner + nuke/reset/up/status modes
  - laplace_priv SECURITY DEFINER wrappers for non-superuser extension management
  - 8 architectural ADRs locked + user docs refreshed
  - 5 new Epics + 50 new Stories created on GitHub
- 2026-05-21: First retro written — `.agent/retros/chunk-0-retro.md`

## Decisions log

See [decisions.md](decisions.md) for one-liners; full ADRs in [docs/adr/](../../docs/adr/) (32 ADRs).

## Memory

Pointers updated in `/home/ahart/.claude/projects/-home-ahart-Projects-Laplace/memory/MEMORY.md`:
- New: `feedback_conventional_db_reflex.md` — PG extension allowlist hygiene (ST_Frechet replaces pg_trgm; UCD replaces citext; perf-cache replaces Bloom)
- New: `project_code_against_repo.md` — submodule-source build is non-negotiable (performance + correctness)

## Verifications

- 2026-05-21: `dotnet build Laplace.slnx -c Release` → 0 warnings, 0 errors
- 2026-05-21: `bash -n scripts/bootstrap-laplace-runner.sh` → syntax OK across multiple commits
- 2026-05-22: postgis package reinstalled + `update-alternatives --auto postgresql-18-postgis.control` restored the symlink chain. CI's old "ST_MMin not found" error is resolved at the package level.
- 2026-05-22: ⏸️ End-to-end integration test pending wrapper deployment on hart-server.
