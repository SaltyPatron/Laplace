# GLOSSARY.md — Laplace Terminology Lock

Every term used in this codebase. If a term you intend to use isn't here, **add it** before using it. If a term IS here, it means exactly what's defined; do not use it differently.

---

## Foundational concepts

### Substrate

The Laplace database itself. Holds entities, physicalities, and attestations. Acts as both storage and inference engine. Replaces what conventional AI calls "model + runtime + database" with a single coherent layer.

### Entity

A unique observed n-gram of digital content. **Identity is its content hash** — `id bytea(16)` PK in `entities`, value being BLAKE3 of the canonical (lossless, type-canonicalized) content bytes (per ADR 0015). The column is named `id` because that is its role; the value happens to be a hash. Plays **two roles simultaneously**: content (the thing observed) AND building block (referenced by higher-tier entities via mantissa-packed trajectory vertices in some physicality). Same content → same hash → ONE row, forever, regardless of how many times or by how many sources it is re-observed.

Entities carry **identity + tier + type** (see [Entity Type](#entity-type)) and lightweight provenance metadata. They do NOT carry geometry, trajectory, or any per-source representation — those live in [Physicality](#physicality) rows.

### Entity Type

A column on `entities` (`type_id` FK referencing another entity that names the type) declaring what *kind* of thing this entity is. Types include: `Text`, `Pixel`, `Patch`, `Region`, `Image`, `Audio_Sample`, `Audio_Frame`, `Audio_Track`, `Model_Recipe`, `Model_Tokenizer`, `Model_Architecture`, `WordNet_Synset`, `UD_Sentence`, `UD_Token`, `Wiktionary_Entry`, `Tatoeba_Sentence`, `Atomic2020_Event`, `ConceptNet_Concept`, etc. Types are themselves entities (content-addressed, bootstrapped at install). The type determines:

- Canonicalization rule (how raw input becomes canonical bytes before hashing)
- Decomposition rule (which IDecomposer plugin breaks this entity into lower-tier constituents)
- Reconstruction rule (how to emit bytes from a trajectory walk)
- Modality-applicable attestation kinds (which typed transforms apply)

### Canonicalization

The type-specific, **lossless** normalization applied to raw input bytes before hashing. Different encodings that decode to the same canonical content yield the same entity ID — UTF-8 vs UTF-16 of identical codepoint sequences; PNG vs lossless WebP of identical pixel grids; FLAC vs WAV of identical PCM samples. **Lossy conversions are NOT equivalent under canonicalization** — a JPEG is a different entity from the PNG it was derived from (different pixel values after decode); an MP3 is a different entity from a FLAC of "the same audio" (different PCM); a quantized GGUF is a different entity from the safetensors it was derived from (different tensor values). Cross-format equivalence between lossy variants is an **attestation** (e.g., `IS_LOSSY_ENCODING_OF`), not an identity collapse.

### Universal T0

Tier 0 atoms are **Unicode codepoints**, universally, across every modality. Pixel values, audio samples, model weights, syntactic dependency labels, knowledge-graph relations — every tier-ladder bottoms at the same 1,114,112-element codepoint alphabet. No modality has its own private T0; pre-seeded once from UCD, used by everyone. Cross-modal reachability is structurally guaranteed because every entity at every tier eventually decomposes to entities in the same T0 hash space.

### Tiered Merkle DAG (semantic, not bit-wise)

The content-addressed structure formed by recursively decomposing every entity into lower-tier constituent entities and content-addressing each. T0 = Universal T0 codepoints; T≥1 = type-specific n-grams of lower-tier entities. The Merkle property holds **semantically**: a parent entity's hash is the BLAKE3 of its canonical content (per its type's canonicalization rule), and that content is reproducible by walking the parent's CONTENT-kind physicality trajectory. Different encodings of the same canonical content produce the same hash; lossy transformations produce different entities.

Deduplication checks are O(tier depth + novel structure). Same hash at the trunk means same content beneath, so the recursion short-circuits whenever a hash lookup matches an existing row. Re-ingesting identical content is O(1); ingesting content sharing constituents with prior observations is O(depth-until-novelty + novelty-size).

### Trajectory

A 4D `ST_LineString` whose ordered, mantissa-packed vertices each reference one constituent entity in sequence. Stored in the **`physicalities.trajectory`** column (NOT on `entities`) — specifically on physicalities of kind CONTENT (and may appear on other kinds that record a structural decomposition from their source's perspective). Each vertex carries the constituent entity's ID + per-vertex metadata (ordinal, run_length, flags) via [Mantissa packing](#mantissa-packing).

Walking the trajectory in order and dereferencing each vertex's `entity_id` recursively reconstructs the parent entity's content. The trajectory IS the parent's content recording per the relevant source's decomposition rule.

### Tier

Stratum in the n-gram hierarchy of a given modality. T0 = Universal T0 codepoints. T≥1 = type-specific n-grams of T_(n-1) entities. Text: T1 = graphemes (UAX#29), T2 = word-forms, T3 = sentences, T4 = paragraphs, T5 = sections, T6 = documents, T7+ = corpora. Visual: T_(pixel) → T_(patch) → T_(region) → T_(image) → T_(collection). Each modality's tier ladder is type-specific; all ladders meet at T0.

### Attestation

A typed semantic relation between entities, sourced and rated. Stored as one row in the `attestations` table per `(subject_id, kind_id, object_id, source_id, context_id)` tuple. **An attestation IS consensus state, NOT an event log entry** — repeated assertions by the same source do not create new rows; Glicko-2 dynamics update on cross-source agreement/disagreement evidence instead.

Attestations are the substrate's **typed computational vocabulary**. They are not metadata about entities; they are the substrate's analog of an AI model's weighted typed transforms. See [Attestation Kind](#attestation-kind) and [Cascade](#cascade-cascading-tier-nn).

### Attestation Kind

The `kind_id` of an attestation — itself an entity — naming a **specific typed transform**. Each kind is drawn from a **small fixed vocabulary** per modality / per architecture family. Kinds are NEVER synthesized via hash-concatenation of arbitrary metadata (no `BLAKE3(layer_n || head_m || ...)` to manufacture per-position kinds — entity identity is BLAKE3 of canonical *content*, never of metadata-tuples).

- **Modality-agnostic kinds**: `IS_A`, `HAS_PART`, `CO_OCCURS_WITH`, `FOLLOWS`, `PRECEDES`, `OCCURS_IN_CONTEXT`.
- **Text kinds**: `HAS_POS`, `HAS_LEMMA`, `IS_HYPERNYM_OF`, `IS_LEMMA_OF`, `IS_TRANSLATION_OF`, etc. — drawn from linguistic-resource vocabularies.
- **Visual kinds**: `EXTRACTS_R_CHANNEL`, `ADJACENT_TO_PIXEL`, `INDICATES_HUE`, etc.
- **Audio kinds**: `IS_AT_SAMPLE`, `HAS_FREQUENCY_PEAK`, etc.
- **Tensor-calculation kinds for transformer-family AI models** (a fixed list of ~10): `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`. Each tensor in a transformer-family model is one of these calculation types; ingestion emits attestations of the corresponding kind between substrate token entities (which are text entities), aggregated across all (layer, head) positions where the source model used that relationship. Per-position attribution is NOT carried on attestations — the **recipe** (text/JSON content on the model entity) is the structural source of truth for layer / head / dimension / per-tensor token vocabulary; the architecture template (substrate code, per `IArchitectureTemplate`) distributes substrate's aggregated typed attestations across tensor slots at emit time per the recipe's layout. Storing per-position attribution would be redundant with the recipe.
- **Cross-modal kinds**: `DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`.

Kinds carry [Arena Semantics](#arena-semantics) (cardinality, competition, context-policy, source-trust-policy) as meta-attestations on the kind entity. Cascade composes kind-typed walks the way a forward pass composes typed projections — but the substrate's vocabulary is **usage- and structure-shaped**, not transformer-position-shaped.

### Attestation Tuple Shape

The attestations table is one universal table; the *logical shapes* it accommodates are a fixed enumeration determined by which of the 5-tuple slots `(subject, kind, object, source, context)` are populated plus the cardinality semantics of each kind:

- **Binary**: subject + kind + object (`Fuck CO_OCCURS_WITH You`).
- **Binary contextual**: + context (a POS reading bound to a sentence).
- **Unary**: subject + kind (`X IS_PUNCTUATION`).
- **Unary valued**: subject + kind + scalar value (rating column carries the scalar, e.g., `HAS_FREQUENCY rating=12345`).
- **N-ary**: object or context references a tuple-entity built canonically from N entity refs (for higher-arity relations).
- **Meta**: subject is an attestation (attestations have content-addressed `id`s, so they are themselves entities — meta-attestations on a tensor-calculation attestation can attach `AT_LAYER`, `AT_HEAD`, `AT_POSITION` without schema change).

The fixed shape list is the substrate's reusable abstraction surface: adding a new modality or source requires only choosing which shapes apply to its kinds, not adding columns.

### Physicality

A per-source, per-kind 4D representation of an entity. **One-to-many entity→physicality** — same entity can carry many physicalities. Stored in `physicalities`, keyed `(entity_id, source_id, kind)` with its own `id` PK. Each physicality row holds the geometry/structural view this source provides on the entity *under this lens*:

- `coord` — 4D point (the entity's position under this source's view)
- `hilbert_index` — 1D B-tree sort key over `coord`
- `trajectory` — mantissa-packed LINESTRING (if this kind records a structural decomposition)
- `alignment_residual`, `source_dim`, `observed_at` — per-source metadata

Physicality kinds:

- **CONTENT** — the bottom-up, decomposition-bearing view. `trajectory` populated; `coord` = aggregate of constituent positions.
- **BUILDING_BLOCK** — the top-down, used-as-constituent view. Coord aggregates over parents that reference this entity; trajectory typically NULL.
- **PROJECTION** — the source's embedding-space view, procrustes-aligned to substrate 4D. Trajectory optional (NULL for vampire-mode AI ingestion).

A pixel entity may have a CONTENT physicality from the substrate-canonical source + PROJECTION physicalities from each AI vision model that has been probed on images containing it. A text word entity may have CONTENT (substrate-canonical UAX#29 decomposition), BUILDING_BLOCK (how it's used across the corpus), and PROJECTION physicalities from every linguistic resource and AI model that has observed it.

### Source

An entity that emits attestations and/or physicalities. Linguistic resources (Unicode/UCD, WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020), AI models, knowledge graphs, text corpora, the substrate-canonical placement engine itself, and the running app session are all sources. Sources are substrate entities; their per-kind credibility is tracked via meta-attestations.

### Source Trust Class

The prior trust band assigned to a source before per-kind credibility updates: foundational constants, standards-derived sources (Unicode/UCD/UCA/UAX), curated academic resources (WordNet, UD), academically linked user-curated resources (OMW, Wiktionary), structured corpora, AI-model probe observations, and prompt-local/user content. Trust class is not truth by fiat; it weights Glicko-2 agreement/disagreement inside an arena.

### Context

An entity representing the context in which an attestation holds. For context-bound attestations (e.g., a POS reading valid only in a specific sentence), `context_id` references a context entity. For context-free attestations (e.g., a universal IS_A), `context_id` is NULL.

### Data Class (app / substrate / user)

A **source-axis** distinction, not a storage-axis distinction. All three flow into the same `entities` / `physicalities` / `attestations` tables; the difference is which source entity attests what.

- **Substrate data** — entities + attestations sourced from seed/canonical sources (Unicode/UCD, WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020, AI model probes). Carries the substrate's accumulated typed knowledge — attestations of every kind, rich enough to drive cascade inference.
- **App data** — attestations sourced from the running app/runtime (session telemetry, internal app state attestations, derived signals). Trust class: app-specific, generally narrow scope.
- **User data** — entities arriving from user content (prompts, uploads, queries) **typically WITHOUT prior attestations**. Content dedupes by hash to existing rows: a user prompt containing common words inherits the substrate's full attestation cloud on those rows *for free*. The substrate may also derive its own attestations at ingest (POS tagging, decomposition, structural relations) under the substrate-canonical source. User-supplied attestations (if any) attach with `source_id = <user/session entity>` and prompt-local/user-content trust class.

Operational implication: a user prompt `"what is a cat?"` decomposes to entities that already exist (the words/graphemes/codepoints), each carrying attestations from WordNet, Wiktionary, ConceptNet, model probes, etc. The cascade has dense substrate-supplied knowledge to walk; the user only contributes the *query shape*, not the *knowledge*.

### Prompt Ingestion

The rule that prompts are decomposed into substrate entities and represented by a context entity/trajectory before inference. A prompt is not an ephemeral token buffer with a context-window limit; it is substrate content, either ephemeral or durable by policy. Prompt entities dedupe against existing rows by hash, so the cascade enters with full substrate-attestation context available on the prompt's constituents — see [Data Class](#data-class-app--substrate--user).

---

## Geometric concepts

### Glome (S³)

The 3-sphere — surface of the 4-ball. The substrate's atomic layer (Unicode codepoints) lives on S³ via super-Fibonacci spirals + Hopf fibration as the substrate-canonical CONTENT physicality coord for T0 entities. The geometric layer is **value-additive enrichment**, not load-bearing: cascade inference is A* through the attestation graph weighted by Glicko-2 (see [A*](#a-in-laplace-context)); strip S³ and the substrate still functions. S³ accelerates candidate narrowing via Hilbert range scans, provides multi-vertical NN, and supports visualization + modality clustering.

### 4-Ball

The interior of S³. Holds centroids of T≥1 entities (under the substrate-canonical CONTENT physicality coord). **Radial distance from origin = abstraction level** — concrete primitives at the surface (r=1); abstract concepts at the center (r→0).

### Hyperbox `[-1, 1]⁴`

Bounding box of the 4-ball. The Hilbert curve fills this hyperbox (not the sphere), letting one curve index both surface and interior physicality coords with consistent 1D locality.

### Canonical coordinate

The 4D position of an entity under the **substrate-canonical source's CONTENT physicality**. Stored in `physicalities.coord` for the row keyed `(entity_id, source_id=<substrate-canonical>, kind=CONTENT)`, NOT as a column on `entities`. For T0 atoms: derived from UCD (super-Fibonacci + Hopf + UCA). For T≥1 entities: centroid of constituents' canonical coords. External sources have their own non-canonical CONTENT / PROJECTION physicalities with their own coords.

### Hilbert index

128-bit (4D × 32-bit-per-dim) space-filling curve position of a physicality `coord` within the bounding hyperbox. Stored per physicality (one Hilbert index per row in `physicalities`). Used for 1D locality-preserving B-tree range scans within a source's view.

### Radial abstraction

The principle that distance from origin in the 4-ball encodes abstraction level. Generalization = move radially inward; specialization = move radially outward; same-abstraction-level traversal = move along an iso-radial sphere. Holds under the substrate-canonical CONTENT physicality.

### Mantissa packing

Encoding of an entity reference + per-vertex metadata into the 4 × FP64 components of a `physicalities.trajectory` LINESTRING vertex. Per ADR 0012, the 212-bit budget (4 × (1 sign + 52 mantissa); biased exponent pinned to `0x3FF` so each coord is a finite normal double in `[1, 2) ∪ (-2, -1]`) is split:

- **XYZ** carries the full 128-bit `entity_id` (no truncation; same value as the referenced entity's `entities.id` PK).
- **M** carries `ordinal` (16) + `run_length` (16) + 21 reserved flag bits.
- 31 additional reserved flag bits ride in Z's high half. 52 reserved flag bits total — for modality, continuation, visualization, indexing.

Same entity → identical XYZ bit pattern across every trajectory vertex referencing it (a *hash-bit-pattern scatter*; distinct from the entity's canonical-coord or projection-physicality positions). Trajectory vertices have no per-vertex spatial meaning in S³ terms.

### Trajectory vs. linestring

LINESTRING is the geometry subtype (a PostGIS concept). Trajectory is the substrate concept (a per-source-per-kind structural decomposition stored on a physicality row). The `physicalities.trajectory` column stores a LINESTRING-typed geometry.

---

## Algorithmic concepts

### Super-Fibonacci

Marc Alexa's 2021 algorithm for quasi-uniform distribution of points on SO(3) ≈ S³. Used to place 1,114,112 Unicode codepoints on S³ surface (as the substrate-canonical CONTENT physicality coord) from their UCA-sorted indices. Deterministic, single-pass, distribution near-optimal.

### Hopf fibration

Decomposition of S³ as S¹ fiber over S² base. Used (alongside super-Fibonacci) to give codepoint canonical coords structured coordinates aligned with UCA ordering.

### UCA (Unicode Collation Algorithm)

ICU-provided deterministic sort order for Unicode codepoints. Used to assign each codepoint its position in the super-Fibonacci sequence.

### Procrustes alignment

SVD-based optimal rigid transform mapping one point set onto another. Used to align an ingested source's N-dim embedding space to the substrate's 4D canonical positions via shared Unicode-anchored entities. Produces the source's PROJECTION physicalities.

### Laplacian eigenmaps

Nonlinear dimensionality reduction preserving local graph structure. Used in the alignment pipeline before Procrustes to reduce a source's N-dim embedding space (or relational structure, in the case of linguistic resources) to a manageable intermediate dimension.

### Gram-Schmidt orthonormalization

QR decomposition producing an orthonormal basis. Used in the alignment pipeline between Laplacian eigenmaps and Procrustes.

### Glicko-2

A rating system extending Glicko (Mark Glickman). Provides rating + rating-deviation + volatility per rated item, with time decay built in. Used in Laplace to **rate every attestation** — the substrate's analog of weight magnitude in an AI model. The cascade orders typed-edge traversal by Glicko-2-derived [Effective Mu](#effective-mu) the way a forward pass weights activations by trained weight magnitude.

### Arena

A semantically-coherent subset of attestations whose ratings compose. Defined by the attestation-kind hierarchy: e.g., all `HAS_POS` attestations form one arena; all `Q_PROJECTS` attestations sourced to one model form another. Ratings within an arena are commensurable; ratings across arenas are not.

### Arena Semantics

The metadata attached to an arena or attestation kind that tells Glicko-2 how to interpret agreement and disagreement: multi-valued compatibility, functional cardinality, inverse-functional cardinality, mutually exclusive object sets, scalar axes, temporal/context requirements, source-scope rules, and competition sets. `rake HAS_POS NOUN` and `rake HAS_POS VERB` can coexist; `France HAS_CURRENT_CAPITAL Paris` and `France HAS_CURRENT_CAPITAL Los Angeles` compete in the same current-capital arena/context.

### Effective Mu

The traversal/synthesis score derived from Glicko-2 rating (`mu`) adjusted by rating deviation, volatility, source credibility for the attestation kind, context compatibility, source trust class, and structural support. Hot-path selection orders by effective mu, not raw source count. In the forward-pass analogy: this is the substrate's "weight magnitude after activation and gating".

### Lottery-Ticket-Aware Sparsity

Multi-pass filter for AI model ingestion: (a) per-tensor top-k% by importance; (b) per-row top-k for attention / MLP structure preservation; (c) probe-validated retention test. **NEVER a flat numeric threshold.** Applies to weight-based sources only. Linguistic resources are ingested at full fidelity.

The same principle applies to the substrate's attestation set as a whole: most candidate attestations are silent (no source has asserted them, or they're low-effective-μ noise); the load-bearing ones drive cascade traversal. The substrate is sparse-by-construction at the attestation graph level too — most positions in the (subject × kind × object × context) tensor are exact zero, just like most positions in a sparse model's weight tensors.

### Noise floor

The lottery-ticket-aware filter applied during AI model ingestion that discards gradient jitter, init noise, training artifacts. Zero-and-near-zero weights are trash, not data. NOT a flat threshold.

### Cascade (cascading-tier NN)

The substrate's inference algorithm. **A forward pass over the typed attestation graph** — the composition pattern is structurally analogous to a transformer's layer-by-layer evaluation (typed projection, weighted aggregation, nonlinear semantic composition), but the substrate's actual vocabulary of typed edges is usage- and structure-shaped, not transformer-position-shaped:

```
query entity → walk CO_OCCURS_WITH / FOLLOWS / PRECEDES   (usage projection — substrate's own typed transforms)
              → walk IS_RELEVANT_FOR / OCCURS_IN_CONTEXT  (relevance weighting via context + arena)
              → walk IS_A / IS_HYPERNYM_OF / IMPLIES      (semantic composition through typed taxonomy)
              → walk Q_PROJECTS / K_PROJECTS / V_PROJECTS (if AI-model-sourced; tensor-calculation kinds between tokens)
              → aggregate by Glicko-2 effective-μ          (weighted combination — activation analog)
              ... cascade across tiers / radially ...
              → ranked answer entities at goal region
```

Each typed walk is one functional transform; the cascade composes them. Each step is O(tier) ≈ O(constant) under good source-trust + arena scoping. Geometric candidate narrowing (Hilbert range, physicality-coord KNN) accelerates the walks; the *semantic decision* is the typed-edge traversal weighted by Glicko-2.

### Compiled Cascade

The implementation rule for cascade traversal: one SQL-call surface enters a C/C++ set-returning function that owns frontier management, priority queues, visited sets, tier transitions, context checks, and ranking. SPI/executor access may perform batched indexed lookups; recursive CTEs, cursors, and app-layer row-by-row loops are not the hot path.

### Multi-vertical NN

Querying the substrate via multiple orthogonal similarity dimensions simultaneously. **Primary vertical: attestation-graph A* weighted by Glicko-2** — this is the inference engine. **Enrichment verticals: geometric (canonical-coord / projection-physicality position), content (n-gram / trajectory structural)** — accelerate candidate narrowing and add semantic context without driving the decision. All composable across tiers.

### A* (in Laplace context)

Best-first graph search through the **typed attestation graph**. Edge cost = function of [Effective Mu](#effective-mu) (Glicko-2 rating + RD + volatility + source credibility for kind + context compatibility + structural support). Heuristic h() = lower-bound estimate of remaining cost to goal region. Streamed via set-returning C function for incremental token generation. The A* search IS the substrate's forward pass.

### Honest Abstention

The substrate-native refusal mode caused by missing or weak path support: no viable path, low effective mu, high RD, high volatility, unresolved arena conflict, or context mismatch. Abstention is structural, not a generated phrase pattern.

### Traversal Mode

The policy that controls how cascade walks the substrate: strict mode requires high effective mu and trusted source scopes; speculative mode surfaces uncertain paths with uncertainty intact; creative/fiction modes deliberately allow lower-rated, analogical, or context-marked walks. Hallucination is therefore an explicit traversal choice, not an opaque failure mode.

### Truths Cluster / Lies Scatter

The source-rating principle that true claims tend to gather support across independent, high-trust, structurally adjacent sources, while unsupported claims scatter or cluster only inside correlated low-trust source families. Low-trust clusters can be stored as claims-about-sources without winning strict truth-seeking arenas.

---

## Codec concepts

### AI⇄DB codec

The reframing of AI operations as database operations. **The substrate's attestation graph + Glicko-2 + A* cascade IS a forward pass** — same shape of computation as a transformer's matmul + softmax + activation pipeline, evaluated by typed-edge traversal instead of matrix arithmetic:

- ingest = INSERT entities + emit attestations of typed kinds
- train = `WHERE` clause / source scope
- distill = `SELECT INTO model_file` via Substrate Synthesis
- prune = `DELETE` low-effective-μ attestations
- unlearn = `DELETE WHERE source_id = X`
- combine = ingest multiple sources into the same substrate
- inference = cascade A* traversal weighted by Glicko-2 effective-μ

### Model-Codec Fidelity

For source-scoped model round-trip, the property that `TransformerModelSource` captures the source model's load-bearing computation as recipe metadata, tokenizer content, physicalities, probe observations, architecture-specific attestations, and lottery-ticket sparse edges. If ingestion and synthesis are faithful under the source's own recipe/scope, the emitted model should land in the source model's behavioral basin.

### Vampire mode

The substrate's posture toward AI model weights: drain knowledge into attestations; **discard the weight bytes entirely**; synthesize fresh packaging on demand. Model files do NOT become substrate entities at the byte level — the substrate never preserves model weights bit-perfectly. Only the recipe, tokenizer, architecture-template entities and the extracted typed attestations remain. Round-trips emit *fresh* files from current substrate state, not copies.

### Content recording

The trajectory facet of an entity, stored on its **CONTENT-kind physicality**: a 4D `ST_LineString` whose mantissa-packed vertices reference the constituent entities (per ADR 0012). Walking the trajectory recursively reconstructs the original canonical bytes. Applies uniformly to text, code, pixels, patches, regions, images, audio frames/tracks, model recipes, structured-data sources, prompts — anything decomposable into the tier hierarchy. **Not exclusive with the attestation facet** — every entity may have both a CONTENT physicality and attestations; they are parallel facets.

### Attestation graph

The attestation facet of an entity: rows in `attestations` whose subject/object/kind/source/context resolve to entity IDs. Holds typed semantic relations with Glicko-2 ratings (per ADR 0036). **Parallel to** the trajectory facet, not an alternative. The asymmetry for AI model weights — keep the attestation graph, drop the weight bytes — is a property of the AI-weights source (see [Vampire mode](#vampire-mode)), not a partition of entity space.

### Recipe

The architectural template of a model. Auto-extracted at ingest from `config.json` + `tokenizer.json` + auxiliary files; stored as a `Model_Recipe`-typed entity with typed attestations (`HAS_HIDDEN_SIZE`, `HAS_NUM_LAYERS`, etc.). Used as the default template for round-trip emission. User can override with custom JSON.

### Substrate Synthesis

Fully parametric model emission from substrate state. Reads three inputs:

1. **Recipe** — text/JSON content on the recipe entity (structural source of truth: num_layers, num_heads, per-tensor token vocabularies, dimensions, layout).
2. **Cross-source consensus** of the fixed-vocabulary tensor-calculation attestations (`EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`) between substrate token entities. For each (subject, kind, object, context) tuple the recipe needs, the substrate aggregates Glicko-2 effective-μ across every source that has attested that relationship — source-trust weighted, arena-scoped, with cross-source agreement clustering ("Truths Cluster"). Source attribution rides on individual attestation rows for traceability, but the emit-time value is consensus. The recipe's `knowledge_scope` field optionally narrows source scope (e.g., "only Qwen3 sources" or "Qwen3 + Llama union").
3. **Architecture template** (substrate code, per `IArchitectureTemplate`) — distributes the consensus values across the recipe's tensor slots per the layout.

Output: a model file of any shape, deterministically materialized from substrate state. Positions with no significant consensus emit zero (R4 sparse-by-construction). Substrate-consensus weights under the chosen recipe + scope, never bit-perfect copies of any source (Vampire mode). Ingesting more independent high-trust sources strengthens the consensus on agreed relationships and surfaces disputes on the rest — emitted models improve as substrate accumulates observations, without retraining.

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
- **`laplace_geom`** — general-purpose 4D additions to PostGIS: `ST_*_4d` functions, BLAKE3-128 helpers on `bytea(16)`, Hilbert encoder, mantissa pack/unpack, custom GIST opclasses for S³-aware indexes
- **`laplace_substrate`** — substrate schema: three core tables, attestation kind hierarchy, Glicko-2 aggregate, cascade SRFs, custom SP-GiST + BRIN opclasses

Thin wrappers around engine functions, exposed via `PG_FUNCTION_INFO_V1`. Add custom 4D-aware functions where standard PostGIS is 2D/3D-only.

### Perf-cache

Memory-mapped binary file containing precomputed T0 codepoint data: IDs, 4D canonical coords, Hilbert indices, UCA orders, flags. Built once at deploy time from Unicode UCD. ~67 MiB; fits in CPU L2/L3 cache.

### Build pipeline (Unicode UCD → artifacts)

The deterministic derivation that produces TWO sibling artifacts from Unicode UCD: (1) the perf-cache binary, (2) the DB seed of T0 entity rows + their substrate-canonical CONTENT physicalities. Neither feeds the other; both trace to Unicode itself as the canonical source.

### Three-phase architecture

The substrate's lifecycle phases:
- **Build** (one-time): derive perf-cache + DB seed from UCD; bootstrap substrate-canonical source entity + Entity Type entities.
- **Ingestion** (per write): C/C++ engine canonicalizes content per type, computes IDs + physicalities + attestations; raw INSERT or skip-on-dedup.
- **Prompt ingestion** (per request): decompose prompt to substrate entities; create/reference context entity; dedup against existing rows so existing attestation cloud is reachable.
- **Query** (per read): C/C++ extension reads perf-cache + B-tree/GIST; compiled cascade A* walks indexed attestations weighted by Glicko-2 effective-μ.
- **Rating accumulation**: Glicko-2 updates fire on observation events — the only runtime DB-side compute.

### Polymorphic plugin architecture

The discipline that adding new capability touches ONE plugin, never all layers. Six plugin interfaces: `ISource`, `IDecomposer`, `IArchitectureTemplate`, `IFormatWriter`, `IFeatureExtractor`, `IProtocolEndpoint`.

### Probe (in ingestion context)

Running an AI model on input data to observe its outputs/attention/activations, then extracting attestations from significant observations. Required for model ingestion (vs. parse-based ingestion of pre-structured sources). May need GPU at probe time for large models; results stored CPU-native afterward.

### Round-trip

The proof-of-concept workflow: ingest model M → emit M' using M's own Recipe as template, populated from substrate attestations → load M' in a sparse-aware runtime → chat with it. M' is architecturally identical to M but weights are substrate-consensus, not M-original.

### Endpoint extension

A plugin in the C# app layer that exposes a protocol (e.g., OpenAI-compat) over the substrate. Translates protocol requests into substrate queries; translates substrate responses into protocol responses. Dissolves the need for conventional inference runtimes — the substrate IS the serving layer.

---

## Per-source decomposition (modality-specific)

Each ingestion source defines its own decomposition rule (an IDecomposer plugin) and its own set of attestation kinds. All sources emit into the same `entities` / `physicalities` / `attestations` tables; all bottom at Universal T0.

### WordNet

- **Entities produced**: `Text` (lemma text entities), `WordNet_Synset` (the synset itself, content-addressed by canonical (POS + sorted lemmas + gloss hash) bytes), `Text` for glosses + example sentences.
- **Attestation kinds emitted**: `IS_LEMMA_OF`, `HAS_POS<part_of_speech>`, `HAS_GLOSS`, `HAS_EXAMPLE`, `IS_HYPERNYM_OF`, `IS_HYPONYM_OF`, `IS_MERONYM_OF`, `IS_HOLONYM_OF`, `IS_ANTONYM_OF`, `IS_SIMILAR_TO`, `IS_DERIVATIONALLY_RELATED_TO`.
- **Source trust class**: curated academic resource.
- **Physicalities**: PROJECTION from Laplacian-eigenmaps on the synset relation graph, Procrustes-aligned via shared Unicode anchors.

### OMW (Open Multilingual WordNet)

- Reuses WordNet's synset entities (cross-lingual synset IDs map to the same `WordNet_Synset` entities).
- New lemma entities per language; new `IS_LEMMA_OF<language=<lang>>` and `HAS_LANGUAGE` attestation kinds.
- Cross-lingual sense bridges traversable via lemma → synset → lemma in any language.
- **Source trust class**: academically linked user-curated resource.

### UD Treebanks

- **Entities produced**: `UD_Sentence` (per parsed sentence), `UD_Token` (per token-in-sentence), reused `Text` entities for token surface forms and lemmas.
- **Attestation kinds emitted** (mostly context-bound to the sentence entity): `HAS_POS<pos>` (context = sentence), `HAS_LEMMA` (context = sentence), `HAS_MORPHOLOGICAL_FEATURE<name>` (context = sentence), `IS_HEAD_OF<relation>` (head→dependent dep arc, context = sentence).
- Arena semantics: `HAS_POS` is multi-valued lexically but functional within a context.
- **Source trust class**: curated academic resource.

### Wiktionary

- **Entities produced**: `Wiktionary_Entry`-typed entities per word entry; `Text` for definitions, etymology, IPA; `Audio_Track` for pronunciation recordings.
- **Attestation kinds emitted**: `HAS_POS_SECTION`, `HAS_DEFINITION`, `HAS_ETYMOLOGY`, `HAS_IPA_PRONUNCIATION`, `HAS_AUDIO_PRONUNCIATION`, `HAS_INFLECTION_FORM`, `IS_TRANSLATION_OF<source_lang, target_lang>`, `HAS_USAGE_EXAMPLE`.
- **Source trust class**: academically linked user-curated resource.

### Tatoeba

- **Entities produced**: `Tatoeba_Sentence`-typed (sentence text content), `Audio_Track` for recordings.
- **Attestation kinds emitted**: `IS_TRANSLATION_OF<source_lang, target_lang>` between sentence entities, `HAS_RECORDING<voice>` linking sentence → audio.
- Same sentence text observed in multiple Tatoeba pairs dedupes by hash; one row, many translation-pair attestations.
- **Source trust class**: structured corpus.

### Atomic2020

- **Entities produced**: `Atomic2020_Event`-typed entities (event text content).
- **Attestation kinds emitted**: `BECAUSE`, `INTENDS`, `EFFECT`, `REQUIRES`, `IS_AFTER`, `IS_BEFORE`, `OXEFFECT_ON`, `XINTENT`, `XATTR`, etc.
- Mostly context-free commonsense inference relations.
- **Source trust class**: structured corpus (commonsense KB).

### ConceptNet

- **Entities produced**: `ConceptNet_Concept`-typed entities; multilingual via per-language lemma entities.
- **Attestation kinds emitted**: `IS_A`, `PART_OF`, `USED_FOR`, `CAPABLE_OF`, `AT_LOCATION`, `HAS_PROPERTY`, `CAUSES`, `MOTIVATED_BY_GOAL`, etc.
- Sub-sources: Wikipedia, OMCS, WordNet, JMDict, etc., each as a distinct source entity, with their own trust class within ConceptNet's umbrella.
- **Source trust class**: structured corpus (mixed via sub-sources).

### TransformerModelSource (AI Model)

AI models are not a special case. They use the same substrate primitives as every other source: tokens become text entities (deduping against existing text); typed-tensor-calculation attestations of the **fixed-vocabulary tensor-calculation kinds** are emitted between those tokens; recipe metadata is captured as ordinary attestations on the model entity.

- **Entities produced**:
  - `Model_Recipe`-typed entity (one per ingested model).
  - `Model_Tokenizer`-typed entity (one per tokenizer; may dedup across models that share a tokenizer).
  - Tokenizer vocab entries as **`Text` entities — same hash space as all other text**. BPE / SentencePiece / WordPiece markers are stripped via canonicalization so `walk%`, `▁walk`, `Ġwalk`, `##walk` all dedup to the same `walk` text entity. The marker becomes an attestation on the tokenizer entity describing how the text gets re-marked at emission time.
  - **No model-weight-bytes entity** ([Vampire mode](#vampire-mode)). **No per-layer / per-head / per-position entities** — those are scalar parameters of the architecture, captured as recipe attestations on the model entity, not as entity-identity constituents.
- **Attestation kinds emitted**:
  - **Recipe metadata** on the model entity: `HAS_HIDDEN_SIZE`, `HAS_NUM_LAYERS`, `HAS_NUM_HEADS`, `IS_A Architecture_<X>`, `USES_TOKENIZER`, `USES_ROPE_THETA`, `USES_ACTIVATION`, etc.
  - **Tokenizer marker attestations** on the tokenizer entity describing how each vocab text entity gets re-marked at emission (continuation, leading-space, etc.).
  - **Typed-tensor-calculation attestations between substrate token entities** of the fixed-vocabulary kinds: `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`. Each tensor in the model is one of these calculations; only lottery-ticket-validated load-bearing positions become attestations.
  - **No per-position meta-attestations on tensor-calculation attestations.** Per-position structural attribution (which layer / which head / which tensor index a calculation lives at) is **recipe content** — captured in the model's recipe entity (text/JSON describing num_layers, num_heads, per-tensor token vocabularies, layout). The architecture template wires substrate's aggregated typed attestations into the recipe's structural shape at emit time; redundant per-position storage on attestations is not needed.
- **Physicalities**:
  - **PROJECTION** from the model's embedding space, Procrustes-aligned to substrate 4D via shared codepoint/word anchors.
  - **CONTENT** physicalities on probe input and output sequence entities, sourced to the model's tokenizer source, with mantissa-packed trajectories carrying the token references in the tokenizer's BPE order (ordinal = position, run_length = repeats, flags = BOS/EOS/continuation). Same mantissa-pack primitive as text trajectories, prompts (R19), and any other sequence content — token sequences are content; no new mechanism.
  - **CONTENT** physicalities on each tokenizer vocab text entity from the model's tokenizer source, recording this tokenizer's view of how that text decomposes (BPE merges into sub-tokens, etc.).
- **Source trust class**: AI-model probe observation.

The substrate's inference engine walks these typed-tensor-calculation attestations between text entities directly. Reconstruction (Substrate Synthesis emitting a model file) reads recipe metadata + tensor-calculation attestations + the architecture template (substrate code, not data) to repopulate the model's tensor slots.

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
