# Agent instructions — Laplace

**Read [CLAUDE.md](CLAUDE.md) first. It is the authoritative entry point** (invention
summary, doc map, build/seed workflow, non-negotiable working rules). This file only
adds harness-specific adaptations and quick references; where they overlap, CLAUDE.md wins.

## Agent customization surface (what is wired up for you)

- **Scoped instructions** ([.github/instructions/](.github/instructions)): auto-apply by
  glob — decomposers, engine-native, sql-substrate, tests, scripts-win, scratchpad-docs.
- **Skills** ([.claude/skills/](.claude/skills), read by VS Code + Claude Code):
  `/laplace-health` (full stack check), `/laplace-seed` (user-invoked only),
  `/substrate-query` (three-layer model + probes), `/foundry-loop` (synthesize→verdict→gate).
- **MCP**: `laplace-db` — read-only (restricted) Postgres server over the `laplace` DB,
  configured for VS Code ([.vscode/mcp.json](.vscode/mcp.json)), Claude Code
  ([.mcp.json](.mcp.json)), and Cursor ([.cursor/mcp.json](.cursor/mcp.json)). Prefer it
  for exploration; it cannot write, which is the point.
- **Guard hook** ([.claude/settings.json](.claude/settings.json) →
  [.claude/hooks/laplace-guard.ps1](.claude/hooks/laplace-guard.ps1)): deterministically
  DENIES (1) bare `scripts/win/*.cmd` invocations not wrapped in `cmd /c`, and
  (2) seed/reset/ingest commands while a `Laplace.Cli` ingest is already running. If it
  blocks you, it is right — fix the command, do not route around it.
- **Subagents** ([.github/agents/](.github/agents)): `substrate-verifier` (read-only
  live-data proof of claims), `doc-reconciler` (kills .scratchpad doc drift).
- **Prompt**: `/next-task` ranks work from binding docs (05/06/09) + code-verified open items — not doc 13 alone.
- **Cursor**: [.cursor/rules/](.cursor/rules) mirrors the hard law + scoped rules.
- **Ingest sizing**: batch/commit/working-set defaults are source-aware in `IngestSizing` — do not override via `LAPLACE_INGEST_*` env vars unless debugging a one-off run.

## Terminal adaptation (VS Code / PowerShell harnesses)

CLAUDE.md says "use the Bash tool" for `scripts/win/*.cmd` because this machine's pwsh
has a confirmed .cmd-launch regression ([PowerShell#27634](https://github.com/PowerShell/PowerShell/issues/27634)).
If your terminal is PowerShell, never invoke a `.cmd` directly. Wrap it in cmd.exe:

```powershell
cmd /c "scripts\win\seed-step.cmd wordnet"
cmd /c "call scripts\win\env.cmd && cd build-win && cmake --build . --target laplace_dynamics"
```

[scripts/win/env.cmd](scripts/win/env.cmd) is the toolchain source of truth (oneAPI/MKL,
cmake/ninja, PG 18 paths, DB env, GC tuning). Scripts self-load it; ad-hoc native builds
must `call` it first, as above.

## Build & test quick reference

### Windows (local)

| Task | Command (wrap in `cmd /c` from pwsh) |
|------|--------------------------------------|
| Full clean rebuild + codegen + perfcache | `scripts\win\rebuild-all.cmd` |
| Engine only / extensions only | `scripts\win\build-engine.cmd` / `scripts\win\build-extensions.cmd` |
| Publish API → IIS (incl. chess/lichess env) | `scripts\win\publish-deploy.cmd` |
| .NET tests (5 xunit projects, excludes `Tier=perf`) | `scripts\win\test-app.cmd [project-substring]` |
| Engine gtests | `scripts\win\test-engine.cmd` (ctest over `build-win`) |
| pg_regress | `scripts\win\regress.cmd` |
| Everything (toolchain + gtest + regress + dotnet + FK verify) | `scripts\win\test-all.cmd` (logs to `build-win-ext\test-all.log`) |
| DB reset / foundation seed / one source | `scripts\win\db-reset.cmd` / `seed-foundation.cmd` / `seed-step.cmd <source>` (`--list` to enumerate) |

### Linux (hart-server / CI)

| Task | Command |
|------|---------|
| Full host bring-up (once) | `sudo bash scripts/setup-host.sh` |
| Vendor deps rebuild | `bash scripts/build-system-deps.sh` (no-op if stamp matches; `LAPLACE_FORCE_DEPS=1` to force) |
| CI / ongoing deploy | push to main → `laplace.yml` → `scripts/pipeline.sh` |
| Manual publish | `bash scripts/pipeline.sh publish` |

`Justfile` is a thin Linux convenience layer; prefer the scripts above when they disagree.

DB: `psql -h localhost -U postgres -d laplace` (password `postgres`), then
`SET search_path = laplace, public;`. `SELECT * FROM api('<substring>');` lists the
schema's own helper catalog — check it before assuming something doesn't exist.

## Hard operational law (violations have corrupted state before)

- One ingest at a time; never run parallel agent sessions against Postgres mid-write.
- Never edit a `.cmd` while it is executing.
- After ANY engine rebuild, run `build-extensions.cmd`; `senses(word_id('dog')) > 0` is the real
  health check (`senses('dog')` always returns 0). MSB3027 copy failure ⇒ clean-rebuild.
- `seed-step.cmd` runs an independent `:verify_step` — trust it, not the CLI summary line.
- Full lesson list: [.scratchpad/02_Identified_Issues.txt](.scratchpad/02_Identified_Issues.txt) (L1–L11).
- Postgres service: never `pg_ctl start` (orphans outside SCM); never agent UAC
  (`Start-Process -Verb RunAs`); never `db-reset`/`DROP DATABASE` unless the user
  explicitly asked this turn. Orphan = service Stopped + port 5432 live → point at
  `scripts\win\reclaim-postgres.cmd` (user elevates themselves) and stop. Preflight:
  `scripts\win\pg-service-guard.cmd`. Hot-swap leftovers must leave `deploy\` (see
  `install-extensions.cmd`), not sit as `*.stale~*` next to live DLLs.
- Index-cycle crash (2026-07-10): concurrent `CREATE INDEX` sessions in
  `NpgsqlIndexCycle` (full `maintenance_work_mem` each) AV'd a backend in
  `VCRUNTIME140.dll` during attestations/consensus rebuilds and detached the
  service. Outer index-build concurrency is capped at 1; do not reintroduce
  ApplyParallelism across multiple CREATE INDEX connections.

## Binding design docs (read before deep work in the area)

- [.scratchpad/05_Substrate_Invariants.txt](.scratchpad/05_Substrate_Invariants.txt) — axioms; binding.
- [.scratchpad/06_Engineering_Ruleset.txt](.scratchpad/06_Engineering_Ruleset.txt) — Rules #1–#12; Rule #8 = the ingest sequence; binding.
- [.scratchpad/17_Decomposer_Full_Stack_Audit.md](.scratchpad/17_Decomposer_Full_Stack_Audit.md) — decomposer + ingest-spine audit (code-verified); start here for decomposer work.
- [.scratchpad/06_Engineering_Ruleset.txt](.scratchpad/06_Engineering_Ruleset.txt) — Rule #8 ingest sequence; binding for pipeline changes.
- [.scratchpad/13_Stabilization_Audit_and_Plan.txt](.scratchpad/13_Stabilization_Audit_and_Plan.txt) — historical stabilization notes; verify against code before trusting.
- Full doc map with status: [CLAUDE.md](CLAUDE.md) § Doc map.

## Conventions that differ from common practice

- Decomposers ([app/Laplace.Decomposers](app/Laplace.Decomposers)) are pure
  content → `SubstrateChange` streams with ZERO inline SQL; the pipeline spine does all
  batching/dedup/fold/COPY. Putting SQL or the right algorithm at the wrong pipeline
  stage is a spec violation (Rule #8).
- C#/SQL orchestrate; native C (`engine/`) does heavy lifting. No GPU code in
  `engine/` or `extension/` — structural, not an omission.
- One implementation per fact; duplication requires a documented reason (Rule #6).
- xunit suites share process-global native state — fixtures must never call
  `CodepointPerfcache.Unload()`.
- Verify against live data; never present a narrow patch as the architectural fix.
