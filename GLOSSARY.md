# GLOSSARY.md — Laplace Terminology Lock

Every term used in this codebase. If a term you intend to use isn't here, **add it** before using it. If a term IS here, it means exactly what's defined; do not use it differently.

---

## Foundational concepts

### Substrate

The Laplace database itself. Holds entities, physicalities, and attestations. Acts as both storage and inference engine. Replaces what conventional AI calls "model + runtime + database" with a single coherent layer.

### Entity

A unique observed n-gram of digital content, identified by its content hash (XXH3-128). Stored as one row in the `entities` table. Plays **two roles simultaneously**: content (the thing observed) AND building block (referenced by higher-tier entities via mantissa-packed trajectories). Same content → same hash → ONE row.

### Atom

A T0 (tier 0) entity. Always a Unicode codepoint. Universal across all modalities and languages. Fixed set of 1,114,112 possible atoms (the entire Unicode codepoint space). Distributed quasi-uniformly on S³ via super-Fibonacci + Hopf + UCA.

### Trajectory

A linestring (sequence of 4D points) representing a T≥1 entity's path through its constituents. Stored in the `trajectory` column of `entities`. Each vertex carries the constituent's identity via **mantissa packing** (low bits of the float64 coords).

### Tier

Stratum in the n-gram hierarchy. T0 = atoms (codepoints), T1 = graphemes (UAX#29), T2 = word-forms, T3 = sentences, T4 = paragraphs, T5 = sections, T6 = documents, T7+ = corpora / collections. Each tier T_n consists of n-grams of T_(n-1) entities.

### Attestation

A typed semantic relation between entities, sourced and rated. Stored as one row in the `attestations` table per `(subject, kind, object, source, context)` tuple. **An attestation IS consensus state, NOT an event log entry** — repeated assertions by the same source do not create new rows.

### Physicality

A per-source 4D projection of an entity. Each ingested model gives each entity a Physicality (its position in the model's representational space, projected to 4D substrate space via Procrustes alignment). Stored in `physicalities` table, keyed `(entity_hash, source_hash)`. Same entity hash across all sources; many Physicalities per entity.

### Source

An entity that emits attestations. Linguistic resources (WordNet, UD, Wiktionary, ConceptNet, Atomic2020, Tatoeba), AI models, knowledge graphs, and text corpora are all sources. Sources are themselves substrate entities (content-recorded); their credibility-per-kind is tracked via meta-attestations.

### Context

An entity representing the context in which an attestation holds. For context-bound attestations (e.g., a POS reading valid only in a specific sentence), the `context_hash` column references a context entity. For context-free attestations (e.g., a universal IS_A), `context_hash` is NULL.

---

## Geometric concepts

### Glome (S³)

The 3-sphere — surface of the 4-ball. The substrate's atomic layer (Unicode codepoints) lives on S³ via super-Fibonacci spirals + Hopf fibration. Embedded in R⁴ as the unit hypersphere.

### 4-Ball

The interior of S³. Holds centroids of T≥1 entities. **Radial distance from origin = abstraction level** — concrete primitives at the surface (r=1); abstract concepts at the center (r→0).

### Hyperbox `[-1, 1]⁴`

Bounding box of the 4-ball. The Hilbert curve fills this hyperbox (not the sphere), letting one curve index both surface entities and interior centroids with consistent 1D locality.

### Canonical coordinate

An entity's authoritative 4D position. For T0 atoms: derived from Unicode UCD (super-Fibonacci + Hopf + UCA). For T≥1 entities: centroid of constituents.

### Hilbert index

128-bit (4D × 32-bit-per-dim) space-filling curve position of an entity's canonical coordinate within the bounding hyperbox. Used for 1D locality-preserving B-tree range scans.

### Radial abstraction

The principle that distance from origin in the 4-ball encodes abstraction level. Generalization = move radially inward; specialization = move radially outward; same-abstraction-level traversal = move along an iso-radial sphere.

### Mantissa packing

Encoding of constituent identity in the low mantissa bits of a 4D vertex's float64 coordinates. Each vertex carries: 8 bits tier + 12 bits position-in-trajectory + 60 bits truncated constituent hash (across the 4 coord components' low 20 mantissa bits each). High mantissa bits preserve approximate spatial position for indexing.

### Trajectory vs. linestring

LINESTRING is the geometry subtype (a PostGIS concept). Trajectory is the substrate concept (an entity's path through its constituents). The `trajectory` column stores a LINESTRING-typed geometry.

---

## Algorithmic concepts

### Super-Fibonacci

Marc Alexa's 2021 algorithm for quasi-uniform distribution of points on SO(3) ≈ S³. Used to place 1,114,112 Unicode codepoints on S³ surface from their UCA-sorted indices. Deterministic, single-pass, distribution near-optimal.

### Hopf fibration

Decomposition of S³ as S¹ fiber over S² base. Used (alongside super-Fibonacci) to give codepoint positions structured coordinates aligned with UCA ordering.

### UCA (Unicode Collation Algorithm)

ICU-provided deterministic sort order for Unicode codepoints. Used to assign each codepoint its position in the super-Fibonacci sequence.

### Procrustes alignment

SVD-based optimal rigid transform mapping one point set onto another. Used to align an ingested model's N-dim embedding space to the substrate's 4D canonical positions via shared Unicode-anchored entities.

### Laplacian eigenmaps

Nonlinear dimensionality reduction preserving local graph structure. Used in the alignment pipeline before Procrustes to reduce a model's N-dim embedding space to a manageable intermediate dimension.

### Gram-Schmidt orthonormalization

QR decomposition producing an orthonormal basis. Used in the alignment pipeline between Laplacian eigenmaps and Procrustes.

### Glicko-2

A rating system extending Glicko (Mark Glickman). Provides rating + rating-deviation + volatility per rated item, with time decay built in. Used in Laplace for **source credibility per attestation kind**, tracked via meta-attestations on source entities. Dynamics update on cross-source agreement/disagreement evidence.

### Arena

A semantically-coherent subset of attestations whose ratings compose. Defined by the attestation-kind hierarchy: e.g., all POS attestations form one arena; all attention-edge attestations from one transformer architecture form another. Ratings within an arena are commensurable; ratings across arenas are not.

### Lottery-Ticket-Aware Sparsity

Multi-pass filter for AI model ingestion. **NEVER a flat numeric threshold.** Combines: (a) per-tensor top-k% by importance; (b) per-row top-k for attention / MLP structure preservation; (c) probe-validated retention test. Applies to weight-based sources only. Linguistic resources are ingested at full fidelity.

### Noise floor

The lottery-ticket-aware filter applied during AI model ingestion that discards gradient jitter, init noise, training artifacts. Zero-and-near-zero weights are trash, not data. NOT a flat threshold.

### Cascade (cascading-tier NN)

The substrate's inference algorithm. A query enters at some tier, decomposes to constituents, multi-vertical NN at that tier, Glicko-2 filters candidates, aggregates to higher-tier candidates, re-evaluates, cascades upward (compositional) and inward (radial abstraction) until reaching the answer region. Each cascade step is O(tier) ≈ O(constant).

### Multi-vertical NN

Querying the substrate via three (or more) orthogonal similarity dimensions simultaneously: geometric (S³ position), content (n-gram / trajectory structural), attestation (graph-walk semantic). Each operable at every tier. Composable.

### A* (in Laplace context)

Best-first graph search through the attestation DAG. Edge cost = function of Glicko-2 rating + RD; heuristic h() = lower-bound estimate of remaining cost to goal region. Streamed via set-returning C function for incremental token generation.

---

## Codec concepts

### AI⇄DB codec

The reframing of AI operations as database operations: ingest = INSERT; train = `WHERE` clause; distill = `SELECT INTO model_file`; prune = `DELETE`; unlearn = `DELETE WHERE source = X`; combine = ingest both into same substrate.

### Vampire mode

The substrate's posture toward AI model weights: drain knowledge into attestations; discard the weight bytes; synthesize fresh packaging on demand. Models are food, not artifacts. The substrate **never preserves model weights bit-perfectly**.

### Content recording

Storage mode for normal digital content (documents, code, datasets, model-file metadata): mantissa-packed linestrings preserving bit-perfect content via recursive constituent references.

### Attestation graph

Storage mode for AI model knowledge: sparse typed edges between entities with Glicko-2 ratings. No weight bytes preserved.

### Recipe

The architectural template of a model. Auto-extracted at ingest from `config.json` + `tokenizer.json` + auxiliary files; stored as a Recipe entity with typed attestations (`HAS_HIDDEN_SIZE`, `HAS_NUM_LAYERS`, etc.). Used as the default template for round-trip emission. User can override with custom JSON.

### Substrate Synthesis

Fully parametric model emission from substrate state. User specifies (via JSON recipe) target architecture, dim, layer count, heads, expert count, vocab, dtype, knowledge scope, etc. Output: a model file of any shape, deterministically materialized from substrate attestations.

### Sparse-by-construction emission

Property of Substrate Synthesis output: positions with no significant attestation emit zero. Emitted models are automatically pruned, ensembled, and consensus-cleaned without any explicit pruning / ensembling step.

---

## Operational concepts

### Engine

The shared C/C++ library (`liblaplace_engine.so` / `.dll`). Linked by the PG extension AND by the C# app layer. Single source of math truth.

### PG extension

The PostgreSQL extension (`laplace`). Thin wrappers around engine functions, exposed via `PG_FUNCTION_INFO_V1`. Adds custom 4D-aware functions where standard PostGIS is 2D/3D-only.

### Perf-cache

Memory-mapped binary file containing precomputed T0 codepoint data: hashes, 4D coordinates, Hilbert indices, UCA orders, flags. Built once at deploy time from Unicode UCD. ~67 MiB; fits in CPU L2/L3 cache.

### Build pipeline (Unicode UCD → artifacts)

The deterministic derivation that produces TWO sibling artifacts from Unicode UCD: (1) the perf-cache binary, (2) the DB seed of T0 rows. Neither feeds the other; both trace to Unicode itself as the canonical source.

### Three-phase architecture

The substrate's lifecycle phases: **Build** (one-time, derive perf-cache + DB seed from UCD); **Ingestion** (per write, C/C++ precomputes T≥1 entity values, raw INSERT); **Query** (per read, C/C++ reads perf-cache + B-tree/GIST, cascades A*); **Rating accumulation** (Glicko-2 updates as observation events arrive — the only runtime DB-side compute).

### Polymorphic plugin architecture

The discipline that adding new capability touches ONE plugin, never all layers. Five plugin interfaces: `ISource`, `IDecomposer`, `IArchitectureTemplate`, `IFormatWriter`, `IFeatureExtractor`, `IProtocolEndpoint`.

### Probe (in ingestion context)

Running an AI model on input data to observe its outputs/attention/activations, then extracting attestations from significant observations. Required for model ingestion (vs. parse-based ingestion of pre-structured sources). May need GPU at probe time for large models; results stored CPU-native afterward.

### Round-trip

The proof-of-concept workflow: ingest model M → emit M' using M's own Recipe as template, weights from substrate → load M' in llama.cpp → chat with it. M' is architecturally identical to M but weights are substrate-consensus, not M-original.

### Endpoint extension

A plugin in the C# app layer that exposes a protocol (e.g., OpenAI-compat) over the substrate. Translates protocol requests into substrate queries; translates substrate responses into protocol responses. Dissolves the need for conventional inference runtimes (llama.cpp / vLLM / etc.) — the substrate IS the serving layer.

---

## Anti-vocabulary (do NOT use these except to explicitly reject them)

- ~~"Vector database"~~ — Laplace's NN is multi-vertical + cascading-tier; not a single-vector cosine-similarity lookup
- ~~"HNSW" / "FAISS" / "ScaNN"~~ — approximate-NN libraries; banned (we use exact deterministic indices)
- ~~"RAG"~~ — Laplace's prompt-is-ingestion + cascade-traversal subsumes RAG without the retrieval-augmentation duct tape
- ~~"Fine-tuning"~~ — Laplace's WHERE-clause specialization replaces fine-tuning
- ~~"Distillation"~~ — Laplace's Synthesis replaces conventional distillation
- ~~"Context window"~~ — Laplace has none; prompt is ingestion
- ~~"Embedding model"~~ — Laplace's per-entity attestation profile + multi-vertical NN replaces "an embedding"
- ~~"Inference server"~~ — Laplace's endpoint extensions over the substrate replace inference servers
- ~~"Training loop"~~ — Laplace has no gradient descent; ingestion accumulates observations
- ~~"Catastrophic forgetting"~~ — doesn't happen at substrate level (each emission is a snapshot synthesis)
- ~~"Hallucination"~~ as opaque — Laplace's hallucinations are addressable (low-rated edge / interior interpolation with no anchor support)
- ~~"Threshold"~~ as flat number — lottery-ticket-aware sparsity is multi-pass, not a single cutoff

If a contributor (human or agent) uses one of these terms without explicit "we reject this convention" framing, that's a red flag for pattern-matching to conventional AI. Re-read [RULES.md](RULES.md).
