# DESIGN.md — Laplace Engineering Spec

**Status:** Authoritative engineering spec. Locked architectural decisions live here. Tuning decisions explicitly flagged.

Before working on any code, read [CLAUDE.md](CLAUDE.md), [GLOSSARY.md](GLOSSARY.md), [RULES.md](RULES.md), [STANDARDS.md](STANDARDS.md) — in that order.

---

## I. Schema

Three core tables. No event log. Postgres 18 + PostGIS 3.6.3. Per [STANDARDS "Storage-class discipline"](STANDARDS.md#storage-class-discipline) each column has a *purpose*: **content** (bytes being recorded), **metadata** (structural / housekeeping properties), **attestation** (typed knowledge edges with Glicko-2 ratings), **lookup** (PK/FK identity-resolution), or **index** (acceleration only). Columns are annotated below.

### `entities` — pure identity + tier + type

Entities are **identity-only**. They carry no geometry, no trajectory, no per-source representation. All of that lives in [`physicalities`](#physicalities--per-source-per-kind-4d-representations). One row per unique observed n-gram, content-addressed by BLAKE3-128 of the canonical (type-canonicalized) content bytes.

```sql
CREATE TABLE entities (
    id                 bytea            PRIMARY KEY CHECK (octet_length(id) = 16),  -- BLAKE3-128 of canonical content
    tier               smallint         NOT NULL CHECK (tier >= 0 AND tier < 256),
    type_id            bytea            NOT NULL REFERENCES entities(id),           -- e.g., Text, Pixel, WordNet_Synset, Model_Recipe, ...
    first_observed_by  bytea            REFERENCES entities(id),                    -- source entity that first observed this; nullable for bootstrap rows
    created_at         timestamptz      NOT NULL DEFAULT now()
);

CREATE INDEX entities_tier      ON entities USING btree (tier);
CREATE INDEX entities_type      ON entities USING btree (type_id);
CREATE INDEX entities_tier_type ON entities USING btree (tier, type_id);
CREATE INDEX entities_first_observed ON entities USING btree (first_observed_by) WHERE first_observed_by IS NOT NULL;
```

**Column purposes:**

- `id` — **lookup** (PK). BLAKE3-128 stored as `bytea(16)` (per ADR 0015). Column-role is `id`; value IS a hash. Raw bytes only — no hex, no casts.
- `tier` — **metadata** (structural; which tier of the Merkle DAG).
- `type_id` — **lookup** (FK to type-entity) + **metadata** (declares what KIND of thing this is — Text / Pixel / Patch / Region / Image / Audio_Frame / Model_Recipe / WordNet_Synset / UD_Sentence / ...). Type drives canonicalization rule, decomposition rule, reconstruction rule, and modality-applicable attestation kinds. Type-entities are themselves rows in `entities`, bootstrapped at install (per the planned ADR on bootstrap ordering).
- `first_observed_by` — **metadata** (provenance — which source first ingested this content). Nullable for the bootstrap type entities + substrate-canonical source entity.
- `created_at` — **metadata** (housekeeping).
- No geometry, Hilbert index, trajectory, radius_origin, or n_constituents on this table — those are **content** + **metadata** properties of a particular source/kind's view of the entity, which live on `physicalities`.

### `physicalities` — per-source, per-kind 4D representations

One-to-many entity→physicality. Each row is one **lens** on an entity provided by one **source**: CONTENT (decomposition view), BUILDING_BLOCK (used-as-constituent view), or PROJECTION (source-embedding-space view). Holds all geometry + trajectory + per-source metadata. Physicalities are projection/access structures: they support fuzzy candidate discovery, alignment, visualization, and indexing. They are not the knowledge layer; semantic state lives in typed attestations.

```sql
CREATE TABLE physicalities (
    id                 bytea            PRIMARY KEY CHECK (octet_length(id) = 16),
    entity_id          bytea            NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    source_id          bytea            NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    kind               smallint         NOT NULL,                                   -- 1=CONTENT, 2=BUILDING_BLOCK, 3=PROJECTION (extensible)
    coord              geometry         NOT NULL
                                        CHECK (ST_HasZ(coord) AND ST_HasM(coord)
                                               AND ST_GeometryType(coord) = 'ST_Point'),
    hilbert_index      bytea            NOT NULL,
    trajectory         geometry         CHECK (trajectory IS NULL
                                               OR (ST_HasZ(trajectory) AND ST_HasM(trajectory)
                                                   AND ST_GeometryType(trajectory) = 'ST_LineString')),
    radius_origin      double precision GENERATED ALWAYS AS (
        sqrt(ST_X(coord)^2 + ST_Y(coord)^2 + ST_Z(coord)^2 + ST_M(coord)^2)
    ) STORED,
    n_constituents     integer          NOT NULL DEFAULT 0 CHECK (n_constituents >= 0),
    alignment_residual double precision,                                            -- nullable; substrate-canonical = 0; PROJECTION carries the Procrustes residual
    source_dim         integer          CHECK (source_dim IS NULL OR source_dim > 0),
    observed_at        timestamptz      NOT NULL DEFAULT now(),
    UNIQUE (entity_id, source_id, kind)
);

CREATE INDEX physicalities_entity        ON physicalities USING btree (entity_id);
CREATE INDEX physicalities_source        ON physicalities USING btree (source_id);
CREATE INDEX physicalities_kind          ON physicalities USING btree (kind);
CREATE INDEX physicalities_coord_nd      ON physicalities USING gist  (coord gist_geometry_ops_nd);
CREATE INDEX physicalities_hilbert       ON physicalities USING btree (hilbert_index);
CREATE INDEX physicalities_radius        ON physicalities USING btree (radius_origin);
CREATE INDEX physicalities_residual      ON physicalities USING btree (alignment_residual) WHERE alignment_residual IS NOT NULL;
-- Trajectory column: GIST on mantissa-packed LINESTRINGs collapses to a fixed bounding box per ADR 0012,
-- so gist_geometry_ops_nd would filter nothing. Structural opclass replacement is tracked in §V.
```

**Column purposes:**

- `id` — **lookup** (PK). Physicality's own content-addressed identifier (BLAKE3 of canonical `(entity_id, source_id, kind, coord, trajectory)` bytes). Lets attestations or higher-tier physicalities reference a specific physicality if needed.
- `entity_id` / `source_id` — **lookup** (FKs to entities). Both are entity references; sources are themselves entities.
- `kind` — **metadata** (which lens: CONTENT / BUILDING_BLOCK / PROJECTION + future extensions). Implemented as `smallint` for compactness; values mirrored in a substrate-canonical lookup of kind-entities.
- `coord` — **content** (4D geometric position of this entity under this source/kind view).
- `hilbert_index` — **index-derived value** stored as a column (B-tree-indexable redundant projection of `coord` for 1D locality range scans).
- `trajectory` — **content** (mantissa-packed LINESTRING per ADR 0012 — the constituent sequence as this source decomposes the entity). NULL for T0 atoms (no constituents); NULL for BUILDING_BLOCK (the kind doesn't carry a downward decomposition); NULL for Vampire-mode AI ingestion (no weight-bytes preserved).
- `radius_origin` — **content-derived** STORED generated column (for cheap radial-abstraction queries).
- `n_constituents` — **metadata** (count of the trajectory's vertices; 0 for NULL trajectory).
- `alignment_residual` / `source_dim` — **metadata** (per-source ingestion quality + dimensionality). Substrate-canonical physicality leaves `alignment_residual` at 0 (no projection); PROJECTION-kind carries the Procrustes residual.
- `observed_at` — **metadata** (housekeeping).
- `(entity_id, source_id, kind)` UNIQUE — one physicality per (entity, source, kind) tuple.

### `attestations` — typed semantic relations, consensus state per source

```sql
CREATE TABLE attestations (
    id                bytea         PRIMARY KEY CHECK (octet_length(id) = 16),     -- BLAKE3-128 of (subject_id, kind_id, object_id, source_id, context_id)
    subject_id        bytea         NOT NULL REFERENCES entities(id),
    kind_id           bytea         NOT NULL REFERENCES entities(id),               -- attestation-kind entity (typed transform vocabulary)
    object_id         bytea                  REFERENCES entities(id),
    source_id         bytea         NOT NULL REFERENCES entities(id),
    context_id        bytea                  REFERENCES entities(id),
    rating            bigint        NOT NULL,                                       -- Glicko-2 mu, fixed-point ×10⁹
    rd                bigint        NOT NULL CHECK (rd > 0),
    volatility        bigint        NOT NULL CHECK (volatility > 0),
    last_observed_at  timestamptz   NOT NULL,
    observation_count bigint        NOT NULL DEFAULT 1 CHECK (observation_count >= 0),
    UNIQUE NULLS NOT DISTINCT (subject_id, kind_id, object_id, source_id, context_id)
);

CREATE INDEX attestations_subject       ON attestations USING btree (subject_id);
CREATE INDEX attestations_kind          ON attestations USING btree (kind_id);
CREATE INDEX attestations_object        ON attestations USING btree (object_id) WHERE object_id IS NOT NULL;
CREATE INDEX attestations_source        ON attestations USING btree (source_id);
CREATE INDEX attestations_context       ON attestations USING btree (context_id) WHERE context_id IS NOT NULL;
CREATE INDEX attestations_rating        ON attestations USING btree (rating DESC, rd ASC);
CREATE INDEX attestations_subject_kind  ON attestations USING btree (subject_id, kind_id);
CREATE INDEX attestations_last_observed ON attestations USING brin  (last_observed_at);

-- Ingestion idempotency:
-- INSERT INTO attestations (...) VALUES (...) ON CONFLICT (subject_id, kind_id, object_id, source_id, context_id) DO NOTHING;
```

**Column purposes:**

- `id` — **lookup** (PK). Content-addressed (BLAKE3 of the 5-tuple); stable across re-observation. Same tuple → same `id` regardless of insertion order.
- `subject_id` / `kind_id` / `object_id` / `source_id` / `context_id` — **lookup** (FKs to entities). All entity references.
- `kind_id` is the substrate's **typed knowledge vocabulary** — a small fixed enumeration per modality / per architecture family. Each kind plays a functional role in cascade composition; the substrate's actual vocabulary is usage- and structure-shaped (`CO_OCCURS_WITH`, `FOLLOWS`, `IS_HYPERNYM_OF`, `Q_PROJECTS`, etc.), not architecture-position-shaped. Kind entities are parameter-free; for transformer-family tensor-calculation kinds, per-position attribution (layer, head, tensor slot, modality vocabulary layout) is recipe content on the model recipe entity. Transformer-family roles are one architecture-family vocabulary, not the universal model ontology.
- Generic ingestion/API mental model: `OBSERVE_ATTESTATION(kind_id, subject_id, object_id, source_id, context_id, qualifiers)`. `HAS_POS` is a kind entity inside that envelope, not a bespoke function. Qualifiers are queryable entity-backed metadata; no opaque `params[]` in storage or hot-path semantics.
- `rating` / `rd` / `volatility` — **attestation knowledge** (the Glicko-2 state per source-scoped attestation row). int64 fixed-point at scale 10⁹. Glicko-2 dynamics update through arena-resolved incoming observations — per [ADR 0036](docs/adr/0036-arena-semantics-and-source-trust.md). The substrate's "weight magnitude" analog.
- `last_observed_at` / `observation_count` — **metadata** (housekeeping only; not effective-mu evidence and never a source-count truth signal).
- `ON DELETE CASCADE` on physicalities (purging a source removes its physicalities); **no** cascade on attestations (entity removal is deliberate).
- Hash-partitioning by `source_id` is optional for operational ease — not required for query performance at the scales we're targeting (10⁹–10¹⁰ rows).

### No `observations` table

Was over-engineering. Attestation rows ARE consensus state, not event log entries. Repeated assertions from the same source are idempotent. Provenance lives in the `source_id` column.

---

## I.A. Content-addressed computational model

The substrate's core compute unit is the **typed attestation graph traversed by Glicko-2-weighted A***, evaluated against the **semantic Merkle DAG of content-addressed entities**. Not a model tensor. Not a row-per-occurrence log. Not a vector index.

```text
raw input bytes
→ type-specific canonicalization (lossless)             ─┐
→ BLAKE3-128 of canonical bytes = entity id              │ identity layer
→ entity row (id + tier + type_id) — dedup on hash       ─┘

→ per-source IDecomposer breaks content into             ─┐
  lower-tier constituent entities                         │
→ mantissa-packed trajectory on a CONTENT physicality     │ representation layer
→ procrustes-aligned coord on a PROJECTION physicality    │ (per source × per kind)
→ usage-aggregated coord on a BUILDING_BLOCK physicality ─┘

→ typed attestations emitted with Glicko-2 ratings        ─┐ knowledge layer
  per arena, source, context                              ─┘
```

**Universal T0**: every modality's tier ladder bottoms at the same Unicode-codepoint atoms. This is the language-agnostic semiotic foundation for the digital Merkle DAG: ISO / language registries, WordNet, OMW, Wiktionary, UD, Tatoeba, prompts, text, books, code, image/audio data, model recipes, and model-weight representations all decompose down to codepoint entities. The codepoint `5` is one row in `entities`, shared by text (`"$255 rent"`), pixel data (RGB red-channel value 255), audio sample magnitudes, model-weight textual representations, postal codes, prices, page numbers — same hash, one row, referenced from every modality.

**Deduplication is O(tier depth + novel structure)**. Walking the Merkle DAG top-down on hash equality short-circuits at every existing-id hit. Re-ingesting identical content is O(1); ingesting content sharing constituents with prior observations is O(depth-until-novelty + novelty-size); fully novel content is O(total novel structure).

**T0 perf-cache** holds codepoint id + substrate-canonical-CONTENT-physicality coord + Hilbert index + UCA order + flags for all 1,114,112 codepoints. Clients and ingestion workers compute atom identity and coordinates locally. T1+ entity math (id, physicality coords/trajectories, Hilbert indices) is computed in the C/C++ engine before INSERT and arrives pre-baked.

**The 4D geometric layer is value-additive enrichment, not the engine**. Physicalities are Laplace's inspectable embedding-like projection/access layer: useful for fuzzy candidate discovery, source alignment, clustering, and visualization, but semantically separated from knowledge. The inference engine is graph A* through the typed attestation graph weighted by Glicko-2 effective-μ. Nearest-neighbor behavior is not spatial closeness; it is arena-conditioned attestation response: tug a query/context strand, then rank what pulls back and how hard under source trust, lineage, RD/volatility, context compatibility, conflict policy, and structural support. Geometric verticals (Hilbert range scan, physicality-coordinate lookups) can seed candidates; semantic decisions follow typed, rated attestations ordered by effective score and constrained by arena semantics.

---

## II. Type system at the SQL layer

Two extensions, installed directly by DbUp as `laplace_admin` (which is `SUPERUSER` per [ADR 0045](docs/adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md)):

```sql
CREATE EXTENSION IF NOT EXISTS postgis;            -- not trusted; SUPERUSER required (we are one)
CREATE EXTENSION IF NOT EXISTS laplace_geom;       -- requires postgis
CREATE EXTENSION IF NOT EXISTS laplace_substrate;  -- requires laplace_geom + postgis
```

`laplace_geom` provides (per ADR 0025):
- BLAKE3-128 hash convenience functions on `bytea(16)` + `laplace_btree_hash128_ops` opclass on `bytea` (ADR 0029) — no parallel PG TYPE wrapper per R22 (bytea + `CHECK (octet_length(VALUE) = 16)` is the canonical storage)
- 4D-aware geometric functions: `ST_distance_4d`, `ST_dwithin_4d`, `ST_centroid_4d`, `ST_radius_origin`, `ST_frechet_4d`, `ST_hausdorff_4d`, `ST_length_4d`
- Hilbert encoder/decoder, mantissa pack/unpack
- `laplace_gist_s3_ops` custom GIST opclass for 4D geometry (ADR 0029)

`laplace_substrate` provides:
- The three core tables (`entities`, `physicalities`, `attestations`) marked with `pg_extension_config_dump()` so substrate data survives `pg_dump`
- Composite types for attestation kinds
- `laplace_sp_trajectory_ops` SP-GiST opclass + `laplace_brin_tier_ops` BRIN opclass (ADR 0029)
- Glicko-2 aggregate
- Cascade SRFs (`laplace_astar_path`, etc.)

We do NOT create a `geometry4d` type. We use standard PostGIS `geometry` with Z+M flags = 4D.

### Source trust and arena semantics

Attestation kinds are substrate entities and may carry meta-attestations declaring their arena semantics:

- compatibility: multi-valued, functional, inverse-functional, mutually exclusive, scalar, symmetric, etc.
- context policy: context-free, context-required, temporal interval, comparison frame, source-local, prompt-local, fiction/speculation mode, etc.
- observation update scope: which tuple slots decide whether an incoming observation updates the same attestation state or a separate state
- conflict policy: which alternatives within that update scope are incompatible; absent for compatible multi-valued arenas
- source trust policy: which source classes are authoritative, admissible, discounted, or prompt-local
- effective-score inputs: rating, RD, volatility, source credibility for kind, trust class, context compatibility, and structural support

Examples:

```text
rake HAS_POS NOUN
rake HAS_POS VERB
```

Both can be true globally because POS is multi-valued at the lexical level.

```text
France HAS_CURRENT_CAPITAL Paris
France HAS_CURRENT_CAPITAL Los Angeles
```

These conflict in the same functional current-capital observation update scope under the same geopolitical/current-time context.

Source trust classes seed priors before per-kind Glicko-2 dynamics refine credibility: foundational constants, standards-derived sources, curated academic resources, academically linked user-curated resources, structured corpora/treebanks, AI-model probe observations, and prompt-local/user content. Repetition inside a correlated source family is not counted as independent consensus.

Operationally: truths cluster across independent, high-trust, structurally adjacent sources; unsupported claims scatter or cluster only inside source-scoped low-trust families. Low-trust claims remain available for analysis as claims-about-sources without winning strict traversal or synthesis scopes.

---

## II.B. Module map (Story A.13)

The substrate is implemented across three layers, each owning one kind of work (per [ADR 0027 separation-of-concerns invariants](docs/adr/0027-separation-of-concerns-invariants.md) + [RULES.md R16](RULES.md)):

| Layer | Project / library | Responsibilities | Links |
|---|---|---|---|
| **C/C++ engine** (3 shared libs per [ADR 0024](docs/adr/0024-engine-modularization.md)) | `liblaplace_core.so` | math4d kernels on raw `double[4]` (no parallel datatype per [R22](RULES.md)); `hash128_t` BLAKE3 helpers; `hilbert128_t` Skilling-2004 encode/decode; `mantissa_payload_t` pack/unpack; `glicko2_state_t` int64 fixed-point; `astar_query_t` cascade frontier (compiled traversal per [ADR 0035](docs/adr/0035-prompt-ingestion-and-compiled-cascade.md)); `codepoint_table` mmap'd T0 perf-cache; `trajectory` builders | `engine/core/` |
| | `liblaplace_dynamics.so` | Procrustes (oneMKL SVD via Eigen); Laplacian eigenmaps (Spectra); Gram-Schmidt (Eigen HouseholderQR); lottery-ticket sparsity per [R3](RULES.md). MKL+TBB integration per [ADR 0030](docs/adr/0030-mkl-eigen-spectra-tbb-integration.md); `laplace_dynamics_init` locks `MKL_THREADING_TBB` + `MKL_CBWR` for substrate determinism. | `engine/dynamics/` |
| | `liblaplace_synthesis.so` | Recipe parsing (per [ADR 0009](docs/adr/0009-recipe-extraction-and-overrides.md)); architecture-template materialization (`LlamaTemplate` etc., per [ADR 0011](docs/adr/0011-polymorphic-plugin-architecture.md)); feature extractors; native Synthesis package writers with sparse-by-construction emission per [R4](RULES.md); proof/compatibility writers such as GGUF. | `engine/synthesis/` |
| **PG extensions** (2 per [ADR 0025](docs/adr/0025-pg-extension-modularization.md)) | `laplace_geom` | General-purpose 4D PostGIS additions: `ST_*_4d` family (extends PostGIS per [R1](RULES.md)); BLAKE3-128 `hash128` helpers on `bytea(16)`; Hilbert encoder/decoder; mantissa pack/unpack; `laplace_btree_hash128_ops` + `laplace_gist_s3_ops` custom opclasses (per [ADR 0029](docs/adr/0029-custom-indexing-strategy.md)). Built via CMake (no PGXS per [ADR 0032](docs/adr/0032-unified-cmake-build-pipeline.md)). Modular SQL via `.sql.in` + cpp preprocessor per [ADR 0034](docs/adr/0034-modular-sql-via-cpp-preprocessor.md). | `extension/laplace_geom/` |
| | `laplace_substrate` | Substrate domain: the three core tables (entities / physicalities / attestations) marked with `pg_extension_config_dump()`; arena-aware Glicko-2 aggregate per [ADR 0036](docs/adr/0036-arena-semantics-and-source-trust.md); `laplace_astar_path` compiled-cascade SRF per [ADR 0035](docs/adr/0035-prompt-ingestion-and-compiled-cascade.md); `laplace_sp_trajectory_ops` + `laplace_brin_tier_ops` custom opclasses per [ADR 0029](docs/adr/0029-custom-indexing-strategy.md). Same build pattern as `laplace_geom`. | `extension/laplace_substrate/` |
| **C# app layer** (multiple projects per [ADR 0026](docs/adr/0026-csharp-project-structure.md)) | `Laplace.Engine.Core` | P/Invoke bindings for `liblaplace_core.so`. | `app/Laplace.Engine.Core/` |
| | `Laplace.Engine.Dynamics` | P/Invoke for `liblaplace_dynamics.so`; static ctor calls `laplace_dynamics_init`. | `app/Laplace.Engine.Dynamics/` |
| | `Laplace.Engine.Synthesis` | P/Invoke for `liblaplace_synthesis.so`. | `app/Laplace.Engine.Synthesis/` |
| | `Laplace.Migrations` | DbUp runner per [ADR 0021](docs/adr/0021-dbup-for-migrations.md). Orchestrates `CREATE EXTENSION laplace_geom` + `CREATE EXTENSION laplace_substrate` + role grants. | `app/Laplace.Migrations/` |
| | `Laplace.Cli` | CLI subcommands: `cascade`, `synthesize`, etc. (lands Chunks 5/7). | `app/Laplace.Cli/` (planned) |
| | `Laplace.Endpoints.*` | Protocol-endpoint plugins (OpenAI-compat, etc., per [ADR 0011](docs/adr/0011-polymorphic-plugin-architecture.md)). | `app/Laplace.Endpoints.*/` (planned) |
| | `Laplace.Sources.*` | `ISource` plugins per modality / per-corpus. | `app/Laplace.Sources.*/` (planned) |
| | `Laplace.Decomposers.*` | `IDecomposer` plugins per modality. | `app/Laplace.Decomposers.*/` (planned) |
| **Test surfaces** (per [STANDARDS.md Testing](STANDARDS.md#testing)) | `engine/*/tests/` | GoogleTest C++ unit tests; `gtest_discover_tests` registers each `TEST()` with CTest. | engine subdirs |
| | `extension/*/tests/sql/` + `expected/` | pg_regress integration tests; CTest add_test wraps `pg_regress --temp-instance` (wiring lands per-Chunk with the first real SQL function). | extension subdirs |
| | `app/Laplace.*.Tests/` | xUnit unit + integration tests. `Laplace.Migrations.Tests` uses `Testcontainers.PostgreSql` to spin up a `postgis/postgis:18` container for DbUp idempotency checks. | `app/Laplace.*.Tests/` |

### Direct dependencies (all submodules per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md))

`external/postgresql/` (PG 18), `external/postgis/` (3.6.3), `external/proj/` (9.4.1), `external/geos/` (3.12.2), `external/gdal/` (v3.9.3), `external/eigen/` (3.4.0), `external/spectra/` (v1.2.0), `external/blake3/` (1.5.4), `external/googletest/` (v1.15.2), `external/tree-sitter/` (v0.22.6). Intel oneAPI is the sole non-submodule (vendor compiler + runtime at `/opt/intel/oneapi/`).

### Build pipeline (per [ADR 0032 Path B](docs/adr/0032-unified-cmake-build-pipeline.md))

One top-level CMake tree:

```sh
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DLAPLACE_PG_PREFIX=/usr/lib/postgresql/18   # or /opt/laplace/pgsql-18 after Epic B
cmake --build build       # 3 engine .so + 2 extension .so + 2 SQLPP-built SQL scripts
cmake --install build     # installs into the PG prefix (sudo for system prefix)
ctest --test-dir build    # 22+ engine tests; pg_regress + dotnet test surfaces per-Chunk
```

`just build` / `just install` / `just test-engine` / `just test-app` wrap these for local iteration.

---

## III. Custom functions (laplace_geom + laplace_substrate extensions)

PostGIS gives us most ops free. These are the additions where PostGIS is 2D/3D-only or where we need substrate-specific math. Functions land in whichever extension owns the concept (per ADR 0025).

### 4D-aware geometric functions (laplace_geom)

```sql
ST_distance_4d(a geometry, b geometry) RETURNS double precision
ST_dwithin_4d(a geometry, b geometry, eps double precision) RETURNS boolean
ST_length_4d(line geometry) RETURNS double precision
ST_centroid_4d(g geometry) RETURNS geometry          -- true 4D centroid
ST_frechet_4d(a geometry, b geometry) RETURNS double precision
ST_hausdorff_4d(a geometry, b geometry) RETURNS double precision
ST_radius_origin(p geometry) RETURNS double precision  -- distance from origin in 4D
```

### Hilbert + hash (laplace_geom)

```sql
laplace_hilbert_encode(p geometry) RETURNS bytea
laplace_hilbert_decode(h bytea) RETURNS geometry
laplace_hash128_blake3(data bytea) RETURNS bytea               -- BLAKE3 → 16 bytes (truncated)
laplace_hash128_merkle(tier smallint, child_hashes bytea[]) RETURNS bytea
```

### Mantissa packing

```sql
laplace_mantissa_pack(entity_id bytea, ordinal integer, run_length integer, flags bigint) RETURNS geometry
laplace_mantissa_unpack(vertex geometry) RETURNS TABLE(entity_id bytea, ordinal integer, run_length integer, flags bigint)
```

Per ADR 0012: vertex is a 4D point whose XYZ encodes the full 128-bit `entity_id` and whose M encodes the per-vertex metadata. No `base` parameter — trajectory vertices are metadata containers, not spatial points being annotated. Full 212-bit utilization (128 id + 16 ordinal + 16 run_length + 52 reserved flags).

### Trajectory construction

```sql
laplace_trajectory_build(entity_ids bytea[], ordinals integer[] DEFAULT NULL,
                         run_lengths integer[] DEFAULT NULL, flags bigint[] DEFAULT NULL)
    RETURNS geometry                                            -- builds mantissa-packed LINESTRING
laplace_trajectory_constituents(traj geometry)
    RETURNS TABLE(ordinal integer, entity_id bytea,
                  run_length integer, flags bigint)             -- enumerates mantissa-packed vertices
```

`laplace_trajectory_build` defaults ordinals to 1..N, run_lengths to 1, flags to 0 when arrays are NULL. The function name retains "constituents" because that names the *role* the referenced entities play in this particular trajectory; the returned `entity_id` matches `entities.id` exactly (one hash space).

### Glicko-2

```sql
-- Custom aggregate; updates rating/rd/volatility per observation event
CREATE AGGREGATE laplace_glicko2_accumulate(
    score integer, source_credibility integer, observed_at timestamptz
) (
    SFUNC = laplace_glicko2_sfunc,
    STYPE = internal,
    FINALFUNC = laplace_glicko2_finalfunc,
    PARALLEL = SAFE
);

laplace_glicko2_decay_rd(rd bigint, volatility bigint, last_observed_at timestamptz, now timestamptz) RETURNS bigint
```

### A* cascade (set-returning)

```sql
laplace_astar_path(
    start bytea,
    goal_region bytea,
    max_depth integer DEFAULT 10,
    k_paths integer DEFAULT 1
) RETURNS TABLE(step_idx integer, entity_id bytea, g double precision, h double precision)

laplace_cascade(
    prompt bytea,
    mode text DEFAULT 'strict',
    source_scope bytea[] DEFAULT NULL,
    max_depth integer DEFAULT 12,
    k_paths integer DEFAULT 32
) RETURNS TABLE(path_idx integer, entity_id bytea, effective_mu bigint, rd bigint, source_trace bytea[])
```

`laplace_cascade` is the compiled prompt-ingestion + traversal surface. It decomposes/records prompt content according to policy, creates or references the prompt context entity, seeds the frontier, walks the attestation DAG, and streams ranked paths. It is implemented in C/C++ as an SRF; recursive SQL is not the hot path.

### Estimated count: ~15–20 custom functions total

---

## IV. Engine API (C ABI)

Lives in `engine/{core,dynamics,synthesis}/include/laplace/<mod>/` per ADR 0024. Linked by the PG extensions AND by the C# Engine.{Core,Dynamics,Synthesis} projects via P/Invoke per ADR 0026.

**Per RULES.md R22 there are no custom geometry datatypes.** The substrate's 4D coordinates ARE `POINT4D` (`{double x, y, z, m}`) from liblwgeom — readable in our tree at `external/postgis/liblwgeom/liblwgeom.h.in:412-416`. Engine math kernels operate on raw XYZM-packed `double` buffers — same memory layout as POINT4D, zero impedance at the PG-wrapper boundary. Geometry containers (`LWPOINT`, `LWLINE`, `LWPOLY`, `LWMPOINT`, `LWGEOM`) come from liblwgeom; we don't typedef parallels.

### `liblaplace_core` — substrate primitives (engine/core/)

```c
// math4d.h — kernels on raw XYZM-packed double buffers (no parallel datatype)
double math4d_dot(const double a[4], const double b[4]);
double math4d_norm(const double v[4]);
double math4d_radius_from_origin(const double v[4]);
double math4d_distance(const double a[4], const double b[4]);
double math4d_distance_sq(const double a[4], const double b[4]);
double math4d_angular_distance(const double a[4], const double b[4]);
void   math4d_add(const double a[4], const double b[4], double out[4]);
void   math4d_sub(const double a[4], const double b[4], double out[4]);
void   math4d_scale(const double a[4], double s, double out[4]);
void   math4d_centroid(const double* points, size_t n_points, double out[4]);

// hash128.h — BLAKE3 truncated to 128 bits ({hi, lo} layout justified by
// mantissa-pack read pattern; see header for read-site documentation)
typedef struct { uint64_t hi, lo; } hash128_t;

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out);
void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out);
int  hash128_compare(const hash128_t* a, const hash128_t* b);
int  hash128_equals(const hash128_t* a, const hash128_t* b);
void hash128_zero(hash128_t* out);

// hilbert4d.h — Skilling 2004 over [-1,1]^4; 16 raw bytes at the API boundary
typedef struct { uint8_t bytes[16]; } hilbert128_t;

void hilbert4d_encode(const double p[4], hilbert128_t* out);
void hilbert4d_decode(const hilbert128_t* h, double out[4]);
int  hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b);

// mantissa.h — payload riding in FP64 mantissas (ADR 0012)
// 212-bit per-vertex budget: XYZ encodes the full 128-bit entity_id, M
// encodes ordinal/run_length/flags. Biased exponent pinned to 0x3FF so every
// coord is a finite normal double. No `base` — vertices are metadata
// containers, not spatial points being annotated.
typedef struct {
    hash128_t entity_id;     // 128 bits — full BLAKE3-128, no truncation
    uint16_t  ordinal;       // position in trajectory's vertex sequence
    uint16_t  run_length;    // RLE count of consecutive identical entity refs
    uint64_t  flags;         // low 52 bits used; high 12 MUST be zero
} mantissa_payload_t;

void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);

// codepoint_table.h — T0 perf-cache (mmap'd; 1.114M × 64 B = ~67 MiB)
typedef struct {
    uint32_t      codepoint;
    uint32_t      uca_order;
    double        coord[4];     // XYZM — matches POINT4D
    hilbert128_t  hilbert;
    hash128_t     hash;
    uint32_t      flags;
    uint32_t      _pad;
} codepoint_entry_t;

int                       codepoint_table_build_from_ucd(const char* ucd_path, codepoint_entry_t* out);
int                       codepoint_table_load_perfcache(const char* path);
const codepoint_entry_t*  codepoint_table_lookup(uint32_t codepoint);

// glicko2.h — int64 fixed-point, scale 1e9 (ADR 0004)
typedef struct {
    int64_t rating, rd, volatility;
    int64_t last_observed_at_unix_ns;
    int64_t observation_count;
} glicko2_state_t;

void glicko2_init(glicko2_state_t* st, int64_t r0, int64_t rd0, int64_t vol0);
void glicko2_update(glicko2_state_t* st, int64_t score, int64_t source_credibility, int64_t now_ns);
void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns);

// astar.h — compiled cascade traversal (ADR 0035; opaque handle per R14)
typedef struct astar_query astar_query_t;
typedef struct { hash128_t entity; double g; double h; } astar_step_t;

astar_query_t* astar_open(const hash128_t* start, const hash128_t* goal_region,
                          size_t max_depth, size_t k_paths);
bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

// trajectory.h — mantissa-packed XYZM buffers (PG wrapper marshals
// LWLINE ↔ double* via liblwgeom's POINTARRAY). Vertices reference entities
// in the same hash space as `entities.hash`; "constituent" names the role
// the referenced entity plays at this vertex's position, not a separate
// entity kind. Real-impl signatures will widen to thread ordinal /
// run_length / flags through to mantissa_pack.
int trajectory_build(const hash128_t* entity_ids, size_t n, double* out_xyzm);
int trajectory_constituents(const double* trajectory_xyzm, size_t n_points,
                            hash128_t* out_hashes, size_t out_cap);

// version.h
const char* laplace_core_version(void);
```

### `liblaplace_dynamics` — alignment + sparsity (engine/dynamics/)

Links oneMKL + Spectra + oneTBB per ADR 0030. Operates on raw double buffers; opaque handles bridge Eigen template instantiations across the C ABI per R14.

```c
// init.h — process startup (locks MKL_THREADING_TBB + MKL_CBWR per ADR 0030)
int         laplace_dynamics_init(void);
const char* laplace_dynamics_version(void);

// procrustes.h — opaque handle; internals hold Eigen matrices
typedef struct procrustes_transform procrustes_transform_t;

procrustes_transform_t* procrustes_fit(const double* source_pts, size_t n,
                                       size_t source_dim,
                                       const double* target_pts);  // n*4 XYZM
void                    procrustes_apply(const procrustes_transform_t* T,
                                         const double* source_vec, size_t source_dim,
                                         double out[4]);
double                  procrustes_residual(const procrustes_transform_t* T);
void                    procrustes_free(procrustes_transform_t* T);

// eigenmaps.h — Spectra sparse-symmetric eigensolver internally
int laplacian_eigenmaps(const double* high_dim_pts, size_t n, size_t high_dim,
                        size_t k_neighbors, size_t target_dim,
                        double* low_dim_out);

// gram_schmidt.h — Eigen HouseholderQR internally
int gram_schmidt_orthonormalize(double* vectors, size_t n_vecs, size_t dim);

// sparsity.h — lottery-ticket multi-pass filter (RULES.md R3, ADR 0007)
typedef struct { double per_tensor_topk_pct; size_t per_row_topk; } sparsity_params_t;

int sparsity_per_tensor_topk(const double* weights, size_t n,
                             const sparsity_params_t* params, uint8_t* out_mask);
int sparsity_per_row_topk(const double* weights, size_t rows, size_t cols,
                          const sparsity_params_t* params, uint8_t* inout_mask);
int sparsity_probe_validate(const double* weights, size_t n,
                            const sparsity_params_t* params, uint8_t* inout_mask);
```

### `liblaplace_synthesis` — recipe + architecture templates + native packages (engine/synthesis/)

All opaque-handle plugin interfaces per ADR 0011 + R10. Real implementations land Chunks 7-8.

```c
// recipe.h — parsed Recipe entity from config.json + overrides (ADR 0009)
typedef struct recipe recipe_t;
recipe_t*   recipe_parse(const char* json_text, size_t len);
const char* recipe_get_field(const recipe_t* r, const char* field_name);
void        recipe_free(recipe_t* r);

// arch_template.h — IArchitectureTemplate plugin surface (R10)
typedef struct arch_template arch_template_t;
typedef struct {
    const char* name; size_t rank; size_t shape[8]; int dtype;
} tensor_spec_t;

arch_template_t* arch_template_load(const char* template_name);
int              arch_template_required_tensors(const arch_template_t* tmpl,
                                                const void* recipe,
                                                tensor_spec_t* out_specs, size_t cap);
void             arch_template_free(arch_template_t* t);

// feature_extractor.h — IFeatureExtractor plugin surface (R10)
typedef struct feature_extractor feature_extractor_t;
feature_extractor_t* feature_extractor_load(const char* extractor_name);
int                  feature_extractor_extract(const feature_extractor_t* fe,
                                               const void* entity_id,
                                               double* out_features, size_t out_dim);
size_t               feature_extractor_output_dim(const feature_extractor_t* fe);
void                 feature_extractor_free(feature_extractor_t* fe);

// format_writer.h — sparse-by-construction package emission per R4.
// Native text-model export is a complete safetensors-style package. GGUF is a
// proof/compatibility writer or conversion target, not the native substrate shape.
typedef struct format_writer format_writer_t;
format_writer_t* format_writer_create(const char* format_id, const char* output_path);
int              format_writer_add_metadata_str(format_writer_t* w, const char* key, const char* value);
int              format_writer_add_metadata_u32(format_writer_t* w, const char* key, uint32_t value);
int              format_writer_add_tensor(format_writer_t* w, const char* name, int dtype,
                                          const size_t* shape, size_t rank, const void* data);
int              format_writer_finalize(format_writer_t* w);
void             format_writer_free(format_writer_t* w);
```

### Geometry interop with PostGIS

Where the substrate touches PostGIS geometries (PG extension wrappers, ingestion), code uses `LWGEOM`/`LWPOINT`/`LWLINE`/`POINTARRAY`/`POINT4D` from liblwgeom directly. Marshalling at the PG boundary: `GSERIALIZED` Datums → `lwgeom_from_gserialized` → `lwgeom_as_lwline` → `POINTARRAY::serialized_pointlist` (`uint8_t*` covering packed POINT4Ds) → cast to `const double*` for engine math kernels. C# side uses `Npgsql.NetTopologySuite` (with `Ordinates.XYZM`) — `NetTopologySuite.Geometries.Point` / `LineString` cross the wire as PostGIS geometry natively, no P/Invoke required for the geometry itself.

---

## V. Indexing strategy

| Table | Index | Purpose |
|---|---|---|
| entities | `id` PK | exact lookup |
| entities | `tier` btree | tier filter |
| entities | `type_id` btree | per-modality scans |
| entities | `(tier, type_id)` btree | type-scoped tier filter |
| entities | `first_observed_by` btree partial | provenance lookup |
| physicalities | `id` PK | physicality identity |
| physicalities | `(entity_id, source_id, kind)` UNIQUE | natural-key dedup |
| physicalities | `entity_id` btree | enumerate all lenses on an entity |
| physicalities | `source_id` btree | source-scoped scans |
| physicalities | `kind` btree | filter by CONTENT / BUILDING_BLOCK / PROJECTION |
| physicalities | `coord` GiST (`gist_geometry_ops_nd`) | 4D candidate access per source/kind |
| physicalities | `hilbert_index` btree | 1D locality range scan |
| physicalities | `radius_origin` btree | abstraction-level queries |
| physicalities | `alignment_residual` btree partial | quality-filtered queries |
| physicalities | `trajectory` structural opclass | _PENDING — mantissa-pack (ADR 0012) collapses every trajectory's bounding box to `[1, 2)⁴ ∪ (-2, -1]⁴`; `gist_geometry_ops_nd` filters nothing. Replacement is a substrate-aware opclass over (entity_id range, ordinal range, run_length filter); tracked in a separate ADR alongside §VI plugins._ |
| attestations | `id` PK | row identity |
| attestations | 5-col UNIQUE NULLS NOT DISTINCT | dedup |
| attestations | `subject_id` btree | subject-scoped |
| attestations | `kind_id` btree | arena / typed-transform filter |
| attestations | `object_id` btree partial | reverse lookup |
| attestations | `source_id` btree | source-scoped |
| attestations | `context_id` btree partial | context-scoped |
| attestations | `(rating DESC, rd ASC)` btree | top-rated |
| attestations | `(subject_id, kind_id)` btree | cascade-frontier expansion pattern |
| attestations | `last_observed_at` BRIN | time-range scans |

**Multiple indexes per column** are permitted where access patterns warrant (e.g., a partial GIST per tier, BRIN alongside btree on the same Hilbert column). **Partitioning** by source / tier / time is operational ease — not required for query performance at our scale.

### Runtime execution model

Cascade traversal is invoked through one SQL-call surface and executed by the C/C++ engine inside the PostgreSQL backend.

```sql
SELECT *
FROM laplace_cascade(
        prompt => convert_to('Hello! Tell me something interesting.', 'UTF8'),
        mode => 'strict',
        source_scope => NULL,
        max_depth => 12,
        k_paths => 32
);
```

The engine loop owns:

- prompt decomposition and context entity creation/reference
- priority queue and visited-set management
- tier-up / tier-down / radial-abstraction transitions
- indexed candidate lookup by subject, kind/arena, context, source scope, and effective score
- geometry/Hilbert filtering as candidate narrowing, not semantic final judgment
- early abstention when no sufficiently supported path exists
- streaming SRF output

SPI/executor calls are allowed only as batched, prepared, indexed lookups. The hot path MUST NOT be recursive CTE traversal, cursor polling, or app-layer row-by-row SELECT loops.

The common attestation expansion pattern is:

```sql
SELECT a.object_id, a.rating, a.rd, a.volatility, a.source_id
FROM attestations a
WHERE a.subject_id = $1
    AND a.kind_id = ANY($2)
    AND a.context_id IS NOT DISTINCT FROM $3
    AND ($4 IS NULL OR a.source_id = ANY($4))
ORDER BY a.rating DESC, a.rd ASC
LIMIT $5;
```

The C/C++ engine turns that tuple stream into an arena-aware effective score and A* frontier operation.

---

## VI. Polymorphic plugin interfaces

Implementations live in `engine/src/`. See [RULES.md R10](RULES.md).

```cpp
class ISource {
public:
    virtual ~ISource() = default;
    virtual void decompose(const Bytes& payload, EntityStream& out) = 0;
    virtual void extract_attestations(AttestationStream& out) = 0;
    virtual std::optional<PhysicalityBundle> compute_physicalities() = 0;
    virtual std::string source_id() const = 0;
};

class IDecomposer {
public:
    virtual ~IDecomposer() = default;
    virtual TierTree decompose(const Bytes& content) = 0;
    virtual std::vector<EntityRef> chunk_at_tier(const Entity& parent, Tier t) = 0;
};

class IArchitectureTemplate {
public:
    virtual ~IArchitectureTemplate() = default;
    virtual TensorSpecs required_tensors(const SynthesisParams&) = 0;
    virtual TensorValues materialize_tensor(const TensorSpec&, SubstrateView&) = 0;
    virtual std::string template_id() const = 0;
};

class IFormatWriter {
public:
    virtual ~IFormatWriter() = default;
    virtual void write(const ModelData&, std::ostream&) = 0;
    virtual std::string format_id() const = 0;
};

class IFeatureExtractor {
public:
    virtual ~IFeatureExtractor() = default;
    virtual std::vector<double> extract(const Entity&, const AttestationCloud&) = 0;
    virtual size_t output_dim() const = 0;
    virtual std::string extractor_id() const = 0;
};

class IProtocolEndpoint {
public:
    virtual ~IProtocolEndpoint() = default;
    virtual Response handle(const Request&, SubstrateService&) = 0;
    virtual std::string protocol_id() const = 0;
};
```

### Layered seed source order

Early source plugins are implemented in a deliberate order so later sources land in an already constrained substrate:

| Order | Source layer | Primary fidelity gained |
|---|---|---|
| 1 | Unicode / UCD / UCA / UAX | atoms, scripts, categories, normalization, collation, segmentation |
| 2 | ISO / CLDR / Glottolog-style registries | language identity, script/region mapping, names/aliases |
| 3 | WordNet | POS, lemmas, synsets, senses, lexical relations |
| 4 | OMW | cross-lingual synset mapping; omniglottal sense bridges |
| 5 | UD Treebanks | observed syntax, morphology, dependency relations, lemmas |
| 6 | Wiktionary | definitions, forms, pronunciations, etymology, examples |
| 7 | Tatoeba | aligned multilingual sentences and audio samples |
| 8 | ConceptNet / Atomic2020 | commonsense, causal, social, and event relations |
| 9 | Tree-sitter grammars / code corpora | parseable programming-language structure |
| 10 | Text/audio/image/model sources | high-volume observations, recipes, physicalities, behavioral attestations |

Each layer adds explicit fidelity channels. AI model sources are late evidence sources measured against the substrate, not the initial source of meaning.

---

## VII. Lottery-ticket-aware sparsity (AI model ingestion only)

**Forbidden:** flat numeric thresholds.

**Required:** multi-pass relative filter.

```cpp
// Pass 1: per-tensor relative top-k%
// Pass 2: per-row top-k (for attention/MLP — preserves IO connectivity)
// Pass 3: probe-validated retention test
// Combined gate: weight is "significant" if it survives all three passes
```

**Linguistic resources:** no filter; every entry at full fidelity.

### Model-codec fidelity

For AI model sources, fidelity means `TransformerModelSource` captures the source model's load-bearing computation in substrate form:

- recipe metadata from `config.json`, tokenizer files, and auxiliary architecture files
- tokenizer/content entities and recipe attestations
- anchor physicalities from the Procrustes/Laplacian/Gram-Schmidt pipeline
- probe observations over prompts/tasks selected for the architecture
- fixed-vocabulary tensor-calculation attestation kinds for transformer-family models (`EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`); per-position attribution is recipe content on the model recipe entity, NOT parameterized kind entities or routine per-attestation metadata. Other architecture families register their own fixed mechanical-role vocabularies through their `IArchitectureTemplate`.
- lottery-ticket sparse edges that survive per-tensor, per-row, and probe validation gates

For a source-scoped round-trip, if ingestion is faithful and synthesis uses the source recipe/scope, missing source-model behavior is a codec bug or tuning failure, not an accepted architectural gap. The expected comparison is stock source model vs. native substrate traversal vs. synthesized export under fixed prompts and sampler settings.

---

## VIII. Recipe extraction (at model ingest) + custom synthesis recipes

Auto-derived from `config.json` + tokenizer.json + auxiliary files:

```
Recipe entity (T6-ish content) + typed attestations:
    HAS_HIDDEN_SIZE, HAS_NUM_LAYERS, HAS_NUM_HEADS, HAS_NUM_KV_HEADS,
    HAS_INTERMEDIATE_SIZE, HAS_VOCAB_SIZE, HAS_DTYPE,
    USES_TOKENIZER, USES_ROPE_THETA, USES_ACTIVATION,
    IS_A Architecture_<X>
```

User custom recipe JSON (Substrate Synthesis):

```json
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-1024-wiki-only",
  "overrides": { "hidden_size": 1024, "num_attention_heads": 8, "dtype": "fp16" },
  "knowledge_scope": {
    "include_sources": ["wikipedia_en", "wordnet", "qwen3-1.5b"],
    "exclude_sources": [],
    "effective_mu_policy": "arena_default"
  },
  "feature_extractors": [
    {"kind": "canonical_coord", "dims": 5},
    {"kind": "pos_one_hot", "dims": 20},
    {"kind": "wordnet_synset", "dims": 100},
    {"kind": "co_occurrence", "window": 5, "dims": 500},
    {"kind": "random_projection_pad", "dims": "remaining"}
  ],
    "output_format": "native_synthesis_package",
    "proof_exports": ["gguf"]
}
```

JSON-driven synthesis → reproducible variants. Same JSON + same substrate state → identical emission.

---

## IX. Three-phase architecture

| Phase | What runs | DB-side compute |
|---|---|---|
| **Build (one-time)** | Derive perf-cache + DB seed from Unicode UCD (independently). Bootstrap substrate-canonical source entity + Entity Type entities (`Text`, `Pixel`, `Image`, `Audio_Frame`, `Model_Recipe`, `WordNet_Synset`, ...) + per-modality kind entities. Seed T0 codepoint entities + their substrate-canonical CONTENT physicalities. | None — bulk seed insert |
| **Ingestion (per write)** | IDecomposer plugin canonicalizes content per source's entity type; C/C++ engine computes IDs + physicalities + attestations; raw INSERT or skip-on-dedup via O(tier depth + novelty) Merkle walk | **None** — Postgres just stores pre-baked rows |
| **Prompt ingestion (per request)** | Decompose prompt to substrate entities (R19); create/reference context entity; dedup against existing entity rows so substrate-supplied attestation cloud is reachable on the prompt's constituents | None for entity-math; pre-baked rows or existing IDs |
| **Query (per read)** | C/C++ extension reads perf-cache + B-tree/GIST; compiled cascade A* walks the typed attestation graph weighted by Glicko-2 effective-μ; geometric verticals (Hilbert range, physicality coord KNN) accelerate candidate narrowing | None for entity-math; only batched indexed lookups |
| **Rating accumulation** | Glicko-2 updates fire on observation events per arena/source-trust semantics (ADR 0036) | **Only** runtime DB-side compute (fixed-point arithmetic) |

---

## X. Open tuning decisions (NOT architectural — bounded engineering)

These are explicit non-decisions. They will be made at execution time with the user.

- **Glicko-2 adaptation formula** for consensus calibration (vs. competitive matches)
- **A\* heuristic h()** specific formula + admissibility analysis
- **Effective mu formula** combining rating/RD/volatility/source-kind credibility/context compatibility
- **Lottery-ticket criteria parameters** (per-tensor k%, per-row k, probe-set design)
- **Per-architecture probe protocols** (Qwen-family text transformer first codec target; mamba/diffusion/CNN/vision/audio/code model families later)
- **Feature-extractor dim assignments** for first synthesis (~1500 for Qwen3-1.5B)
- **Specific modality decomposers** for vision/audio/video (text via UAX + code via tree-sitter are settled)

---

## XI. Scope

- **~15–25K lines of code** for foundation through first chattable Qwen3 round-trip.
- **3 tables + ~15–20 custom functions + ~12–15 engine modules + ~6–8 first plugins.**
- Single-node Postgres on the 125 GiB box.
- Realistic timeline: **8–12 weeks** of focused work to chattable Qwen3-roundtrip in llama.cpp.

---

## XII. Verification (non-negotiable)

For every PR/change touching hot-path code:

- Cross-machine determinism: same input → byte-identical output
- Round-trip: serialize → deserialize → match
- FK integrity intact
- Perf-cache vs. DB seed cross-verified
- No `-ffast-math` introduced

See [.claude/agents/verification.md](.claude/agents/verification.md) for the verification agent's specific checks.
