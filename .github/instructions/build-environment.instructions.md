---
applyTo: "{scripts/**,engine/**,extension/**,app/**,docs/**,CMakeLists.txt,Justfile,.github/workflows/**}"
description: "Use when building, deploying, testing, or running Laplace on Windows — engine, extensions, regress, e2e."
---

# Laplace Build & Deploy (Windows)

**Canonical platform:** Windows with Intel oneAPI + EDB PostgreSQL 18 on localhost. Operational detail: `docs/OPERATIONS-WINDOWS.md`. Architecture context: `docs/ARCHITECTURE.md` (Platform notes).

Agents must follow the **local deploy pattern** below. Do not improvise installs into system directories.

## The local deploy pattern

| Step | Script | Output |
|------|--------|--------|
| 1. Environment | `scripts\win\env.cmd` (called by all other scripts) | `LAPLACE_ROOT`, Intel MKL/TBB on PATH, `PGBIN` for **read-only** PG tools |
| 2. Engine | `scripts\win\build-engine.cmd` | `build-win\` (DLLs, tests, perfcache target) |
| 2b. Engine libs only | `scripts\win\build-engine-libs.cmd` | subset of `build-win\` for CLI (no full gtest) |
| 3. Extensions | `scripts\win\build-extensions.cmd` | `build-win-ext\` (`laplace_geom.dll`, `laplace_substrate.dll`) |
| 4. Deploy | `scripts\win\install-extensions.cmd` | **`D:\Data\Postgres\laplace`** + PG GUC wiring |
| 5. Regress | `scripts\win\regress.cmd` | `build-win-ext\regress_*` output dirs |
| 6. Full e2e | `scripts\win\e2e-master.cmd` | orchestrates clean → codegen → build → deploy → ingest → verify |

**Standard loop (no elevation ever):**

```
scripts\win\build-engine.cmd
scripts\win\build-extensions.cmd
scripts\win\install-extensions.cmd
scripts\win\regress.cmd
```

Verify deploy without rebuilding: `scripts\win\verify-deploy.cmd`

## Hard rules for agents

### NEVER write into PostgreSQL install dirs

- **Do NOT** run `cmake --install` on Windows for Laplace extensions.
- **Do NOT** `copy` / `xcopy` / `move` DLLs or `.control` / `.sql` files into `C:\Program Files\PostgreSQL\18\lib`, `...\share\extension`, or any path under `C:\Program Files\PostgreSQL`.
- **Do NOT** modify files under `C:\Program Files\PostgreSQL` without explicit user approval.

`C:\Program Files\PostgreSQL\18` is the **installed PG runtime** (binaries, headers, `pg_regress.exe`, bundled `libxml2`). It is read-only for Laplace builds. Laplace artifacts deploy to **`D:\Data\Postgres\laplace`**.

`install-extensions.cmd` stages DLLs + extension SQL there and wires the running server via `ALTER SYSTEM` (semicolon-separated GUC lists on Windows):

- `extension_control_path` → `D:/Data/Postgres/laplace/share`
- `dynamic_library_path` → `D:/Data/Postgres/laplace/lib`

Then `pg_reload_conf()`. No admin, no service restart.

### ALWAYS use the Windows script chain

- Engine builds: **`build-win\`** via `scripts\win\build-engine.cmd` (or `build-engine-libs.cmd`).
- Extension builds: **`build-win-ext\`** via `scripts\win\build-extensions.cmd`.
- Extension deploy: **`scripts\win\install-extensions.cmd`** only — never hand-copy to PG libdir.
- All Windows builds must chain through `env.cmd` (sets `MKLROOT` via Intel `setvars.bat`, pins SDK tools, `LAPLACE_ROOT`).

If `MKLROOT not set` or `libircmt.lib` link errors appear, run via `env.cmd` — do not patch CMake to skip MKL.

### pg_regress uses localhost, not system extension install

`scripts\win\regress.cmd`:

- Uses existing PG server: **localhost**, user **postgres**, password **postgres** (`PGPASSWORD`).
- Uses EDB's `pg_regress.exe` from `C:\Program Files\PostgreSQL\18\lib\pgxs\...` (tool only — not an install target).
- Creates ephemeral DBs `laplace_regress_geom` / `laplace_regress_substrate`.
- Writes output under **`build-win-ext\regress_geom`** and **`build-win-ext\regress_substrate`**.

Extensions must already be deployed via `install-extensions.cmd` so `CREATE EXTENSION` resolves from `D:\Data\Postgres\laplace`.

### Python / pip

- **Do NOT** `pip install` into system Python (`C:\Python*`, `C:\Program Files\Python*`, or global `pip install` without a venv) without explicit user approval.
- Laplace Windows builds do not require pip for engine/extensions. Codegen uses `scripts\codegen-attestation-law.ps1`.

### Linux / Justfile is NOT the Windows path

`Justfile`, `scripts/setup-host.sh`, and `.github/workflows/*` target **Linux CI** (`cmake --install` → `/opt/laplace`). GH Actions runner is disabled; the working machine is Windows.

On Windows, **ignore** `just build`, `just install`, root `cmake -B build`, and `cmake --install` unless the user explicitly asks for Linux-style setup.

### Permission denied → stop and fix the path

If a command fails with **Access denied**, **requires elevation**, or **permission denied** on `C:\Program Files\...`:

1. **STOP.** You used the wrong deploy path.
2. **Do NOT** ask the user to "run the build manually" as a workaround.
3. Explain that Laplace deploys to `D:\Data\Postgres\laplace` via `install-extensions.cmd`.
4. Re-run the correct script chain from repo root.

## What `env.cmd` provides (read-only vs write)

| Variable / path | Role |
|-----------------|------|
| `LAPLACE_ROOT` | Repo root (`scripts\win\..\..`) |
| Intel oneAPI `setvars` | `MKLROOT`, TBB, icx on PATH/LIB |
| `PGBIN` | `C:\Program Files\PostgreSQL\18\bin` — **psql, createdb, dropdb only** |
| `build-win\...\` on PATH | engine DLLs for native tests / CLI |
| PG bundled libxml2 | **compile-time** include/lib in `build-engine.cmd` — not a deploy target |

## Build tree layout

```
build-win\           engine: core/, dynamics/, synthesis/, tests, perfcache
build-win-ext\       extensions: laplace_geom/, laplace_substrate/, regress_* output
D:\Data\Postgres\laplace\
  lib\               laplace_*.dll (+ engine DLLs + TBB runtime copied by install-extensions)
  share\extension\   .control + .sql (from gen-sql.ps1)
```

## Common agent mistakes (grep before improvising)

| Wrong | Right |
|-------|-------|
| `cmake --install build` or `cmake --install build-win-ext` | `install-extensions.cmd` |
| `copy *.dll` to `C:\Program Files\PostgreSQL\18\lib` | `install-extensions.cmd` → `D:\Data\Postgres\laplace\lib` |
| `cmake -B build` at repo root on Windows | `cmake -B build-win -S engine` via `build-engine.cmd` |
| Manual `CREATE EXTENSION` after copying to Program Files | `install-extensions.cmd` then `CREATE EXTENSION` (GUCs already wired) |
| `pip install` for build deps on Windows | use `env.cmd` + existing toolchain; ask user before any pip |
| "Please run as administrator" | use local deploy scripts — elevation is never required |

## Prerequisites (user machine)

- PostgreSQL 18 running on localhost (`postgres` / `postgres`)
- Intel oneAPI 2025.3+ with MKL
- VS 2026 CMake/Ninja (paths in `env.cmd`)
- Unicode UCD under `D:\Data\Ingest\Unicode\Public\17.0.0` for full engine build
- Junctions: `D:\vault\Data` → `D:\Data\Ingest`, `D:\vault\models` → `D:\Models\hub` (for e2e ingest)

## Related docs

- `docs/OPERATIONS-WINDOWS.md` — script catalog, platform laws
- `docs/ARCHITECTURE.md` — layering, determinism, Windows dlopen/static-link law
- `.github/instructions/layering-law.instructions.md` — C#/SQL/C boundaries (not build/deploy)
