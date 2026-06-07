# Operations — Windows (canonical platform as of 2026-06-07)

The repo's working platform is this Windows machine; Linux scripts persist but may go stale until the custom Intel-toolchain PG/PostGIS build resumes. GH Actions runner is DISABLED — local functionality only.

## Machine & locations

- i9-14900KS (24c), RTX 4060 Ti + GTX 1080 Ti (GPU phase: AFTER ingest/export proofing, as batch-math accelerators — never the query path; driver pinned to keep Pascal alive, CUDA 12.9/13.x).
- Toolchain: Intel oneAPI 2025.3 (icx — use for BOTH C and C++ on Windows; icpx is the GNU-driver and rejects MSVC-style flags), VS2022+2026 (CMake/Ninja bundled), Windows SDK 26100 (rc/mt pinned by absolute path).
- Runtime PG: EDB PostgreSQL 18.1 (`C:\Program Files\PostgreSQL\18`), PostGIS 3.7.0dev available. Bundles libxml2 WITH headers+import lib (used for engine LibXml2).
- Data: `D:\Data\Ingest` = the /vault/Data equivalent (full Unicode 17.0.0 mirror under Unicode\Public\17.0.0; all ladder corpora; test-data\text book library). `D:\Models\hub` = /vault/models (HF layout). `D:\LlamaCPP` = behavioral-harness binaries (llama-completion.exe). Junctions make hardcoded /vault paths resolve: `D:\vault\Data → D:\Data\Ingest`, `D:\vault\models → D:\Models\hub` (CLI cwd must be on D:).
- Deploy home (zero-admin): `D:\Data\Postgres\laplace\{lib, share\extension}`.
- DB auth: host localhost, user postgres, password postgres. LAPLACE_DB for the CLI: `Host=localhost;Username=postgres;Password=postgres;Database=laplace`. Two-DB law still applies conceptually (laplace vs laplace-dev).

## Hard-won platform laws

1. **Harness env conflict**: Claude-Code shells inject `NoDefaultCurrentDirectoryInExePath=1`, which breaks Intel setvars.bat's `pushd + call vars.bat` pattern (every component prints `'vars.bat' is not recognized`; LIB never gets Intel dirs → LNK1104 libircmt.lib). Fix: `set "NoDefaultCurrentDirectoryInExePath=" ` before calling setvars. env.cmd does this. User terminals are unaffected.
2. **PG win32 dlopen is plain LoadLibrary** (src/port/win32dlopen.c) — NO dependent-DLL search beside extension modules. Therefore extensions STATIC-link the engine: `laplace_core_static` + `laplace_dynamics_static` (sequential static MKL ⇒ no TBB runtime dep); resulting DLLs depend on system runtimes only.
3. **PG-18 path GUCs use SEMICOLON list separators on Windows** and `extension_control_path` entries are SHAREDIRs (PG appends /extension). Wired live, no admin/restart: `ALTER SYSTEM SET extension_control_path='$system;D:/Data/Postgres/laplace/share'; ALTER SYSTEM SET dynamic_library_path='$libdir;D:/Data/Postgres/laplace/lib'; SELECT pg_reload_conf();`
4. **Bare module names**: control/SQL must reference `laplace_substrate`, not `$libdir/...` — `$libdir`-prefixed paths bypass dynamic_library_path entirely. gen-sql.ps1 enforces.
5. **.cmd files MUST be CRLF** (cmd's LF bug eats leading characters of lines). Convert after authoring: `sed -i 's/\r$//; s/$/\r/'`.
6. **PowerShell -replace is case-INSENSITIVE** — macro/token substitution scripts must use `-creplace` (the LAPLACE_GEOM_VERSION vs laplace_geom_version() incident).
7. **Column law**: attestation/physicality column is `type`, never `kind` — surviving `kind` column refs are refactor residue (ContentRoundtrip incident, fixed).
8. **Concurrent runners lock CLI binaries** — ingest-text.cmd builds a SIDECAR copy (`%TEMP%\laplace-cli-sidecar`) so document ingestion runs while ladder runners hold bin\Release. (Guard TODO: skip rebuild when sidecar exists.)
9. **pg_regress.exe ships with EDB** (lib\pgxs\src\test\regress\) and needs Git's diff on PATH.
10. Engine UCD inputs: pass the four cache-derived vars explicitly when repointing (LAPLACE_UCD_PATH alone doesn't refresh derived cached paths).

## Script catalog (`scripts\win\`)

| script | purpose |
|---|---|
| `env.cmd` | THE environment chain: harness-var unset → setvars → SDK rc/mt pins (LAPLACE_RC/MT) → CMake/Ninja → TBB/MKL/compiler bins → build-tree DLL dirs → LAPLACE_ROOT |
| `build-engine.cmd` | configure+build `build-win`: icx/icx, Ninja, WINDOWS_EXPORT_ALL_SYMBOLS, BLAKE3_SIMD_TYPE=none, BUILD_TESTING=ON, UCD@D:\Data\Ingest, PG-bundled libxml2 |
| `build-extensions.cmd` | configure+build `build-win-ext` standalone (`-S extension`): pg_config discovery, static engine import, trimmed lwgeom |
| `install-extensions.cmd` | gen-sql.ps1 → stage `D:\Data\Postgres\laplace` → GUC wiring + reload → availability probe. Idempotent redeploy. |
| `gen-sql.ps1` | the .sql.in pipeline replica: configured sqldefines include, case-SENSITIVE macro expansion, bare MODULE_PATHNAME, geom @extschema@. strip |
| `regress.cmd` | per-extension fresh DBs + pg_regress (geom: hash128 st_4d; substrate: bootstrap glicko2_aggregate entities_exist_bitmap consensus_signed consensus_period converse) |
| `test-engine.cmd` | ctest over build-win (RUN SERIAL — gguf tests share a fixed temp path and race under -j) |
| `e2e.cmd <sources…>` | DB ensure + extensions + CLI build + sequential `ingest` per arg (the ladder driver) |
| `ingest-text.cmd <files…>` | sidecar CLI build + `db-roundtrip` per file (the document/book on-ramp) |
| `converse.cmd "question"` | the demo: psql :'q' interpolation into laplace.converse() |

Standard loop: `build-engine` → `build-extensions` → `install-extensions` → `regress` → `e2e unicode iso639 wordnet …` → `converse "what is a dog"`. No step requires elevation, ever.

## Build outputs

`build-win\`: 3 engine DLLs + static variants + laplace_ucd_tables_emit.exe + perfcache (85.4 MB blob) + 289 tests. `build-win-ext\`: laplace_geom.dll / laplace_substrate.dll (self-contained) + lwgeom_static. Stage: control + `<ext>--0.1.0.sql` + DLLs under D:\Data\Postgres\laplace.

## Session verification record (2026-06-07)

Engine 289/289 (serial 26.2 s) incl. UAX#29/#15 conformance vs UCD 17.0.0; perfcache emit byte-deterministic; pg_regress 8/8 BYTE-IDENTICAL to Linux-generated expected outputs (hash128 265 ms · st_4d 51 · bootstrap 6690 · glicko2 45 · bitmap 54 · signed 67 · period 115 · converse 90); cold start→converse 4:54.86; ladder + library throughputs and query latencies in INGESTION.md / RECEIPTS.md. dotnet solution builds 0W/0E.
