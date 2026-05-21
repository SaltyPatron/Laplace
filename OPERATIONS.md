# OPERATIONS.md — Build / Run / Launch / Update / Query

The iteration framework. How everything is supposed to work — defined in advance so neither human nor agent has to guess.

The canonical command runner is **`just`** (Justfile at project root). Scripts live in `scripts/`. Operations involving the database, the engine library, and the C# app are unified under `just`.

---

## Prerequisites (verified on dev machine)

| Component | Version | Path / source |
|---|---|---|
| OS | Ubuntu 22.04 LTS | — |
| CPU | x86_64 with AVX2 (AVX-512 on deployment targets) | `lscpu` |
| Postgres | 18.3 | `/usr/lib/postgresql/18` |
| PostGIS | 3.6.3 | system pkg |
| Intel oneAPI | 2026.0.0 | `/opt/intel/oneapi/` (source `setvars.sh`) |
| GCC / Clang | 11.4 / 14 | system |
| CMake | 3.22+ | system |
| Ninja | 1.10+ | system |
| .NET SDK | 10.0.107 | `/usr/lib/dotnet/` |
| Eigen | 3.4.0 | `/usr/include/eigen3/` |
| Spectra | TBD | `engine/third_party/spectra/` (header-only, downloaded) |
| tree-sitter | TBD | `sudo apt install libtree-sitter-dev` |
| libxxhash | 0.8.1 | system pkg |
| ICU | 70.1+ | `/usr/include/unicode/` |

Verify with: `just check-prereqs`

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

# Build the PostgreSQL extension (PGXS)
build-extension:
    cd extension && make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

# Install the PG extension into the cluster
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

The engine library is built once per Postgres major version + Unicode version. Rebuilds for code changes are incremental via ninja. Output: `engine/build/liblaplace_engine.so`.

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
- Cascade inference: `just cascade "<prompt>"` → goes through the engine; substrate query, not SQL
- Substrate Synthesis: `just synthesize <recipe.json>` → emits a model file

### Update

- **Schema migrations** are additive only. New migration file lives in `extension/laplace--<from>--<to>.sql`. Apply via `ALTER EXTENSION laplace UPDATE TO '<to>';`
- **Engine library updates** require rebuild + reinstall + Postgres restart (since `laplace.so` is loaded by the postmaster).
- **Source re-ingestion:** idempotent (UPSERT on attestation dedup key). Re-running `just ingest <source>` is safe.

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
