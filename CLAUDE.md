# Laplace — Agent Operations

Windows is the canonical platform. Deep law lives in `.github/instructions/*.instructions.md`
(build-environment, layering-law, ingest-witness, type-id-law, model-deposition, attestation-engine),
`docs/OPERATIONS-WINDOWS.md` (platform laws), `docs/ARCHITECTURE.md` (system map).
This file is the operational index: constants, verbs, recipes. Do not improvise around it.

## Constants — never rediscover, never vary

ALL of these are served by `scripts\win\env.cmd` (single source, `if not defined` = pre-set to
override for one run). Scripts must NOT redeclare them; a new script gets them by calling env.cmd.

- **DB**: host `localhost`, user `postgres`, password `postgres`.
  - psql: `$env:PGPASSWORD='postgres'` then `& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -U postgres -d laplace`
  - .NET CLI/endpoint: `LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace`
  - Several `laplace*` databases may exist (`laplace`, `laplace_code`, `laplace_probe`, regress DBs…). `laplace` is the primary; scripts taking a DB arg mean any of them.
- **PG service**: `postgresql-x64-18`. NEVER restart it to "fix" a build/deploy problem; deploy is restart-free by design.
- **Deploy home**: `D:\Data\Postgres\laplace` (`lib\`, `share\extension\`). NEVER write under `C:\Program Files\PostgreSQL`.
- **Data**: corpora `D:\Data\Ingest` (books: `D:\Data\Ingest\test-data\text`; Unicode: `D:\Data\Ingest\Unicode\Public\17.0.0`); models `D:\Models\hub`; junctions `D:\vault\Data`→Ingest, `D:\vault\models`→hub.
- **Endpoint**: `serve.cmd`, port 5187, OpenAI-compatible at `http://localhost:5187/v1`.

## Build trees — THE LAW

Exactly three trees, ever:

| tree | what | script |
|---|---|---|
| `build-win` | engine Release (3 DLLs, tests, perfcache) | `build-engine.cmd` |
| `build-win-ext` | PG extensions | `build-extensions.cmd` |
| `build-win-asan` | engine + `-fsanitize=address` | `build-engine-asan.cmd` |

**NEVER create another tree** and never run `cmake -B` by hand (the `build-win2` improvisation
committed 74k lines of artifacts; a hand-run ASan configure poisoned its tree with literal `%LAPLACE_RC%`).
Builds are mutex-guarded (`.lap-lock` inside the tree, stale-self-clearing): if a build waits, another
build is live — wait, don't fork. Blocked by a held artifact (LNK1104, copy fail)? `locks.cmd` shows
holders; `locks.cmd --kill` clears the safe ones. Dead-configure debris is cleared automatically.

## Verbs (`scripts\win\`) — always these, never improvised pipelines

| verb | does |
|---|---|
| `status.cmd` | **RUN FIRST.** Tree freshness (ninja dry-run), deploy currency (hash diff), PG/extensions/DBs/row counts, endpoint, locks |
| `locks.cmd [--kill]` | who holds Laplace artifacts; `--kill` stops CLI/test/endpoint hosts (never postgres, never live builds) |
| `build-engine.cmd [--reconfigure] [targets…]` | incremental `build-win`; configure only if needed |
| `build-engine-libs.cmd` | alias: just the 3 DLLs + core tests (the CLI iteration loop) |
| `build-engine-asan.cmd [--configure-only] [targets…]` | ASan tree; the icx ASan flag law is baked in — do not configure by hand |
| `build-extensions.cmd [--reconfigure] [targets…]` | incremental `build-win-ext` |
| `install-extensions.cmd [--recycle]` | gen-sql → stage to deploy home → GUC wiring. Locked DLLs are hot-swapped; `--recycle` terminates `laplace%` backends (kills in-flight work) so new code loads immediately |
| `verify-deploy.cmd` | deploy completeness, no rebuild |
| `test-engine.cmd [-R regex] [-LE regress]` | ctest over `build-win` (serial — gguf tests share a temp path and race under `-j`) |
| `regress.cmd` | pg_regress, both extensions (deploy first) |
| `test-app.cmd [ProjectFilter] [dotnet-args]` | dotnet tests; filter = substring, e.g. `test-app.cmd SubstrateCRUD` |
| `test-all.cmd` | engine + regress + app + verify-fk |
| `refresh-substrate-module.cmd <NN_mod.sql.in> <db>` | hot-reload ONE substrate SQL module on a live DB — no rebuild, no redeploy |
| `e2e-master.cmd [--skip-clean] [--skip-models] [--db-only]` | THE orchestrator: clean→codegen→build→deploy→DB→ladder→verify |
| `seed-ladder.cmd` | THE witness ladder (executable mirror of `witness-manifest.json`). Knobs: `LAPLACE_LADDER_START=proof`, `LAPLACE_LADDER_DRY=1`, `LAPLACE_SKIP_{USAGE,MODELS,LEXICAL_BULK}`. NEVER copy the ladder into a new script — call this |
| `seed-substrate.cmd` / `seed-resume-prove.cmd` / `seed-deferred-lexical.cmd` | thin callers: fresh drop+seed / resume proof path / heavy lexical — all delegate to the ladder |
| `index-content.cmd <db> [deep\|text]` | rebuild generation content index after seeding |
| `walk-verdict.cmd [db]` | post-deposit acceptance: rebuild consensus secondaries (B2 pair) + model-planes-audit; verdict → `build-win\verdicts\` |
| `serve.cmd` / `converse.cmd "q"` | dev endpoint / one-shot converse query (endpoint also runs TurnWitness: served turns deposit as UserPrompt testimony) |
| `download-code-data.cmd {tiny-codes\|stack-v2\|authority}` | fetch code corpora (HF_TOKEN needed for HF) |

## Iteration recipes — change → minimal pipeline

- **Engine C** (`engine/core|dynamics|synthesis`): `build-engine-libs.cmd` → `test-engine.cmd -R <area> -LE regress`. If extensions statically consume it: `build-extensions.cmd` → `install-extensions.cmd` → `regress.cmd`.
- **Extension C** (`extension/laplace_substrate/src`): `build-extensions.cmd` → `install-extensions.cmd --recycle` → `regress.cmd` (or targeted audit SQL below).
- **SQL surface**: edit `extension/laplace_substrate/sql/NN_*.sql.in` (NEVER a generated/staged `.sql`) → `refresh-substrate-module.cmd NN_module.sql.in laplace`. Full redeploy only when the module set changes.
- **C# app**: `dotnet build app\<Project>\<Project>.csproj -c Release` → `test-app.cmd <Project>`. A running CLI/endpoint locks `bin\Release` — `locks.cmd --kill`. Long ingests use the sidecar pattern (`ingest-text.cmd` builds to `%TEMP%\laplace-cli-sidecar`) so they never pin the repo bins.
- **Relation/attestation law**: edit `RelationTypeRegistry.cs` / `PosReference.cs` → `scripts\codegen-attestation-law.ps1` → rebuild engine libs + refresh seed module. NEVER hand-edit `engine/core/src/generated/*`, `engine/manifest/*.toml`, or `extension/laplace_substrate/sql/generated/*`.
- **Memory bug hunt**: `build-engine-asan.cmd laplace_core_tests` → `ctest --test-dir build-win-asan -R <area>`. PATH law: `env.cmd` puts `build-win\core` first, so before running anything from the ASan tree prepend `build-win-asan\{core,dynamics,synthesis}` (the build script does this itself; do the same for standalone ctest) — otherwise its exes silently load the Release DLLs (0xC0000139).

## Native-C law (extension/laplace_substrate/src)

Shared datum/hash/SPI-read helpers (`copy_bytea_datum`, `rel_type_id`, `spi_realize`,
`eff_mu_display_numeric`, …) live in **`spi_common.h`** — never re-declare one in a native file
(they used to exist in up to five copies, twice with the same bug). The SPI **nulls-string law**
is documented in that header: start all-present (`"   "`), mark `'n'` per-param conditionally.
Long-running CLI work goes through `cli-sidecar.cmd` (staleness-checked sidecar — never pins
`bin\Release`).

## Validation ladder — cheapest first

`status.cmd` → `verify-deploy.cmd` → `test-engine.cmd -R <area>` → `regress.cmd` →
`psql … -f scripts/sql/substrate-audit.sql` / `converse-audit.sql` → `e2e-master.cmd --skip-models`.
Never start at the right end: e2e is for proving the whole pipeline, not for checking one change.

## Footguns (each one has burned a session)

- `.cmd` files MUST be CRLF (cmd's LF bug eats characters). After authoring/editing, re-normalize.
- PowerShell `-replace` is case-INSENSITIVE — use `-creplace` for token/macro work.
- The Bash tool mangles `cmd /c` — run `.cmd` scripts from the PowerShell tool directly.
- `setvars.bat` breaks under `NoDefaultCurrentDirectoryInExePath` (harness sets it); `env.cmd` unsets it — always enter through the scripts, never call setvars yourself.
- pwsh 7 exports its own `PSModulePath`; an inherited copy makes Windows PowerShell 5.1 cmdlets (`Get-FileHash`, `ConvertTo-Json`) silently vanish. `env.cmd` clears it; any new `.cmd` that calls `powershell` without env.cmd must too.
- Never let a tree get configured with a different toolchain's ninja — `CMAKE_MAKE_PROGRAM` is pinned in the build scripts because a VS2022-ninja log is unreadable by VS2026's ninja (phantom full rebuilds, log destroyed).
- icx rejects ASan+debug CRT; CMake's ABI probe defaults to Debug config — only `build-engine-asan.cmd` knows the full flag law.
- `LAPLACE_INGEST_WORKERS=1` for wordnet/omw (referential race). ConceptNet is pinned serial in `seed-deferred-lexical.cmd`.
- Attestation/physicality column is `type` — any surviving `kind` reference is refactor residue, i.e. a bug.
- `icpx` is the GNU driver and rejects MSVC flags — both C and C++ use `icx` on Windows.
- Justfile / `scripts/*.sh` / GH workflows are the (stale) Linux CI path — not for this machine.
- Any process running TextDecomposer must call `CodepointPerfcache.LoadDefault()` first (Engine.Core) — `TryBuildContentWitness` swallows the not-loaded error into a silent no-op (cost a debugging round 2026-06-11).
- **The consensus merge fold is the write-path ceiling** (25–70k rel/s; 2 random PK probes/relation, working set > RAM, 3–4× re-touch across periods — measured, see `docs/HANDOFF-fold-lane.md`). Sorted probes do NOT fix it (heap is history-random). Bulk deposits: backlog is bounded by `LAPLACE_FOLD_BACKLOG_MAX` (default 12 epochs ≈ ~28 GB staging on disk; 0 disables). The fix is the PK-less C bulk fold (unbuilt) — read the handoff before touching this lane.
