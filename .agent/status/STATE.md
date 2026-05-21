# Laplace — Project State

**Last updated:** 2026-05-21 (post-SDLC-refactor — Chunk 0 framework re-baselined)
**Updated by:** claude (SDLC refactor + ADR 0023 architectural pivot)

---

## Current phase

**Chunk 0 — Plan + deps + skeleton + SDLC framework.** ✅ COMPLETE
**Awaiting:** Layer-0 bootstrap on hart-server (see [blockers.md](blockers.md)) before Chunk 1.

All Chunk 0 acceptance criteria green:
- ✅ `just build` succeeds (engine + extension + app + migrations)
- ✅ Engine produces `liblaplace_engine.so.0.1.0`; ctest passes
- ✅ PG extension produces `laplace.so` via PGXS; v0.1.0 alignment locked
- ✅ C# app produces `Laplace.Engine.dll` + `Laplace.Migrations.dll` (net10.0)
- ✅ `ci.yml` (GitHub-hosted) passes for doc/lint/banned-vocab/Conventional Commits
- ✅ GitHub Issues #1-#8 created for Chunks 1-8 (now being refactored from flat checklists into Epic/Story/Task)
- ⏸️ `integration.yml` build job — **blocked on runner bootstrap** (peer-auth probe surfaces the gap intentionally)

Framework, three-layer architecture (Layer 0 bootstrap / Layer 1 DbUp + extension / Layer 2 CI + Justfile), 24 ADRs, Conventional Commits + release-please, Definition of Done, and retro discipline all landed.

## Repository

- **GitHub:** https://github.com/SaltyPatron/Laplace (public)
- **Default branch:** `main`
- **Recent commits (top first):**
  - `e245413` ci(integration): db-ensure job + agent-managed laplace database
  - `87aab5d` refactor(extension)!: extension owns substrate schema; align to v0.1.0
  - `592dcdb` feat(migrations): DbUp + Npgsql Layer-1 runner with up/status/reset/nuke
  - `da00a83` feat(bootstrap): Layer-0 rewrite with bootstrap/status/reset modes
  - `13eb38f` feat(sdlc): adopt full SDLC framework — ADRs, Conventional Commits, DoD, retros

## Architectural pivot — ADR 0023

The `laplace` extension OWNS its schema, tables, types, opclasses, and functions (created by `laplace--A.B.C.sql`, upgraded by `laplace--A.B.C--D.E.F.sql` via `ALTER EXTENSION laplace UPDATE`).

DbUp narrows to extension-lifecycle orchestration: `CREATE EXTENSION postgis + laplace`, role grants, default privileges. The three substrate tables (`entities`, `physicalities`, `attestations`) and all custom types/functions land in the extension's upgrade scripts in Chunks 1–7, NOT in `db/migrations/`.

Tables holding user-ingested data will be marked with `pg_extension_config_dump()` so `pg_dump` captures rows while the schema rebuilds via `CREATE EXTENSION`.

## Runner (per ADR 0019 + ADR 0018)

- **Target identity:** `laplace-runner` system account (no home in /home/, no shell)
- **Installed at:** `/var/lib/laplace-runner/actions-runner`
- **Service:** `actions.runner.SaltyPatron-Laplace.hart-server.service` (systemd, runs as laplace-runner)
- **Labels:** `self-hosted, Linux, X64, laplace, oneapi, postgres-18, dotnet-10, avx2`
- **Peer auth:** `laplace-runner` (OS) → `laplace_admin` (PG role, CREATEDB CREATEROLE)
- **Sudoers:** bounded NOPASSWD for `/usr/bin/make install*` only
- **Bootstrap status on hart-server:** ⚠️ **NOT YET RUN** — see `blockers.md`. Current registered runner is still the legacy `ahart`-owned one from earlier commits.

## Three-layer architecture (ADR 0018)

| Layer | Owner | Resettable via | Status |
|---|---|---|---|
| 0 — system account, runner, PG roles, peer auth, sudoers | `sudo scripts/bootstrap-laplace-runner.sh` | `sudo bootstrap-laplace-runner.sh reset` (typed RESET) | Script ready; bootstrap pending on hart-server |
| 1 — `laplace` DB, `CREATE EXTENSION`, role grants | `app/Laplace.Migrations/` (DbUp) | `just db-reset` (drop SchemaVersions) or `just db-nuke` (drop DB) | Ready; blocked on Layer 0 |
| 2 — build / test / verify / CI | Justfile + `.github/workflows/` | `just clean` | Ready; blocked on Layer 0 |

## Ticket structure (SDLC hierarchy)

Per the SDLC refactor in progress, GitHub Issues follow an **Epic → Story → Task** hierarchy:

- **Epic** (label `epic`) — milestone-scale work, one per chunk. Body lists constituent Stories with checkbox progress.
- **Story** (label `story`) — sprintable agent-deliverable capability. Body references its Epic via "Part of #N", contains acceptance criteria + linked Tasks.
- **Task** (label `task`) — engineering work item (≤2 days). Created when the parent Story enters work.
- **Spike** (label `spike`) — investigation; resolves into ADR + follow-on stories.

Issues #1–#8 (the original "chunks") are being relabeled as Epics, with detailed story decomposition landing alongside (see `.agent/retros/` for the refactor's plan + outcomes).

## Workflows

- **CI (GitHub-hosted):** `.github/workflows/ci.yml` — required-files check, markdown lint, link check, banned-vocabulary scan, Conventional Commits validation.
- **Integration (self-hosted):** `.github/workflows/integration.yml` — capability check (with peer-auth probe), build, db-ensure (DbUp migrations against managed laplace DB), smoke test. Triggers on push to main + manual ONLY.
- **Release Please:** `.github/workflows/release-please.yml` — watches Conventional Commits on main; opens release PRs with auto-generated CHANGELOG + version bumps.

## Milestones (GitHub)

- **v0.1.0 — Chattable Qwen3 Roundtrip** — the substrate's first end-to-end proof: ingest a real Transformer, emit it as a GGUF, chat with the emitted model. Encompasses Epics 1–8.

## Epics (chunks)

| # | Epic | Status | Issue |
|---|---|---|---|
| 0 | Plan + deps + skeleton + SDLC | ✅ DONE | — (no GitHub issue; retro at [.agent/retros/chunk-0-retro.md](../retros/chunk-0-retro.md)) |
| 1 | Core math primitives (coord4d, hash128, hilbert4d, mantissa) | ⏸️ BLOCKED on runner bootstrap | [#1](https://github.com/SaltyPatron/Laplace/issues/1) |
| 2 | Geometry serde + GIST integration | — | [#2](https://github.com/SaltyPatron/Laplace/issues/2) |
| 3 | Perf-cache + T0 seed (Unicode UCD) | — | [#3](https://github.com/SaltyPatron/Laplace/issues/3) |
| 4 | First linguistic source (WordNet) | — | [#4](https://github.com/SaltyPatron/Laplace/issues/4) |
| 5 | Glicko-2 + cross-source dynamics | — | [#5](https://github.com/SaltyPatron/Laplace/issues/5) |
| 6 | TransformerModelSource + Procrustes + lottery-ticket sparsity | — | [#6](https://github.com/SaltyPatron/Laplace/issues/6) |
| 7 | Synthesis pipeline (LlamaTemplate + extractors + GGUFWriter) | — | [#7](https://github.com/SaltyPatron/Laplace/issues/7) |
| 8 | Round-trip + chat verification (MILESTONE) | — | [#8](https://github.com/SaltyPatron/Laplace/issues/8) |

Canonical plan in [.agent/status/plan.md](plan.md).

## In progress

- SDLC ticket refactor — converting Chunks #1–#8 from flat-checklist issues to Epic/Story/Task hierarchy.

## Recently completed (post-Chunk-0-framework)

- 2026-05-21: SDLC framework — 24 ADRs, Conventional Commits + release-please, pre-commit hooks, Definition of Done, retro discipline
- 2026-05-21: Three-layer architecture (ADR 0018) — Layer 0 bootstrap / Layer 1 DbUp + extension / Layer 2 CI + Justfile
- 2026-05-21: `laplace-runner` system account (ADR 0019) replacing the `ahart`-runs-CI pattern
- 2026-05-21: DbUp + Npgsql Layer-1 runner (ADR 0021) — `up` / `status` / `reset` / `nuke`
- 2026-05-21: Extension owns substrate schema (ADR 0023); DbUp narrows to extension-lifecycle orchestration
- 2026-05-21: Extension version aligned to v0.1.0 across .control / .sql / C `laplace_version()` / CI smoke test
- 2026-05-21: First retro written — `.agent/retros/chunk-0-retro.md`

## Open blockers

See [blockers.md](blockers.md). Headline: Layer-0 bootstrap pending on hart-server.

## Decisions log

See [decisions.md](decisions.md) for one-line summaries; full ADRs in [docs/adr/](../../docs/adr/).

## Verifications

- 2026-05-21: `dotnet build Laplace.slnx -c Release` → 0 warnings, 0 errors (engine + extension binding stub + migrations runner all build clean)
- 2026-05-21: `bash -n scripts/bootstrap-laplace-runner.sh` → syntax OK
- 2026-05-21: `integration.yml` capabilities job — peer-auth probe correctly surfaces that hart-server still has the legacy `ahart` runner; this is the expected failure state until bootstrap is run.
