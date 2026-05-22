# DESIGN.md — Laplace Engineering Spec

**Status:** Authoritative engineering spec. Locked architectural decisions live here. Tuning decisions explicitly flagged. Memory files at `/home/ahart/.claude/projects/-home-ahart-Projects-Laplace/memory/` carry the substrate concepts; this file is the concrete engineering surface.

Before working on any code, read [CLAUDE.md](CLAUDE.md), [GLOSSARY.md](GLOSSARY.md), [RULES.md](RULES.md), [STANDARDS.md](STANDARDS.md) — in that order.

---

## I. Schema

Three core tables. No event log. Postgres 18 + PostGIS 3.6.3.

### `entities` — every unique observed n-gram

```sql
CREATE TABLE entities (
    hash             bytea         PRIMARY KEY,
    tier             smallint      NOT NULL CHECK (tier >= 0 AND tier < 256),
    canonical_coord  geometry      NOT NULL
                                   CHECK (ST_HasZ(canonical_coord)
                                          AND ST_HasM(canonical_coord)
                                          AND ST_GeometryType(canonical_coord) = 'ST_Point'),
    hilbert_index    bytea         NOT NULL,
    trajectory       geometry      CHECK (trajectory IS NULL
                                          OR (ST_HasZ(trajectory) AND ST_HasM(trajectory)
                                              AND ST_GeometryType(trajectory) = 'ST_LineString')),
    radius_origin    double precision GENERATED ALWAYS AS (
        sqrt(ST_X(canonical_coord)^2 + ST_Y(canonical_coord)^2 +
             ST_Z(canonical_coord)^2 + ST_M(canonical_coord)^2)
    ) STORED,
    n_constituents    integer       NOT NULL DEFAULT 0 CHECK (n_constituents >= 0),
    first_observed_by bytea,                                                    -- references entities(hash); self-ref deferred
    created_at        timestamptz   NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX entities_hilbert_btree   ON entities USING btree (hilbert_index);
CREATE INDEX entities_coord_nd        ON entities USING gist  (canonical_coord gist_geometry_ops_nd);
CREATE INDEX entities_trajectory_nd   ON entities USING gist  (trajectory gist_geometry_ops_nd) WHERE trajectory IS NOT NULL;
CREATE INDEX entities_tier_btree      ON entities USING btree (tier);
CREATE INDEX entities_radius_btree    ON entities USING btree (radius_origin);
CREATE INDEX entities_first_observed  ON entities USING btree (first_observed_by) WHERE first_observed_by IS NOT NULL;

-- Optional partial indexes (added as access patterns warrant — multiple-indexes-per-column is permitted)
-- CREATE INDEX entities_coord_t0       ON entities USING gist (canonical_coord gist_geometry_ops_nd) WHERE tier = 0;
-- CREATE INDEX entities_coord_interior ON entities USING gist (canonical_coord gist_geometry_ops_nd) WHERE radius_origin < 0.3;
-- CREATE INDEX entities_hilbert_brin   ON entities USING brin  (hilbert_index) WITH (pages_per_range = 32);
```

**Notes:**

- `hash` is BLAKE3 truncated to 128 bits stored as `bytea(16)` (per ADR 0015). Content-addressable PK. No separate ID column. Raw bytes only — no hex, no casts.
- `tier` is a `smallint` (one byte sufficient, but Postgres has no native uint8).
- `canonical_coord` is a standard PostGIS `geometry` constrained to ZM-flagged Point.
- `trajectory` is NULL for T0 atoms; mantissa-packed LINESTRING for T≥1.
- `radius_origin` is a STORED generated column for cheap abstraction-level queries.
- `gist_geometry_ops_nd` is PostGIS's native N-dimensional GiST opclass — handles 4D MBRs out of the box.

### `physicalities` — per-source 4D projections

```sql
CREATE TABLE physicalities (
    entity_hash        bytea            NOT NULL REFERENCES entities(hash) ON DELETE CASCADE,
    source_hash        bytea            NOT NULL REFERENCES entities(hash) ON DELETE CASCADE,
    coord              geometry         NOT NULL
                                        CHECK (ST_HasZ(coord) AND ST_HasM(coord)
                                               AND ST_GeometryType(coord) = 'ST_Point'),
    hilbert_index      bytea            NOT NULL,
    alignment_residual double precision NOT NULL,
    source_dim         integer          NOT NULL CHECK (source_dim > 0),
    observed_at        timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_hash, source_hash)
);

CREATE INDEX physicalities_coord_nd     ON physicalities USING gist  (coord gist_geometry_ops_nd);
CREATE INDEX physicalities_hilbert      ON physicalities USING btree (hilbert_index);
CREATE INDEX physicalities_source_hash  ON physicalities USING btree (source_hash);
CREATE INDEX physicalities_residual     ON physicalities USING btree (alignment_residual);

-- Optional: partition by hash(source_hash) when ingesting many large models
-- CREATE TABLE physicalities (...) PARTITION BY HASH (source_hash);
```

### `attestations` — typed semantic relations, consensus state per source

```sql
CREATE TABLE attestations (
    id                bigserial     PRIMARY KEY,
    subject_hash      bytea         NOT NULL REFERENCES entities(hash),
    kind_hash         bytea         NOT NULL REFERENCES entities(hash),
    object_hash       bytea                  REFERENCES entities(hash),
    source_hash       bytea         NOT NULL REFERENCES entities(hash),
    context_hash      bytea                  REFERENCES entities(hash),
    rating            bigint        NOT NULL,                              -- fixed-point ×10⁹
    rd                bigint        NOT NULL CHECK (rd > 0),
    volatility        bigint        NOT NULL CHECK (volatility > 0),
    last_observed_at  timestamptz   NOT NULL,
    observation_count bigint        NOT NULL DEFAULT 1 CHECK (observation_count >= 0),
    CONSTRAINT attestations_dedup UNIQUE NULLS NOT DISTINCT
        (subject_hash, kind_hash, object_hash, source_hash, context_hash)
);

CREATE INDEX attestations_subject       ON attestations USING btree (subject_hash);
CREATE INDEX attestations_kind          ON attestations USING btree (kind_hash);
CREATE INDEX attestations_object        ON attestations USING btree (object_hash) WHERE object_hash IS NOT NULL;
CREATE INDEX attestations_source        ON attestations USING btree (source_hash);
CREATE INDEX attestations_context       ON attestations USING btree (context_hash) WHERE context_hash IS NOT NULL;
CREATE INDEX attestations_rating        ON attestations USING btree (rating DESC, rd ASC);
CREATE INDEX attestations_subject_kind  ON attestations USING btree (subject_hash, kind_hash);
CREATE INDEX attestations_last_observed ON attestations USING brin  (last_observed_at);

-- Ingestion idempotency:
-- INSERT INTO attestations (...) VALUES (...) ON CONFLICT (subject_hash, kind_hash, object_hash, source_hash, context_hash) DO NOTHING;
```

**Notes:**

- Synthetic `id` PK + `UNIQUE NULLS NOT DISTINCT` constraint handles dedup with nullable object/context cleanly.
- `rating`/`rd`/`volatility` are int64 fixed-point at scale 10⁹.
- `ON DELETE CASCADE` on physicalities (purging a source removes its physicalities); **no** cascade on attestations (entity removal is deliberate).
- Hash-partitioning by `source_hash` is optional for operational ease — not required for query performance at the scales we're targeting (10⁹–10¹⁰ rows).

### No `observations` table

Was over-engineering. Attestation rows ARE consensus state, not event log entries. Repeated assertions from the same source are idempotent. Provenance lives in the `source_hash` column.

---

## II. Type system at the SQL layer

Two extensions, both installed via `laplace_priv.install_extension` (SECURITY DEFINER wrapper) per ADR 0023 + 0025:

```sql
SELECT laplace_priv.install_extension('postgis');           -- requires SUPERUSER; wrapped
SELECT laplace_priv.install_extension('laplace_geom');      -- requires postgis
SELECT laplace_priv.install_extension('laplace_substrate'); -- requires laplace_geom + postgis
```

`laplace_geom` provides (per ADR 0025):
- `hash128` type + `laplace_btree_hash128_ops` opclass (ADR 0029)
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
laplace_mantissa_pack(base geometry, tier smallint, position integer, child_hash bytea) RETURNS geometry
laplace_mantissa_unpack(vertex geometry) RETURNS TABLE(tier smallint, position integer, hash_partial bytea)
```

### Trajectory construction

```sql
laplace_trajectory_build(constituent_hashes bytea[]) RETURNS geometry  -- builds mantissa-packed LINESTRING
laplace_trajectory_constituents(traj geometry) RETURNS TABLE(position integer, constituent_hash bytea)
```

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
) RETURNS TABLE(step_idx integer, entity_hash bytea, g double precision, h double precision)
```

### Estimated count: ~15–20 custom functions total

---

## IV. Engine API (C ABI)

Lives in `engine/include/laplace/`. Linked by the PG extension AND by the C# app via P/Invoke.

```c
// coord4d.h
typedef struct { double x, y, z, w; } coord4d_t;

double    coord4d_dot(const coord4d_t* a, const coord4d_t* b);
double    coord4d_norm(const coord4d_t* v);
double    coord4d_radius_from_origin(const coord4d_t* v);
double    coord4d_distance(const coord4d_t* a, const coord4d_t* b);
double    coord4d_distance_sq(const coord4d_t* a, const coord4d_t* b);
double    coord4d_angular_distance(const coord4d_t* a, const coord4d_t* b);
void      coord4d_centroid(const coord4d_t* points, size_t n, coord4d_t* out);
void      coord4d_add(const coord4d_t* a, const coord4d_t* b, coord4d_t* out);
void      coord4d_sub(const coord4d_t* a, const coord4d_t* b, coord4d_t* out);
void      coord4d_scale(const coord4d_t* a, double s, coord4d_t* out);

// hilbert4d.h
typedef struct { uint64_t hi, lo; } hilbert128_t;

void      hilbert4d_encode(const coord4d_t* p, hilbert128_t* out);
void      hilbert4d_decode(const hilbert128_t* h, coord4d_t* out);
int       hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b);

// hash128.h
typedef struct { uint64_t hi, lo; } hash128_t;

void      hash128_blake3(const uint8_t* data, size_t len, hash128_t* out);     // BLAKE3 → truncate to 128 bits
void      hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out);
int       hash128_compare(const hash128_t* a, const hash128_t* b);

// mantissa_pack.h
typedef struct { uint8_t tier; uint16_t position; uint64_t hash_partial; } mantissa_payload_t;

void      mantissa_pack(coord4d_t* vertex, const coord4d_t* base, const mantissa_payload_t* p);
void      mantissa_unpack(const coord4d_t* vertex, mantissa_payload_t* out);

// geometry4d.h — works with PostGIS WKB-style serialization
typedef struct geometry4d geometry4d_t;
typedef enum { G4D_POINT=1, G4D_LINESTRING=2, G4D_POLYGON=3, G4D_MULTIPOINT=4 } g4d_type_t;

geometry4d_t* geometry4d_point_create(const coord4d_t* p);
geometry4d_t* geometry4d_linestring_create(const coord4d_t* pts, size_t n);
size_t        geometry4d_serialize(const geometry4d_t* g, uint8_t* buf, size_t cap);
geometry4d_t* geometry4d_deserialize(const uint8_t* buf, size_t len);
void          geometry4d_centroid(const geometry4d_t* g, coord4d_t* out);
double        geometry4d_radius_origin(const geometry4d_t* g);
size_t        geometry4d_npoints(const geometry4d_t* g);
int           geometry4d_point_n(const geometry4d_t* g, size_t n, coord4d_t* out);
g4d_type_t    geometry4d_type(const geometry4d_t* g);
void          geometry4d_bbox(const geometry4d_t* g, coord4d_t* min_out, coord4d_t* max_out);
void          geometry4d_free(geometry4d_t* g);

double  geometry4d_distance(const geometry4d_t* a, const geometry4d_t* b);
bool    geometry4d_dwithin(const geometry4d_t* a, const geometry4d_t* b, double eps);
double  geometry4d_frechet_discrete(const geometry4d_t* a, const geometry4d_t* b);
double  geometry4d_hausdorff(const geometry4d_t* a, const geometry4d_t* b);

// codepoint_table.h — perf-cache for T0
typedef struct {
    uint32_t codepoint;
    uint32_t uca_order;
    coord4d_t coord;
    hilbert128_t hilbert;
    hash128_t hash;
    uint32_t flags;
    uint32_t _pad;
} codepoint_entry_t;  // 64 bytes; 1.114M × 64 = ~67 MiB

int       codepoint_table_build_from_ucd(const char* ucd_path, codepoint_entry_t* out);
int       codepoint_table_load_perfcache(const char* path);  // mmap
const codepoint_entry_t* codepoint_table_lookup(uint32_t codepoint);

// glicko2.h — int64 fixed-point at scale 1e9
typedef struct {
    int64_t  rating, rd, volatility;
    int64_t  last_observed_at_unix_ns;
    int64_t  observation_count;
} glicko2_state_t;

void glicko2_init(glicko2_state_t* st, int64_t r0, int64_t rd0, int64_t vol0);
void glicko2_update(glicko2_state_t* st, int64_t score, int64_t source_credibility, int64_t now_ns);
void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns);

// procrustes / laplacian / gram-schmidt — ingestion alignment
typedef struct procrustes_transform procrustes_transform_t;

procrustes_transform_t* procrustes_fit(const double* source_pts, size_t n, size_t source_dim,
                                       const coord4d_t* target_pts);
void                    procrustes_apply(const procrustes_transform_t* T,
                                         const double* source_vec, size_t source_dim,
                                         coord4d_t* out);
double                  procrustes_residual(const procrustes_transform_t* T);
void                    procrustes_free(procrustes_transform_t* T);

int laplacian_eigenmaps(const double* high_dim_pts, size_t n, size_t high_dim,
                        size_t k_neighbors, size_t target_dim,
                        double* low_dim_out);

// astar.h — cascade path search
typedef struct astar_query astar_query_t;
typedef struct { hash128_t entity; double g; double h; } astar_step_t;

astar_query_t* astar_open(const hash128_t* start, const hash128_t* goal_region,
                          size_t max_depth, size_t k_paths);
bool           astar_next(astar_query_t* q, astar_step_t* out_step);
void           astar_close(astar_query_t* q);

// trajectory.h
geometry4d_t* trajectory_build(const hash128_t* constituent_hashes, size_t n);
int           trajectory_constituents(const geometry4d_t* traj, hash128_t* out, size_t cap);
```

---

## V. Indexing strategy

| Table | Index | Purpose |
|---|---|---|
| entities | `hash` PK | exact lookup |
| entities | `hilbert_index` btree | 1D range scan |
| entities | `canonical_coord` GiST (`gist_geometry_ops_nd`) | 4D spatial KNN |
| entities | `trajectory` GiST partial | trajectory similarity |
| entities | `tier` btree | tier filter |
| entities | `radius_origin` btree | abstraction-level queries |
| physicalities | `(entity_hash, source_hash)` PK | exact lookup |
| physicalities | `coord` GiST nd | per-source spatial KNN |
| physicalities | `hilbert_index` btree | per-source 1D range |
| physicalities | `source_hash` btree | source-scoped scans |
| physicalities | `alignment_residual` btree | quality-filtered queries |
| attestations | `id` PK | row identity |
| attestations | 5-col UNIQUE NULLS NOT DISTINCT | dedup |
| attestations | `subject_hash` btree | subject-scoped |
| attestations | `kind_hash` btree | arena filter |
| attestations | `object_hash` btree partial | reverse lookup |
| attestations | `source_hash` btree | source-scoped |
| attestations | `context_hash` btree partial | context-scoped |
| attestations | `(rating DESC, rd ASC)` btree | top-rated |
| attestations | `(subject_hash, kind_hash)` btree | very common pattern |
| attestations | `last_observed_at` BRIN | time-range scans |

**Multiple indexes per column** are permitted where access patterns warrant (e.g., a partial GIST per tier, BRIN alongside btree on the same Hilbert column). **Partitioning** by source / tier / time is operational ease — not required for query performance at our scale.

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
    "rating_threshold": 0.5
  },
  "feature_extractors": [
    {"kind": "canonical_coord", "dims": 5},
    {"kind": "pos_one_hot", "dims": 20},
    {"kind": "wordnet_synset", "dims": 100},
    {"kind": "co_occurrence", "window": 5, "dims": 500},
    {"kind": "random_projection_pad", "dims": "remaining"}
  ],
  "output_format": "gguf"
}
```

JSON-driven synthesis → reproducible variants. Same JSON + same substrate state → identical emission.

---

## IX. Three-phase architecture

| Phase | What runs | DB-side compute |
|---|---|---|
| **Build (one-time)** | Derive perf-cache + DB seed from Unicode UCD (independently) | None — bulk seed insert |
| **Ingestion (per write)** | C/C++ extension precomputes entity values; raw INSERT or skip-on-dedup | **None** — Postgres just stores pre-baked rows |
| **Query (per read)** | C/C++ extension reads perf-cache + B-tree/GIST; cascades A* | None for entity-math; only index lookups |
| **Rating accumulation** | Glicko-2 updates fire on observation events | **Only** runtime DB-side compute (fixed-point arithmetic) |

---

## X. Open tuning decisions (NOT architectural — bounded engineering)

These are explicit non-decisions. They will be made at execution time with the user.

- **Glicko-2 adaptation formula** for consensus calibration (vs. competitive matches)
- **A\* heuristic h()** specific formula + admissibility analysis
- **Lottery-ticket criteria parameters** (per-tensor k%, per-row k, probe-set design)
- **Per-architecture probe protocols** (transformer first; mamba/diffusion/CNN later)
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
