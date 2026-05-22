# OPERATIONS.md — Build / Run / Launch / Update / Query

The iteration framework. How everything is supposed to work — defined in advance so neither human nor agent has to guess.

The canonical command runner is **`just`** (Justfile at project root). Scripts live in `scripts/`. Operations involving the database, the engine library, and the C# app are unified under `just`.

---

## Prerequisites (verified on dev machine)

Per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md), all direct C/C++ deps are git submodules under `external/` and build under `/opt/laplace/`. The only non-submodule direct dep is Intel oneAPI (vendor compiler + runtime).

### System (apt — supporting only)

| Component | Version | Purpose |
|---|---|---|
| OS | Ubuntu 22.04 LTS | — |
| CPU | x86_64 with AVX2 (AVX-512 on deployment targets) | verified via `lscpu` + `grep -o 'avx[a-z0-9_]*' /proc/cpuinfo` |
| Intel oneAPI | 2026.0.0 | `/opt/intel/oneapi/`; source `setvars.sh` before build |
| .NET SDK | 10.0.107 | `/usr/lib/dotnet/` |
| GCC / Clang | 11.4 / 14 | fallback compilers when oneAPI is not sourced |
| CMake | 3.22+ | top-level build orchestrator (ADR 0032) |
| Ninja | 1.10+ | CMake generator |
| build-essential, autoconf, automake, libtool, pkg-config, perl, bison, flex, gettext | latest apt | build-time tooling for submodule builds |
| libxml2-dev, libicu-dev, libsqlite3-dev | latest apt | supporting libs for PROJ + PostgreSQL configure |

Build-environment apt packages are installed by `bootstrap_build_environment` in `scripts/bootstrap-laplace-runner.sh`.

### Submodule deps under `external/` (built into `/opt/laplace/`)

| Component | Version | Submodule | Built to | Build script |
|---|---|---|---|---|
| PostgreSQL | 18 (REL_18_0) | `external/postgresql/` | `/opt/laplace/pgsql-18/` | `scripts/build-pg.sh` |
| PostGIS | 3.6.3 | `external/postgis/` | under PG prefix | `scripts/build-postgis.sh` |
| PROJ | 9.4.1 | `external/proj/` | `/opt/laplace/proj/` | `scripts/build-proj.sh` |
| GEOS | 3.12.2 | `external/geos/` | `/opt/laplace/geos/` | `scripts/build-geos.sh` |
| GDAL | v3.9.3 | `external/gdal/` | `/opt/laplace/gdal/` | `scripts/build-gdal.sh` |
| Eigen | 3.4.0 | `external/eigen/` | header-only (INTERFACE) | — |
| Spectra | v1.2.0 | `external/spectra/` | header-only (INTERFACE) | — |
| BLAKE3 | 1.5.4 | `external/blake3/` | `add_subdirectory` from engine | — |
| GoogleTest | 1.15+ | `external/googletest/` | `add_subdirectory` for ctest | — |
| tree-sitter | v0.22.6 | `external/tree-sitter/` | `/opt/laplace/tree-sitter/` | `scripts/build-tree-sitter.sh` (lands when first CodeDecomposer is needed) |

Submodule init: `git clone --recurse-submodules <repo>` on fresh clone, or `git submodule update --init --recursive` after a plain clone.

### One-command dep build

```sh
just build-deps          # invokes scripts/build-all-deps.sh — proj → geos → gdal → pg → postgis
# Or selectively:
scripts/build-all-deps.sh proj geos      # only build PROJ + GEOS
```

The dep build is idempotent: re-running skips already-built deps. To force a fresh build of one dep: `scripts/build-pg.sh --clean`.

Verify everything with: `just check-prereqs`

---

## The Justfile (command index)

```just
# Justfile — canonical command runner for Laplace
# Run `just` with no arguments to list all commands.

# === Environment ===

# Source Intel oneAPI environment (must run before build/test commands that need it)
source-oneapi:
    @echo "Run: source /opt/intel/oneapi/setvars.sh"

# Verify all prerequisites are installed
check-prereqs:
    @scripts/check-prereqs.sh

# === Build ===

# Build everything (engine + extension + app)
build: build-engine build-extension build-app

# Build the C/C++ engine library
build-engine:
    cd engine && cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icpx
    cd engine && cmake --build build

# Build the PostgreSQL extension.
# Current bridge path uses PGXS until Epic B/B' lands the full ADR 0032 CMake build.
build-extension:
    cd extension && make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

# Install the PG extension into the cluster.
# Current bridge path uses PGXS until Epic B/B' lands the full ADR 0032 CMake build.
install-extension: build-extension
    cd extension && sudo make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config install

# Build the C# app projects
build-app:
    cd app && dotnet build -c Release

# Build perf-cache from Unicode UCD (one-time)
build-perfcache:
    scripts/build-perfcache.sh

# === Launch ===

# Start Postgres (if not already running)
launch-db:
    sudo systemctl start postgresql || pg_ctlcluster 18 main start

# Create the Laplace database
create-db:
    sudo -u postgres createdb laplace
    sudo -u postgres psql -d laplace -c "CREATE EXTENSION postgis;"
    sudo -u postgres psql -d laplace -c "CREATE EXTENSION laplace;"

# Apply schema (entities, physicalities, attestations + indexes)
apply-schema:
    psql -d laplace -f extension/schema.sql

# Seed T0 codepoint entities from Unicode UCD
seed-t0: build-perfcache
    scripts/seed-t0.sh

# Full setup: launch + create + apply-schema + seed-t0
setup: launch-db install-extension create-db apply-schema seed-t0
    @echo "Laplace ready. Try: just query 'SELECT count(*) FROM entities;'"

# === Ingest ===

# Ingest a source (linguistic resource, AI model, text corpus)
# Usage: just ingest wordnet
#        just ingest model /vault/models/qwen3-1.5b
#        just ingest text-corpus /vault/Data/wikipedia-en
ingest source path="":
    scripts/ingest-source.sh {{source}} {{path}}

# === Query ===

# Run an arbitrary SQL query against the substrate
# Usage: just query "SELECT count(*) FROM entities WHERE tier = 0"
query sql:
    psql -d laplace -c "{{sql}}"

# Run a substrate cascade query (via engine, not raw SQL)
# Usage: just cascade "what does running mean"
cascade prompt:
    app/Laplace.Cli/bin/Release/net10.0/laplace-cli cascade --prompt "{{prompt}}"

# === Synthesis ===

# Run Substrate Synthesis with a recipe JSON
# Usage: just synthesize recipes/qwen3-roundtrip.json
synthesize recipe:
    app/Laplace.Cli/bin/Release/net10.0/laplace-cli synthesize --recipe {{recipe}}

# Round-trip test: ingest model → synthesize same architecture → load in llama.cpp → chat
# Usage: just roundtrip /vault/models/qwen3-1.5b
roundtrip model_path:
    scripts/roundtrip.sh {{model_path}}

# === Verify ===

# Run all integrity checks (determinism, FK, perf-cache vs DB)
verify: verify-determinism verify-fk verify-perfcache

verify-determinism:
    scripts/verify-determinism.sh

verify-fk:
    psql -d laplace -f scripts/verify-fk.sql

verify-perfcache:
    scripts/verify-perfcache.sh

# === Status ===

# Show agent-tracked progress + open blockers
status:
    @cat .agent/status/STATE.md
    @echo ""
    @echo "=== Blockers ==="
    @cat .agent/status/blockers.md 2>/dev/null || echo "(none)"

# === Test ===

# Run all tests (engine + extension + app + integration)
test: test-engine test-extension test-app test-integration

test-engine:
    cd engine && cmake --build build --target test
    cd engine/build && ctest --output-on-failure

test-extension:
    cd extension && make installcheck PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

test-app:
    cd app && dotnet test -c Release

test-integration:
    scripts/test-integration.sh

# === Clean ===

clean: clean-engine clean-extension clean-app

clean-engine:
    rm -rf engine/build

clean-extension:
    cd extension && make clean

clean-app:
    cd app && dotnet clean

# Wipe the database (destructive — confirms)
nuke-db:
    @read -p "DROP database laplace? [y/N] " confirm; [[ "$$confirm" == "y" ]] || exit 1
    sudo -u postgres dropdb laplace
```

---

## Command reference (deeper than the Justfile)

### Build

The three engine libraries (`liblaplace_core.so`, `liblaplace_dynamics.so`, `liblaplace_synthesis.so` — per ADR 0024) are built once per Postgres major version + Unicode version. Rebuilds for code changes are incremental via ninja. Outputs under `engine/build/{core,dynamics,synthesis}/`.

The PG extension links the engine library. Install copies `laplace.so`, `laplace.control`, and the SQL files into the Postgres extension directory.

The C# app projects link the engine library via P/Invoke (`[DllImport("laplace_engine")]`).

### Launch

`just setup` is the one-time bootstrap. After that:

- `just launch-db` starts Postgres
- The extension is auto-loaded when a query uses one of its functions (since it's installed cluster-wide)

### Ingest

Each source plugin has a corresponding `scripts/ingest-<source>.sh` wrapper. The unified `just ingest <source> [path]` dispatches to the right one.

Per-source procedure:

1. Plugin reads source content (files, URLs, etc.)
2. Engine decomposes via the appropriate `IDecomposer`
3. Engine extracts attestations via the source's `ISource.extract_attestations`
4. For AI model sources: engine runs Procrustes alignment → physicalities
5. Pre-baked rows bulk-`COPY`'d into Postgres
6. Source's status logged to `.agent/status/STATE.md`

### Query

- Raw SQL: `just query "<sql>"` → standard psql
- Cascade inference: `just cascade "<prompt>"` → prompt is ingested as substrate content, then one compiled cascade call walks indexed attestations through the engine
- Substrate Synthesis: `just synthesize <recipe.json>` → emits a sparse model file from substrate state

`just cascade` is not a raw SQL graph traversal and not an app-layer loop issuing repeated SELECTs. The CLI calls into the app layer, which invokes the database SRF/operator surface; the C/C++ engine owns prompt decomposition, context entity creation/reference, frontier management, A*, tier transitions, source-scope filtering, effective-score ordering, and abstention. PostgreSQL supplies indexes and MVCC visibility.

Prompts have no context-window primitive. A prompt can be ephemeral or durable by policy, but either way it becomes content in the tiered entity DAG before traversal begins. The practical limits are ingestion cost, storage policy, and traversal budget.

Traversal modes are operational choices:

- `strict` — high effective mu, low RD, trusted source scopes; abstain when support is weak or conflicting
- `speculative` — allow uncertain paths but return uncertainty and source traces
- `creative` / `fiction` — allow lower-rated or context-marked walks under explicit mode labels

### Round-trip comparison target

Chunk 8's `just roundtrip <model_path>` implementation compares three surfaces under fixed prompt and sampler settings:

```text
stock source model      → original model package loaded directly
native substrate        → prompt ingestion + compiled cascade over source-scoped substrate state
synthesized export      → GGUF emitted from the same source-scoped recipe/scope
```

The default smoke prompt is:

```text
Hello! Tell me something interesting.
```

For source-scoped tests, differences should point to the model-ingest codec, sparse gate settings, synthesis writer, or sampler/runtime settings. Broader consensus synthesis may intentionally diverge by changing source scope and trust policy.

### Update

- **Schema migrations** are additive only. New migration file lives in `extension/<name>/sql/upgrade/<from>--<to>.sql.in`. Built artifact `<name>--<from>--<to>.sql` is generated by the SQLPP step (per ADR 0034). Apply via `ALTER EXTENSION <name> UPDATE TO '<to>';`
- **Engine library updates** require rebuild + reinstall + Postgres restart (the engine `.so`s are loaded by the postmaster via the extensions).
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
5. Rebuild: `just build` (or `cmake --build build`). The built artifact `laplace_geom--0.1.0.sql` appears under `build/extension/laplace_geom/`.
6. Reinstall + verify: `just install-extension && just db-up && just test-extension`.

### Verify

- `just verify` runs all integrity checks. Required before any commit touching hot-path code.
- Specific checks:
  - Determinism: re-derive perf-cache from UCD; compare byte-for-byte to stored perf-cache.
  - FK integrity: SQL queries verifying no orphans in physicalities / attestations.
  - Perf-cache vs. DB: every T0 entity in DB has matching perf-cache row.

### Status

- `just status` reads `.agent/status/STATE.md` + blockers.
- The verification agent updates STATE.md after successful verifications.
- The ingestion-pipeline agent updates STATE.md after successful ingestions.

---

## File-system layout (operational)

```
laplace/                              ← project root
├── engine/build/                     ← engine build artifacts (gitignored)
├── extension/                        ← PG extension source + Makefile
├── app/                              ← C# projects
├── scripts/                          ← bash/python operational scripts
├── data/                             ← gitignored: perf-cache binary, intermediate artifacts
│   └── perfcache.bin                 ← built from UCD
├── recipes/                          ← Substrate Synthesis recipe JSONs
└── .agent/status/                    ← agent-tracked status
```

Data on the user's machine (NOT in the repo):

- `/vault/models/` — ingested model packages (Qwen3, Llama, etc.)
- `/vault/Data/` — linguistic resources, text corpora
- `/data/models/` — secondary model collection
- Postgres data: `/var/lib/postgresql/18/main/`

---

## Recipe JSON examples

Located in `recipes/`. Examples:

```json
// recipes/qwen3-roundtrip.json — default round-trip of Qwen3-1.5B
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-roundtrip",
  "knowledge_scope": { "include_sources": ["qwen3-1.5b"], "rating_threshold": 0.5 },
  "output_format": "gguf"
}

// recipes/qwen3-consensus.json — Qwen3 architecture, knowledge from multiple sources
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-consensus",
  "knowledge_scope": {
    "include_sources": ["qwen3-1.5b", "llama-3.2-1b", "wikipedia_en", "wordnet"],
    "rating_threshold": 0.4
  },
  "output_format": "gguf"
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
| `CREATE EXTENSION laplace` fails | Extension not installed | `just install-extension` |
| `function laplace_distance_4d does not exist` | Extension installed but not loaded | Reload schema or reconnect |
| Perf-cache hash mismatch with DB seed | Mismatched Unicode versions | Rebuild both from the SAME UCD version: `just build-perfcache && just seed-t0` |
| Ingestion fails with FK violation | T0 not seeded | `just seed-t0` |
| `engine_last_error` shows non-zero | Engine-side error | Check stderr; consult [.claude/agents/verification.md](.claude/agents/verification.md) |
| GGUF file fails to load in llama.cpp | Format version mismatch | Verify `LLAMA_FILE_VERSION` against llama.cpp version; rebuild |

---

## Things explicitly NOT in this file

- **Phase-by-phase build plan** — lives in `.agent/status/STATE.md` and per-chunk planning docs. This file is the operational interface; the plan is separate.
- **Tuning knobs** for Glicko-2, A* heuristic, lottery-ticket criteria — these are configured at execution time, not standardized.
- **Per-source ingestion specifics** — each source plugin has its own README in `engine/src/sources/<source>/README.md`.
