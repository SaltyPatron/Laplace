# GLOSSARY.md — Laplace Terminology Lock

Every term used in this codebase. If a term you intend to use isn't here, **add it** before using it. If a term IS here, it means exactly what's defined; do not use it differently.

---

## Foundational concepts

### Substrate

The Laplace database itself. Holds entities, physicalities, and attestations. Acts as both storage and inference engine. Replaces what conventional AI calls "model + runtime + database" with a single coherent layer.

### Entity

A unique observed n-gram of digital content, identified by its content hash (BLAKE3 truncated to 128 bits — per ADR 0015). Stored as one row in the `entities` table (owned by the `laplace_substrate` extension per ADR 0025). Plays **two roles simultaneously**: content (the thing observed) AND building block (referenced by higher-tier entities via mantissa-packed trajectories). Same content → same hash → ONE row.

### Tiered Merkle DAG

The content-addressed structure formed by hashing each entity from its tier and constituent child hashes. T0 atoms are Unicode codepoints; T≥1 entities are n-grams of lower-tier entities. Deduplication, identity checks, and reconstruction walk the DAG from trunk to leaf and cost O(tier depth + novel structure), not O(total corpus size).

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

### Source Trust Class

The prior trust band assigned to a source before per-kind credibility updates: foundational constants, standards-derived sources (Unicode/UCD/UCA/UAX), curated academic resources (WordNet, UD), academically linked user-curated resources (OMW, Wiktionary), structured corpora, AI-model probe observations, and prompt-local/user content. Trust class is not truth by fiat; it weights Glicko-2 agreement/disagreement inside an arena.

### Context

An entity representing the context in which an attestation holds. For context-bound attestations (e.g., a POS reading valid only in a specific sentence), the `context_hash` column references a context entity. For context-free attestations (e.g., a universal IS_A), `context_hash` is NULL.

### Prompt Ingestion

The rule that prompts are decomposed into substrate entities and represented by a context entity/trajectory before inference. A prompt is not an ephemeral token buffer with a context-window limit; it is substrate content, either ephemeral or durable by policy, and cascade traversal starts from that content graph.

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

### Arena Semantics

The metadata attached to an arena or attestation kind that tells Glicko-2 how to interpret agreement and disagreement: multi-valued compatibility, functional cardinality, inverse-functional cardinality, mutually exclusive object sets, scalar axes, temporal/context requirements, source-scope rules, and competition sets. `rake HAS_POS NOUN` and `rake HAS_POS VERB` can coexist; `France HAS_CURRENT_CAPITAL Paris` and `France HAS_CURRENT_CAPITAL Los Angeles` compete in the same current-capital arena/context.

### Effective Mu

The traversal/synthesis score derived from Glicko-2 rating (`mu`) adjusted by rating deviation, volatility, source credibility for the attestation kind, context compatibility, source trust class, and structural support. Hot-path selection orders by effective mu, not raw source count.

### Lottery-Ticket-Aware Sparsity

Multi-pass filter for AI model ingestion. **NEVER a flat numeric threshold.** Combines: (a) per-tensor top-k% by importance; (b) per-row top-k for attention / MLP structure preservation; (c) probe-validated retention test. Applies to weight-based sources only. Linguistic resources are ingested at full fidelity.

### Noise floor

The lottery-ticket-aware filter applied during AI model ingestion that discards gradient jitter, init noise, training artifacts. Zero-and-near-zero weights are trash, not data. NOT a flat threshold.

### Cascade (cascading-tier NN)

The substrate's inference algorithm. A query enters at some tier, decomposes to constituents, multi-vertical NN at that tier, Glicko-2 filters candidates, aggregates to higher-tier candidates, re-evaluates, cascades upward (compositional) and inward (radial abstraction) until reaching the answer region. Each cascade step is O(tier) ≈ O(constant).

### Compiled Cascade

The implementation rule for cascade traversal: one SQL-call surface enters a C/C++ set-returning function that owns frontier management, priority queues, visited sets, tier transitions, context checks, and ranking. SPI/executor access may perform batched indexed lookups; recursive CTEs, cursors, and app-layer row-by-row loops are not the hot path.

### Multi-vertical NN

Querying the substrate via three (or more) orthogonal similarity dimensions simultaneously: geometric (S³ position), content (n-gram / trajectory structural), attestation (graph-walk semantic). Each operable at every tier. Composable.

### A* (in Laplace context)

Best-first graph search through the attestation DAG. Edge cost = function of Glicko-2 rating + RD; heuristic h() = lower-bound estimate of remaining cost to goal region. Streamed via set-returning C function for incremental token generation.

### Honest Abstention

The substrate-native refusal mode caused by missing or weak path support: no viable path, low effective mu, high RD, high volatility, unresolved arena conflict, or context mismatch. Abstention is structural, not a generated phrase pattern.

### Traversal Mode

The policy that controls how cascade walks the substrate: strict mode requires high effective mu and trusted source scopes; speculative mode surfaces uncertain paths with uncertainty intact; creative/fiction modes deliberately allow lower-rated, analogical, or context-marked walks. Hallucination is therefore an explicit traversal choice, not an opaque failure mode.

### Truths Cluster / Lies Scatter

The source-rating principle that true claims tend to gather support across independent, high-trust, structurally adjacent sources, while unsupported claims scatter or cluster only inside correlated low-trust source families. Low-trust clusters can be stored as claims-about-sources without winning strict truth-seeking arenas.

---

## Codec concepts

### AI⇄DB codec

The reframing of AI operations as database operations: ingest = INSERT; train = `WHERE` clause; distill = `SELECT INTO model_file`; prune = `DELETE`; unlearn = `DELETE WHERE source = X`; combine = ingest both into same substrate.

### Model-Codec Fidelity

For source-scoped model round-trip, the property that `TransformerModelSource` captures the source model's load-bearing computation as recipe metadata, tokenizer content, physicalities, probe observations, architecture-specific attestations, and lottery-ticket sparse edges. If ingestion and synthesis are faithful under the source's own recipe/scope, the emitted model should land in the source model's behavioral basin.

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

### Zero Calories

The performance consequence of sparse-by-construction emission: exact zero tensor positions carry no substrate-supported evidence and may be skipped, compressed, or omitted by sparse-aware runtimes. Tiny nonzero gradient jitter still costs memory traffic and multiply/add work; exact zero changes the computational contract.

---

## Operational concepts

### Engine

Three shared C/C++ libraries (per ADR 0024):
- `liblaplace_core.so` — coord4d, hash128 (BLAKE3), hilbert4d, mantissa, geom4d serde, Glicko-2 fixed-point, A* primitives
- `liblaplace_dynamics.so` — Procrustes, eigenmaps, Gram-Schmidt, lottery-ticket sparsity (links oneMKL + Spectra + TBB)
- `liblaplace_synthesis.so` — recipe extraction, architecture templates, feature extractors, GGUF writer

The same `.so` files are loaded by the PG extensions AND by the C# app layer via P/Invoke. Single source of math truth.

### PG extension

Two PostgreSQL extensions (per ADR 0025):
- **`laplace_geom`** — general-purpose 4D additions to PostGIS: `ST_*_4d` functions, `hash128` type, Hilbert encoder, mantissa pack/unpack, custom GIST opclasses for S³-aware indexes
- **`laplace_substrate`** — substrate schema: three core tables, attestation kind hierarchy, Glicko-2 aggregate, cascade SRFs, custom SP-GiST + BRIN opclasses

Thin wrappers around engine functions, exposed via `PG_FUNCTION_INFO_V1`. Add custom 4D-aware functions where standard PostGIS is 2D/3D-only.

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
