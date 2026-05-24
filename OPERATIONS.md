# OPERATIONS.md — Build / Run / Launch / Update / Query

The iteration framework. How everything is supposed to work — defined in advance so neither human nor agent has to guess.

The canonical command runner is **`just`** (Justfile at project root). Scripts live in `scripts/`. Operations involving the database, the engine library, and the C# app are unified under `just`.

---

## Prerequisites (verified on dev machine)

Per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md), all direct C/C++ deps are git submodules pinned in `.gitmodules`. Per [ADR 0046](docs/adr/0046-persistent-submodule-cache.md), the canonical source checkouts live at **`/opt/laplace/external/<dep>/`** as non-bare working trees maintained by `scripts/sync-external.sh` at the `.gitmodules` pin. CMake reads from `$LAPLACE_EXTERNAL` (default `/opt/laplace/external`) — no workspace `external/` is required for the build. The only non-submodule direct dep is Intel oneAPI (vendor compiler + runtime) at `/opt/intel/oneapi/`.

### System (apt — supporting only)

| Component | Version | Purpose |
|---|---|---|
| OS | Ubuntu 22.04 LTS | — |
| CPU | x86_64 with AVX2 (AVX-512 on deployment targets) | verified via `lscpu` + `grep -o 'avx[a-z0-9_]*' /proc/cpuinfo` |
| Intel oneAPI | 2026.0.0 | `/opt/intel/oneapi/`; sourced automatically by the build via `cmake/toolchains/intel-oneapi.cmake` |
| PostgreSQL 18 + PostGIS 3 | stock apt | `postgresql-18` + `postgresql-18-postgis-3`; runtime cluster the substrate connects to (per [ADR 0045](docs/adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md)) |
| .NET SDK | 10.0.107 | `/usr/lib/dotnet/` |
| GCC | 11.4 | dep-chain toolchain (per [ADR 0038](docs/adr/0038-unified-deps-cmake-pipeline-gcc-toolchain.md)) |
| CMake | 3.22+ | top-level build orchestrator (ADR 0032) |
| Ninja | 1.10+ | CMake generator |
| build-essential, autoconf, automake, libtool, pkg-config, perl, bison, flex, gettext | latest apt | build-time tooling for submodule builds |
| libxml2-dev, libicu-dev, libsqlite3-dev | latest apt | supporting libs for PROJ + PostgreSQL configure |

Build-environment apt packages are installed by `bootstrap_build_environment` in `scripts/bootstrap-laplace-runner.sh`.

### Submodule deps under `/opt/laplace/external/` (built into `/opt/laplace/`)

| Component | Version | Submodule path (workspace) | Source consumed by build | Built to |
|---|---|---|---|---|
| PostgreSQL | 18 (REL_18_0) | `external/postgresql/` | `${LAPLACE_EXTERNAL}/postgresql/` | `/opt/laplace/pgsql-18/` |
| PostGIS | 3.6.3 | `external/postgis/` | `${LAPLACE_EXTERNAL}/postgis/` | under PG prefix |
| PROJ | 9.4.1 | `external/proj/` | `${LAPLACE_EXTERNAL}/proj/` | `/opt/laplace/proj/` |
| GEOS | 3.12.2 | `external/geos/` | `${LAPLACE_EXTERNAL}/geos/` | `/opt/laplace/geos/` |
| GDAL | v3.9.3 | `external/gdal/` | `${LAPLACE_EXTERNAL}/gdal/` | `/opt/laplace/gdal/` |
| Eigen | 3.4.0 | `external/eigen/` | `${LAPLACE_EXTERNAL}/eigen/` | header-only (INTERFACE) |
| Spectra | v1.2.0 | `external/spectra/` | `${LAPLACE_EXTERNAL}/spectra/` | header-only (INTERFACE) |
| BLAKE3 | 1.5.4 | `external/blake3/` | `${LAPLACE_EXTERNAL}/blake3/` | `add_subdirectory` from engine |
| GoogleTest | 1.15+ | `external/googletest/` | `${LAPLACE_EXTERNAL}/googletest/` | `add_subdirectory` for ctest |
| tree-sitter | v0.22.6 | `external/tree-sitter/` | `${LAPLACE_EXTERNAL}/tree-sitter/` | `/opt/laplace/tree-sitter/` |

`$LAPLACE_EXTERNAL` defaults to `/opt/laplace/external/`. Override via env var only when working against an alternate checkout location.

Sync the cache to the `.gitmodules` pin (idempotent — no-op when nothing moved):

```sh
scripts/sync-external.sh
```

For local-only editing, developers may also keep a workspace `external/<dep>/` via `git submodule update --init <path>`; this is ergonomics, not load-bearing. The build reads from `$LAPLACE_EXTERNAL`.

### One-command dep build

```sh
just build-deps
```

This runs `cmake -B build/deps -S external && cmake --build build/deps -j` against `external/CMakeLists.txt` (per [ADR 0038](docs/adr/0038-unified-deps-cmake-pipeline-gcc-toolchain.md)). Each dep is an `ExternalProject_Add` built in its own isolated CMake context; ordering is encoded via `DEPENDS` (proj → gdal; postgis → pg + geos + proj + gdal). Stamp-based caching — re-runs with the same submodule SHAs are sub-second no-ops; one-time clean build is ~10-12 min.

gcc toolchain (`cmake/toolchains/gcc-deterministic.cmake`) is used for every dep; icpx is reserved for the engine where it earns its slot via oneMKL/Spectra/TBB.

Verify everything with: `just check-prereqs`

---

## The Justfile (command index)

The Justfile is the authoritative reference — `just --list` is always current. Below is a guide to what each recipe does and when to use it.

### Environment

| Recipe | Purpose |
|---|---|
| `just check-prereqs` | Verify host has the right system deps + oneAPI + PG + .NET. |

### Layer 0 — bootstrap (one-time, sudo)

Per [ADR 0018](docs/adr/0018-three-layer-architecture.md) + [ADR 0019](docs/adr/0019-laplace-runner-system-account.md). `scripts/bootstrap-laplace-runner.sh` is the **only** privileged surface — creates the `laplace-runner` system account, PG roles, peer auth, `/opt/laplace/{external,lib,share,include,bin}` with correct ownership + mode 2775, ld.so.cache registration of `/opt/laplace/lib`, and the PG `extension_control_path` / `dynamic_library_path` GUCs per [ADR 0045](docs/adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md).

| Recipe | Purpose |
|---|---|
| `sudo just bootstrap` | Run Layer 0 setup (one-time per machine; idempotent). |
| `sudo just bootstrap-status` | Show current Layer 0 state. |
| `sudo just bootstrap-reset` | Tear down Layer 0 state (destructive — use only on a host being repurposed). |

### One-shot host setup

`scripts/setup-host.sh` orchestrates bootstrap (Layer 0) + DbUp migrations (Layer 1) in sequence — for fresh hosts or after a reset.

| Recipe | Purpose |
|---|---|
| `just setup-host` | Layer 0 + Layer 1. |
| `just setup-host-status` | Status of both. |
| `just setup-host-reset` | Reset both. |

### Build

Per [ADR 0032](docs/adr/0032-unified-cmake-build-pipeline.md): one top-level CMake tree drives 3 engine `.so` + 2 PG extensions + 2 preprocessed SQL install scripts.

| Recipe | Purpose |
|---|---|
| `just submodule-sanity` | Apply CRLF-fixture overrides for 5 of 303 tree-sitter grammars (silent no-op when clean). Auto-runs before `build-deps`. |
| `just build-deps` | Build PROJ → GEOS → GDAL → PG → PostGIS → tree-sitter from `${LAPLACE_EXTERNAL}/<dep>/` to `/opt/laplace/<dep>/`. gcc toolchain. Stamp-cached. |
| `just build` | Build engine + extensions. icpx toolchain (`cmake/toolchains/intel-oneapi.cmake`). Defaults: `CMAKE_INSTALL_PREFIX=/opt/laplace`, `LAPLACE_INSTALL_STAGED=ON`, `LAPLACE_PG_PREFIX=/usr/lib/postgresql/18` (stock PG; override via env var for the custom-built PG at `/opt/laplace/pgsql-18`). |
| `just install` | `cmake --install build` — sudo-free thanks to `LAPLACE_INSTALL_STAGED=ON` (extensions land in `/opt/laplace/{lib,share}/postgresql/$PG_MAJOR`; PG finds them via the conf paths set by `bootstrap_pg_extension_paths`). |
| `just install-laplace-prefix` | Legacy alias for `just install` — kept so existing CI/docs keep working. |
| `just build-app` | `dotnet build Laplace.slnx -c Release`. |
| `just build-migrations` | `dotnet build Laplace.Migrations`. |
| `just build-perfcache` | Build perf-cache from UCD (one-time). |
| `just clean` | Wipe `build/`. Preserves `/opt/laplace/*` dep installs. |

#### Custom PG build (optional path)

The substrate normally runs against the system PG (`/usr/lib/postgresql/18`) with extensions staged at `/opt/laplace/{lib,share}/postgresql/18` per [ADR 0045](docs/adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md). For benchmarking against an Intel-toolchain custom PG build:

```sh
just build-deps                                  # builds /opt/laplace/pgsql-18/
LAPLACE_PG_PREFIX=/opt/laplace/pgsql-18 just build install
# (then point a separately-managed cluster at the custom binaries)
```

The custom-PG cluster path is not the runtime the substrate depends on — it remains available as a build target for future hermetic-cluster work.

### Layer 1 — extension lifecycle (DbUp)

Per [ADR 0021](docs/adr/0021-dbup-for-migrations.md) + [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md). DbUp orchestrates extension lifecycle (CREATE EXTENSION postgis + laplace_geom + laplace_substrate, version pins, cross-extension setup); it does NOT define substrate schema. Substrate schema lives in `extension/laplace_substrate/sql/`.

| Recipe | Purpose |
|---|---|
| `just launch-db` | Start the system Postgres cluster if not running. |
| `just db-up` | Apply DbUp migrations (creates DB if missing, runs CREATE EXTENSION ladder). |
| `just db-status` | Show current migration state. |
| `just db-reset` | Drop SchemaVersions only — preserves substrate data; next `db-up` re-applies migrations. |
| `just db-nuke` | DROP DATABASE laplace + re-create empty. Loses all substrate data. |
| `just migrate-new <name>` | Generate a new timestamped migration file in `db/migrations/`. |

`just db-up` depends on `just install` — installing the latest extension SQL before DbUp tries to CREATE EXTENSION ensures the staged-extension files are in place.

### Seed

| Recipe | Purpose |
|---|---|
| `just seed-t0` | Seed T0 codepoint entities from UCD (1,114,112 entities). Depends on `build-perfcache`. |
| `just setup` | `launch-db` → `db-up` → `seed-t0`. Convenience composite for a clean substrate. |

### Ingest

| Recipe | Purpose |
|---|---|
| `just ingest <source> [path]` | Dispatch to `scripts/ingest-source.sh` (per-source plugin invocation). |

### Query / Cascade

| Recipe | Purpose |
|---|---|
| `just query <sql>` | Run raw SQL via `psql -d laplace`. |
| `just cascade <prompt>` | Run a compiled cascade query via the CLI (prompt → ingestion → engine SRF/operator → response). |

### Synthesis

| Recipe | Purpose |
|---|---|
| `just synthesize <recipe.json>` | Emit a sparse native Synthesis package + optional proof exports (e.g., GGUF). |
| `just roundtrip <model_path>` | Ingest model → synthesize same architecture → load in llama.cpp → chat (Chunk 8 milestone). |

### Verify

| Recipe | Purpose |
|---|---|
| `just verify` | All integrity checks (determinism, FK, perf-cache vs DB). Required before any commit touching hot-path code. |
| `just verify-determinism` | Re-derive perf-cache from UCD; byte-compare to stored. |
| `just verify-fk` | FK integrity across physicalities + attestations. |
| `just verify-perfcache` | Cross-check perf-cache rows vs DB entities. |

### Status

| Recipe | Purpose |
|---|---|
| `just status` | Last 10 commits (`git log --oneline -10`). Project state lives in GitHub issues + ADRs, not in a status file. |

### Test

Three test surfaces per [STANDARDS.md Testing](STANDARDS.md):

| Recipe | Purpose |
|---|---|
| `just test` | Composite: `test-engine` → `regress` → `test-app`. |
| `just test-engine` | ctest over GoogleTest binaries. Excludes pg_regress. Requires `just build` first. |
| `just regress` | pg_regress smoke tests for `laplace_geom` + `laplace_substrate` against the running system PG with staged extensions installed (per [#196](https://github.com/SaltyPatron/Laplace/issues/196)). Requires `just install` first. CTest fixtures sequence setup/teardown. |
| `just test-app` | `dotnet test Laplace.slnx -c Release` (xUnit + Testcontainers; requires Docker daemon). |

---

## Command reference (deeper than the Justfile)

### Build internals

Three engine libraries — `liblaplace_core.so`, `liblaplace_dynamics.so`, `liblaplace_synthesis.so` (per [ADR 0024](docs/adr/0024-engine-modularization.md)) — built once per Postgres major version + Unicode version. Code-change rebuilds are incremental via ninja. Outputs land under `build/engine/{core,dynamics,synthesis}/`.

The two PG extensions (`laplace_geom`, `laplace_substrate` per [ADR 0025](docs/adr/0025-pg-extension-modularization.md)) link the engine libraries. `cmake --install build` copies them with `LAPLACE_INSTALL_STAGED=ON` to `/opt/laplace/{lib,share}/postgresql/$PG_MAJOR/`.

The C# app projects link the engine libraries via P/Invoke (`[DllImport("laplace_core")]` etc.) per [ADR 0026](docs/adr/0026-csharp-project-structure.md).

### Launch internals

The substrate runs against the **system PG cluster** (`/usr/lib/postgresql/18`) per ADR 0045. The PG `postgresql.conf` is amended by `bootstrap_pg_extension_paths` to add `/opt/laplace/{lib,share}/postgresql/18` to `dynamic_library_path` and `extension_control_path`, so `CREATE EXTENSION laplace_geom` finds the staged files immediately after `just install`.

`laplace_admin` is a SUPERUSER role (per ADR 0045); DbUp connects as `laplace_admin` via peer auth and runs `CREATE EXTENSION` directly — no SECURITY DEFINER wrapper, no parallel cluster, no custom systemd unit.

### Ingest

Each source plugin has a corresponding `scripts/ingest-<source>.sh` wrapper. The unified `just ingest <source> [path]` dispatches.

Per-source procedure:

1. Plugin reads source content (files, URLs, etc.)
2. Engine decomposes via the appropriate `IDecomposer`
3. Engine extracts attestations via the source's `ISource.extract_attestations`
4. For AI model sources: engine runs Procrustes alignment → physicalities
5. Pre-baked rows bulk-`COPY`'d into Postgres

### Query

- Raw SQL: `just query "<sql>"` → standard psql
- Cascade inference: `just cascade "<prompt>"` → prompt is ingested as substrate content, then one compiled cascade call walks indexed attestations through the engine
- Substrate Synthesis: `just synthesize <recipe.json>` → emits a sparse native Synthesis package from substrate state, with optional proof/compatibility exports such as GGUF

`just cascade` is not a raw SQL graph traversal and not an app-layer loop issuing repeated SELECTs. The CLI calls into the app layer, which invokes the database SRF/operator surface; the C/C++ engine owns prompt decomposition, context entity creation/reference, frontier management, A*, tier transitions, source-scope filtering, effective-score ordering, and abstention. PostgreSQL supplies indexes and MVCC visibility.

Prompts have no context-window primitive. A prompt can be ephemeral or durable by policy, but either way it becomes content in the tiered entity DAG before traversal begins. The practical limits are ingestion cost, storage policy, and traversal budget.

Prompt-local content is useful immediately because it reuses existing entities and records a context trajectory. User claims inside that content stay prompt/session/source scoped unless explicit promotion and corroboration admit them to broader arenas. Operationally, hallucination and drift are diagnosed by inspecting traversal mode, source scope, evidence trace, and where the path left high-support attestations.

Traversal modes are operational choices:

- `strict` — high effective mu, low RD, trusted source scopes; abstain when support is weak or conflicting
- `speculative` — allow uncertain paths but return uncertainty and source traces
- `creative` / `fiction` — allow lower-rated or context-marked walks under explicit mode labels

### Round-trip comparison target

Chunk 8's `just roundtrip <model_path>` implementation compares three surfaces under fixed prompt and sampler settings:

```text
stock source model      → original model package loaded directly
native substrate        → prompt ingestion + compiled cascade over source-scoped substrate state
synthesized export      → native package emitted from the same source-scoped recipe/scope
proof export            → GGUF converted/emitted from the native package for llama.cpp validation
```

The default smoke prompt is:

```text
Hello! Tell me something interesting.
```

For source-scoped tests, differences should point to the model-ingest codec, sparse gate settings, synthesis writer, or sampler/runtime settings. Broader consensus synthesis may intentionally diverge by changing source scope and trust policy.

### Update

- **Schema migrations** are additive only. New migration file lives in `extension/<name>/sql/upgrade/<from>--<to>.sql.in`. Built artifact `<name>--<from>--<to>.sql` is generated by the SQLPP step (per [ADR 0034](docs/adr/0034-modular-sql-via-cpp-preprocessor.md)). Apply via `ALTER EXTENSION <name> UPDATE TO '<to>';`
- **Engine library updates** require rebuild + reinstall (`just install`). No PG restart needed in the staged-extension model when only the `.so` content changed; restart only when extension control / SQL signatures change.
- **Source re-ingestion:** idempotent (UPSERT on attestation dedup key). Re-running `just ingest <source>` is safe.

### Editing extension SQL (per ADR 0034)

Per [RULES.md R17](RULES.md), extension SQL is modular `.sql.in` files preprocessed via `cpp -traditional-cpp -w -P -Upixel -Ubool`. **Edit only the `.sql.in` sources — never the built `<name>--<version>.sql` artifact** (it will be overwritten on next `cmake --build`).

Layout per extension (e.g., `laplace_geom`):

```
extension/laplace_geom/sql/
├── sqldefines.h.in            ← shared CPP macros (function-volatility shortcuts, version gates)
├── laplace_geom.sql.in        ← main entry; #includes the modules below
├── 01_meta.sql.in             ← edit here for: laplace_geom_version() + identity checks
├── 02_hash128_type.sql.in     ← CREATE TYPE hash128 + I/O functions
├── 03_hash128_ops.sql.in      ← laplace_btree_hash128_ops opclass
├── 04_hilbert.sql.in          ← laplace_hilbert_encode/decode
├── 05_mantissa.sql.in         ← laplace_mantissa_pack/unpack
├── 06_st_4d.sql.in            ← ST_*_4d function family
├── 07_s3_opclass.sql.in       ← laplace_gist_s3_ops opclass
└── uninstall_laplace_geom.sql.in
```

Numeric prefixes (`NN_`) lock load order. To add a new function:

1. Find the right module file (or, for a wholly new concern, add a new `NN_module.sql.in` and include it from `laplace_geom.sql.in`).
2. Edit the `.sql.in`. Use `MODULE_PATHNAME` as the C-function lib placeholder (cpp will substitute the real `$libdir/laplace_geom` path); use the `LAPLACE_IMMUTABLE_STRICT` / `LAPLACE_VOLATILE_STRICT` macros from `sqldefines.h.in` for function modifiers.
3. Add a matching C wrapper in `extension/laplace_geom/src/laplace_geom.c` (PG_FUNCTION_INFO_V1 + the actual marshalling to the engine call).
4. Add a pg_regress test pair: `extension/laplace_geom/tests/sql/<name>.sql` + `extension/laplace_geom/tests/expected/<name>.out`.
5. Rebuild + reinstall: `just install` (depends on `just build`).
6. Verify: `just db-up && just regress`.

### Verify

- `just verify` runs all integrity checks. Required before any commit touching hot-path code.
- Specific checks:
  - Determinism: re-derive perf-cache from UCD; compare byte-for-byte to stored perf-cache.
  - FK integrity: SQL queries verifying no orphans in physicalities / attestations.
  - Perf-cache vs. DB: every T0 entity in DB has matching perf-cache row.

### Status

Project state lives in:

- **GitHub issues** — chunk progress, story tracking, blockers
- **`docs/adr/`** — accepted architectural decisions
- **`CHANGELOG.md`** — user-visible changes per release
- **`git log`** — full commit history

`just status` shows recent commits; chunk/story progress is at https://github.com/SaltyPatron/Laplace/issues. There is intentionally no `STATE.md` cadence file (it was tried in prior iterations and degraded into a conversation log — issues + ADRs are the durable record).

---

## File-system layout (operational)

```
laplace/                              ← project root
├── external/                         ← workspace submodule checkouts (developer ergonomics only)
├── build/                            ← top-level CMake build artifacts (gitignored)
│   └── deps/                         ← ExternalProject_Add stamp tree + per-dep build dirs
├── engine/{core,dynamics,synthesis}/ ← C/C++ engine source per ADR 0024
├── extension/{laplace_geom,laplace_substrate}/  ← PG extensions per ADR 0025
├── app/                              ← C# projects per ADR 0026
├── db/migrations/                    ← DbUp migrations per ADR 0021
├── scripts/                          ← bash operational scripts
├── data/                             ← gitignored: perf-cache binary, intermediate artifacts
│   └── perfcache.bin                 ← built from UCD
├── recipes/                          ← Substrate Synthesis recipe JSONs
└── docs/adr/                         ← Architecture Decision Records
```

Persistent state outside the repo:

- `/opt/laplace/external/` — canonical source for dep checkouts (per [ADR 0046](docs/adr/0046-persistent-submodule-cache.md))
- `/opt/laplace/{proj,geos,gdal,pgsql-18,tree-sitter}/` — built dep install prefixes (per ADR 0033)
- `/opt/laplace/{lib,share,include,bin}/` — engine library + staged-extension install (per ADR 0045)
- `/opt/intel/oneapi/` — Intel oneAPI toolchain + libraries
- `/var/lib/postgresql/18/main/` — system PG data directory

Data on the user's machine (NOT in the repo):

- `/vault/models/` — ingested model packages (Qwen3, Llama, etc.)
- `/vault/Data/` — linguistic resources, text corpora
- `/data/models/` — secondary model collection

---

## Recipe JSON examples

Located in `recipes/`. Examples:

```json
// recipes/qwen3-roundtrip.json — default round-trip of Qwen3-1.5B
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-roundtrip",
    "knowledge_scope": { "include_sources": ["qwen3-1.5b"], "effective_mu_policy": "source_scoped_roundtrip" },
    "output_format": "native_synthesis_package",
    "proof_exports": ["gguf"]
}

// recipes/qwen3-consensus.json — Qwen3 architecture, knowledge from multiple sources
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-consensus",
  "knowledge_scope": {
    "include_sources": ["qwen3-1.5b", "llama-3.2-1b", "wikipedia_en", "wordnet"],
    "effective_mu_policy": "arena_default"
  },
    "output_format": "native_synthesis_package",
    "proof_exports": ["gguf"]
}
```

---

## Logging and telemetry

- Engine logs: stderr, structured (one JSON line per event).
- Postgres logs: standard location `/var/log/postgresql/`.
- C# app logs: `Microsoft.Extensions.Logging` to stdout in dev; configurable for prod.
- Telemetry (Prometheus-compatible): exposed by the C# app on `:9090/metrics` (when running endpoint extensions).

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `CREATE EXTENSION laplace_geom` fails | Extension not installed at staged path | `just install` |
| `function laplace_distance_4d does not exist` | Extension installed but not loaded in the session | Reconnect or `SET search_path` |
| Perf-cache hash mismatch with DB seed | Mismatched Unicode versions | Rebuild both from the SAME UCD version: `just build-perfcache && just seed-t0` |
| Ingestion fails with FK violation | T0 not seeded | `just seed-t0` |
| `engine_last_error` shows non-zero | Engine-side error | Check stderr; consult [.claude/agents/verification.md](.claude/agents/verification.md) |
| GGUF proof export fails to load in llama.cpp | Format version mismatch or native-package conversion bug | Verify `LLAMA_FILE_VERSION` against llama.cpp version; rebuild the proof export from the native package |
| `cmake --build build/deps` fails on PROJ with `-fno-fast-math: not found` | Old icpx toolchain leaking into deps | Should not happen post-[ADR 0038](docs/adr/0038-unified-deps-cmake-pipeline-gcc-toolchain.md); ensure `cmake/toolchains/gcc-deterministic.cmake` is in effect for `build-deps` |
| `git submodule update` slow / rate-limited | Per-job init not using the `/opt/laplace/external/` cache | Use `scripts/sync-external.sh` instead (per [ADR 0046](docs/adr/0046-persistent-submodule-cache.md)) |

---

## Things explicitly NOT in this file

- **Tuning knobs** for Glicko-2, A* heuristic, lottery-ticket criteria — these are configured at execution time, not standardized.
- **Per-source ingestion specifics** — each source plugin has its own README in `engine/src/sources/<source>/README.md`.
