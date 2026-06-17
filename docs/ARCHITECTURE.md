# Architecture

The system as built. Vocabulary per GLOSSARY.md; rationale per VISION.md; gaps per OPEN-PROBLEMS.md.

## Component map

```
engine/                      three shared C/C++ libraries (the math; DB-independent)
  core/                      liblaplace_core: identity, geometry kernels, text law
  dynamics/                  liblaplace_dynamics: LE+GSO+PA (MKL/TBB/Spectra footprint)
  synthesis/                 liblaplace_synthesis: recipe→tensors→GGUF export
extension/
  laplace_geom/              PG extension (schema public): 4D/hash/hilbert/mantissa wrappers
  laplace_substrate/         PG extension (schema laplace): tables + full SQL surface + C SRFs
app/                         C# orchestration (NO math)
  Laplace.Engine.*           P/Invoke bindings (DllImport "laplace_core" etc. — name-based, cross-platform)
  Laplace.SubstrateCRUD      the writer: COPY batches, period-fold accumulation
  Laplace.Ingestion          IngestRunner: layer gates, worker pools
  Laplace.Decomposers.*      witness adapters (Unicode, ISO, WordNet, OMW, UD, Tatoeba,
                             Atomic2020, ConceptNet, Wiktionary, FrameNet, OpenSubtitles,
                             VerbNet, PropBank, SemLink, Model, Image*, Audio*)  *scaffold
  Laplace.Cli                command surface (ingest/converse/generate/synthesize/db-roundtrip/…)
  Laplace.Migrations         DbUp Layer-1 (Linux-era orchestration)
  Laplace.Endpoints.OpenAICompat  HTTP surface + billing meters (Stripe scaffolding)
external/                    submodules: postgresql, postgis, proj, geos, gdal, eigen,
                             spectra, blake3, googletest, tree-sitter (+303 grammars)
                             — present so the dep stack can be compiled WITH the Intel
                             toolchain and Eigen/Spectra integration (roadmap)
```

Hard layering law: math lives in C/C++; the PG extension is thin wrappers + set logic; C# orchestrates and never inlines math (e.g. Glicko in C# delegates to the C primitive).

## The tables (extension `laplace_substrate`, schema `laplace`)

### entities — pure identity
```
id bytea(16) PK            BLAKE3-128 of canonical content bytes per type
tier smallint              Merkle stratum (0–255)
type_id bytea              → entities (type entity; FK added post-bootstrap)
first_observed_by bytea    → entities (source), nullable for bootstrap
created_at timestamptz
```
No geometry, no payload. Indexes: tier, type, (tier,type) btrees; first_observed partial; created_at BRIN.

### physicalities — per-source per-type 4D views (THE geometric home)
```
id bytea(16) PK
entity_id / source_id      → entities (CASCADE)
type smallint              physicality type (CONTENT=1, BUILDING_BLOCK, PROJECTION, PROJECTION_OUTPUT)
coord geometry(PointZM)    position (S³ surface / 4-ball interior)
hilbert_index bytea(16)    1-D locality key; equality = identical position (multiset signature)
trajectory geometry        mantissa-packed constituent sequence (identity, never coordinates)
radius_origin double GENERATED  ‖xyzm‖ (interior depth / surface check)
n_constituents int
alignment_residual double  Procrustes residual (LE+GSO+PA output)
source_dim int             witness native dimensionality
observed_at timestamptz
UNIQUE (entity_id, source_id, type)   ← idempotent re-observation upsert
```
Indexes: entity/source/type btrees, coord ND-GIST, hilbert btree, radius btree, residual partial, observed BRIN.
Ruling (2026-06-07, narrowed 2026-06-11): per-witness placements live HERE (source_id = the model source; the S3 morph writes PROJECTION rows — no circuit entities, no per-role types); 19_geometry_* tables retire.

### attestations — EVIDENCE = PROVENANCE (largest table; every index paid per ingested cell)
```
id bytea(16) PK            BLAKE3 of canonical (subject,type,object,source,context)
subject_id/type_id/object_id/source_id/context_id → entities (object,context nullable)
outcome smallint 0|1|2     refute | draw | confirm  (class, never magnitude)
last_observed_at timestamptz
observation_count bigint
```
Never a value channel. Indexes (minimal audit surface, measured): type, object partial, source, context partial, (subject,type,object) relation btree, last_observed BRIN. No 5-tuple UNIQUE (the PK IS the law).

### consensus — adjudicated truth
```
id bytea(16) PK            BLAKE3(subject ‖ type ‖ object|zero16)  — source+context OUT
subject_id/type_id/object_id → entities
rating/rd/volatility bigint   Glicko-2 state ×1e9 (rd>0, vol>0)
witness_count bigint
last_observed_at timestamptz
```
Indexes: object partial, relType, (subject,type); ranked-μ expression btrees `((rating-2*rd)) DESC` global-partial and `(subject_id, (rating-2*rd) DESC)` partial — these MUST match eff_mu()'s inlined expression exactly (measured: without them, top-N = 17 s seq scan over 153.7M rows; with, generate-tree laterals are one indexed scan per node).

### Readback & session
- `canonical_names(id,name)`, `codepoint_render(id,cp)` (+`build_codepoint_render()` populating all of Unicode) — render() resolution.
- `converse_turns` UNLOGGED — session cursor (durable turn attestation = open work).
- Period staging: `consensus_period_staging_<k>` UNLOGGED, created/dropped by the fold surface.

All durable tables are `pg_extension_config_dump`'d — the extension is the deployment unit; pg_dump carries substrate content.

## SQL surface (laplace_substrate modules, load order)

```
01 schema/version        02 entities          03 physicalities     04 attestations
05 indexes               10 bootstrap (self-referential meta-types, then 06–09,11+)
06 glicko2: sfunc/finalfunc C, laplace_glicko2_accumulate AGGREGATE,
            laplace_glicko2_accumulate_games (batch period entry, bit-identical to replay)
07 cascade: astar_path_raw C SRF (engine A*; SPI neighbor provider)
11 entities_exist_bitmap C (merkle-dedup filter for bulk COPY)
12 consensus schema      13 μ-law: eff_mu/eff_mu_display/refuted + glicko priors
                            (single-expression SQL, NO SET clause ⇒ planner-inlined; LAW)
14 period fold: create/drop staging, materialize_period_partition/_consensus
                (SET session_replication_role=replica, work_mem 4GB; φ-mixed invariant trap)
15 readback: canonical_id/register_canonical(s)/codepoint render/constituents/
             vertex_atom/vertex_tier/render_text C/render
16 inspect: entity_facets/entity_physicalities/attestations_in|out/
            consensus_out_readable/top_relations_readable/attestation_response(_unary)
17 consensus reads: top_relations/completions/consensus_in|out/
                    generate_tree C/generate_greedy C/consensus_stats
18 ops surface: relation_type_id/source_id/consensus_count/evidence_count/
                content_count/multi_source_entity_count/substrate_counts…
19 (RETIRED by ruling — geometry evidence/consensus tables)
20 converse: word_id/label/prompt_words/word_language/senses/define/synonyms/
             translations/hypernyms/examples/resolve_last_word/prompt_state/expansion/
             type_label/realize/realize_path/related(_in)/describe/isa_path/usage_overlap/
             reason/relatedness/epistemic_status/gaps/contrast/route_prompt/resolve_topic/
             respond/converse/structural_neighbors/structural_locale/generate
21 seed: build_codepoint_render() + the canonical vocabulary
22 cascade surface: astar_path (conceptual arenas defaulted), cascade(text,text) rendered
23 structural surface (2026-06-07): word_curve/word_shape_distance/anagrams_of/collocates
```

laplace_geom (schema public): `laplace_geom_version`, `laplace_hash128_blake3`, `laplace_hash128_merkle`
(tier-prefixed), `laplace_hilbert_encode/decode`, `laplace_mantissa_pack/unpack`, and the ST-4D family:
`laplace_distance_4d`, `laplace_angular_distance_4d` (geodesic; THE structural metric),
`laplace_dwithin_4d` (squared-dist, no sqrt), `laplace_centroid_4d` (Euclidean vertex mean),
`laplace_radius_origin`, `laplace_frechet_4d` (Eiter–Mannila), `laplace_hausdorff_4d`.
Statically links a trimmed in-tree liblwgeom (gserialized↔POINT4D only; GEOS/PROJ paths stubbed fail-loud on Windows).

## Engine kernels

core: `hash128` (BLAKE3-128), `hash_composer` (tier-prefix Merkle), `hilbert4d` (Skilling,
pure integer), `mantissa` (212-bit pack/unpack), `super_fibonacci` (T0 law),
`math4d` (norms, angular, log_s3/exp_s3, **karcher_mean** weighted iterative),
`glicko2` (int64 fixed-point; Glickman 2013; commutative within period — pinned by determinism vectors),
`astar` (frontier heap, visited/stale-skip, came-from, goal region),
`trajectory` (constituents from packed XYZM), `tier_tree`, `merkle_dedup` (LSB-first bitmaps),
`intent_stage` (PG COPY-binary builder; endian-shimmed for Windows),
UAX#29 grapheme/word/sentence break + UAX#15 NFC state machines fed by the perfcache,
`text_decomposer` (raw text → tier tree), `unicode_seed` (UCDXML SAX via libxml2; zip via popen/tar),
`codepoint_table` (perfcache mmap loader; CreateFileMapping shim on Windows).

dynamics: `laplacian_eigenmaps` (dense + sparse COO graph), `procrustes_transform/residual`,
`gram_schmidt`, `bilinear_edges` (contracted-operator circuit tiles), `init` (MKL_CBWR pinning).

synthesis: `recipe` (config parse), `arch_template` (mold), `feature_extractor`,
`qk_pairs_threshold(_pruned)`, `qk_project_cached`, `tensor_decompose` (export-only SVD),
`bf16_decoder` (AVX2), `gguf_writer`, `format_writer` (safetensors-style out).

Perfcache binary: header(magic,version,record_count=1,114,112,record_size,offsets) +
records + decomp records/data + compose records + BLAKE3-128 trailer over body. Emit is
byte-deterministic (verified target); loader validates magic/version/bounds/CRC before serving.

## Data flow

INGEST: Decomposer reads source → ContentEmitter/TextEntityBuilder build tier trees +
attestation intents → SubstrateCRUD batches: entities_exist_bitmap dedup → COPY novel
entities/physicalities (trajectories mantissa-packed via intent_stage binary) → attestation
upserts → per-relation period partials → staging partitions → materialize fold (Glicko batch
kernel) → consensus upsert. Idempotent end-to-end; concurrent with serving (MVCC).

READ: converse/respond route prompts → resolve via Merkle word_id → arena reads on
eff_mu indexes / compiled cascade (SPI) → realize to language. Structural reads: physicalities
coord/hilbert/curves via geom kernels.

EXPORT (instrument-tier, the foundry): mold (recipe file or discovered via model_recipes) →
consensus + trajectory plane reads → LE basis (GSO + Procrustes anchor) → operator grams →
SVD factors at mold ranks → gguf_writer → llama.cpp-runnable artifact; acceptance =
conventional function (forward-gguf oracle + behavioral harness), never numeric agreement
with any ingested witness.

## Determinism architecture

One compiled kernel per math truth, shared by emitter/runtime/extension (no recompiled
variants — caught producing 1-ULP drift); no fast-math anywhere; engine flags pinned
(Linux: -fno-fast-math -ffp-contract=off -march=…; Windows: /fp:precise /arch:AVX2 — cross-FP-regime
byte-identity empirically proven by 8/8 regress vs Linux-generated expected files);
Glicko int64 fixed-point; Hilbert/BLAKE3 integer-pure; perfcache emit byte-compared in CI;
pg_regress .out files are byte-contracts.

## Platform notes (Windows-canonical as of 2026-06-07)

PG win32 dlopen is plain LoadLibrary (no dependent-DLL search next to modules) ⇒ extensions
STATIC-link the engine (laplace_core_static, laplace_dynamics_static + sequential static MKL);
self-contained DLLs deploy to D:\Data\Postgres\laplace and are wired by PG-18
extension_control_path/dynamic_library_path (SEMICOLON list separator; BARE module names so
dynamic_library_path applies; ALTER SYSTEM + reload — zero Windows admin). Full operational
law in OPERATIONS-WINDOWS.md. Agent-facing build/deploy rules:
`.github/instructions/build-environment.instructions.md`.

## Forbidden patterns (anti-drift)

These reinventions have caused drift; do not reintroduce them. Enforcement: `.github/instructions/layering-law.instructions.md`, `build-environment.instructions.md`, `ingest-witness.instructions.md`, `type-id-law.instructions.md`, and CI source scans.

| Pattern | Why forbidden | Correct alternative |
|---------|---------------|---------------------|
| Inline `WITH RECURSIVE` in `Laplace.Cli/Program.cs` | Duplicates `laplace.generate` with divergent behavior | `SELECT * FROM laplace.generate(...)` |
| `Hash128.OfCanonical("substrate/type/...")` in decomposers | Bypasses rank/symmetry; three minting paths | `EntityTypeRegistry.Id` / `RelationTypeRegistry.RelationTypeId` |
| Glicko2 calibration via SQL `laplace_glicko2_accumulate_games` from C# | Round-trip per grid point | `Glicko2.UpdatePeriod` (native) in `CalibratedInverse` |
| `ContentEmitter.Emit` in corpus inner loops | Native tree → C# rows → native `IntentStage` churn | `ContentWitnessBatch` or fast-ingest witness + memo |
| Per-line `new byte[]` in `StreamingUtf8LineReader` | O(lines) allocations on GB corpora | Zero-copy slice or `ArrayPool` rented buffer |
| `Parallel.For` float→double in `ModelTableETL` | C# tensor math | Native `f32_gather_to_f64` / `laplace_bf16_decode` |
| Direct `SELECT ... FROM laplace.consensus` in CLI | Bypasses versioned read API | `laplace.consensus_export` or COPY |
| C# `Vector128` / `Simd` | SIMD belongs in engine | `engine/` AVX2/MKL kernels |
| `*FastIngest` corner-cuts (skip grammar compose) | Bypass constituent deposition; ghost content ids | `StructuredGrammarIngest` + `IGrammarWitness` + `TrySpanEntity` |
