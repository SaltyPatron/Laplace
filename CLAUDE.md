# Laplace — operating manual (read this before building/running anything)

Windows host. Toolchain: **Intel oneAPI `icx`/`icpx`**, Ninja, CMake (VS 2026 bundled), Windows SDK
`rc`/`mt`, **PostgreSQL 18**, .NET 10. Every `.cmd` lives in `scripts\win\` and sources `env.cmd` first.

## env.cmd (sourced by every script)
Sets `PGBIN` (PG18), prepends `build-win\{core,dynamics,synthesis}` + Intel oneAPI + VS to `PATH`,
and: `INGEST=D:\Data\Ingest`, `REPOS=D:\Repositories`, `LAPLACE_MODEL_HUB=D:\Models\hub`,
`LAPLACE_DB=Host=localhost;...;Database=laplace`. Always run engine/ingest commands **through a script
that sources env.cmd** (or source it yourself) — otherwise the native DLLs won't resolve.

## Build & deploy — use the scripts, never hand-copy artifacts
- **Full clean/rebuild/redeploy:** `scripts\win\rebuild-all.cmd`
  (Phase 1 clean → 2 codegen → 3 engine → 4 extensions → 5 **deploy/install** → 6 app → 7 perfcache).
  Flags: `--skip-clean`, `--skip-app`.
- **Engine only:** `build-engine.cmd [--clean-first|--reconfigure] [targets]` → `build-win\` (Release, icx):
  `laplace_core.dll`, `laplace_dynamics.dll`, `laplace_synthesis.dll`.
- **Extensions only:** `build-extensions.cmd` → `build-win-ext\`: `laplace_geom.dll`, `laplace_substrate.dll`.
- **App:** `dotnet build app\Laplace.slnx -c Release`. Output: `app\Laplace.Cli\bin\Release\net10.0`.
- **Deploy:** `install-extensions.cmd [--recycle]` — copies engine+extension DLLs into the **custom
  library path `D:\Data\Postgres\laplace\lib`** via a `swapcopy` HOT-SWAP (renames a locked `.dll` to
  `.stale~<rand>`, drops the new one; backends pick it up on reconnect), generates SQL into
  `...\share\extension`, and sets `dynamic_library_path` / `extension_control_path`.
- **ASAN engine:** `build-engine-asan.cmd [targets]` → `build-win-asan\` (RelWithDebInfo, has `.pdb`).

## Where the engine actually loads from (this matters for crash repro)
- **Postgres backends** (server-side: the `laplace_substrate` extension, e.g. the OpenSubtitles ingest):
  load `laplace_substrate.dll` + `laplace_core.dll` from `D:\Data\Postgres\laplace\lib` via the DB GUC
  `dynamic_library_path = $libdir;D:/Data/Postgres/laplace/lib`. Query it: `SHOW dynamic_library_path;`.
- **.NET CLI** (client-side: omw/wiktionary/tatoeba ingest): `[LibraryImport("laplace_core")]` default
  resolver → app `bin` then `PATH`. **Do NOT hand-copy DLLs into `bin`** to force a different build —
  the loader prefers `bin` over PATH, and poking installed artifacts is wrong. For native crash repro
  use the **standalone native ASAN test** `build-win-asan\core\tests\laplace_core_tests.exe` (fully
  native — the .NET CLR swallows `0xC0000005` as "Fatal error" before ASAN can report).

## Database
PG18 cluster at `D:\Data\Postgres`, port 5432, db **`laplace`** (Win service `postgresql-x64-18`;
restart via Services, not `pg_ctl`). `db-reset.cmd [--recycle]`: terminate laplace backends → DROP +
`createdb laplace` → `CREATE EXTENSION postgis, laplace_geom, laplace_substrate` (regenerates the SQL
from `extension\laplace_substrate\sql\*.sql.in` and deploys) → `substrate_health()`. A pgAdmin/pgAgent
session may be live on this cluster and has dropped `laplace` mid-work before — check before blaming a pipeline.

## Seeding
`seed-step.cmd <step> [path]` (one decomposer) or `seed-stage.cmd <stage>`. Data under `D:\Data\Ingest`.
Runs `dotnet run --project Laplace.Cli ... -c Release --no-build -- ingest <step> [path]` (needs a prior
Release build). Granular dependency order: unicode, iso639, cili, wordnet, verbnet, propbank, framenet,
mapnet, wordframenet, semlink, ud, document, chess (chess = `ingest chess D:\Data\Ingest\Games\Chess`).

## Gotchas
- Shells (Bash + PowerShell tools) **share a working directory** — a `cd` in one affects the other; use
  absolute paths or `Set-Location D:\Repositories\Laplace` first. A relative `scripts\win\...` from the
  wrong cwd fails with "system cannot find the path specified".
- Never put a `C:\Program Files` (or other system) path in the same script as `Remove-Item`/destructive
  ops — the harness guard blocks the whole command and reports it as "Remove-Item on system path C:\Program".
- ASAN-instrumented core won't report inside the .NET CLI (CLR SEH). Use the native test exe.
