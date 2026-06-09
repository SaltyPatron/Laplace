# Glossary

Authoritative vocabulary. Terms are laws, not suggestions; code and docs use these exactly.

## Identity & Structure

**Entity** — the unit of knowledge: a row in `laplace.entities` whose `id` is the BLAKE3-128 of its canonical content bytes. Identity is computed from content; the same content is the same entity everywhere, forever.

**Content addressing** — the identity law: `id = BLAKE3-128(canonical bytes per type)`. Raw 16-byte `bytea`, never hex, never text. Uniqueness is enforced by the primary key alone; restating it with secondary UNIQUE constraints is a measured anti-pattern (~144 B/row of duplication).

**Tier** — compositional altitude in the Merkle DAG (`entities.tier`, 0–4 for text content). 0 = codepoint (T0 atom), 1 = grapheme, 2 = word, 3 = sentence, 4 = document. Meaningful **only** when `type_id` is a compositional text type (Codepoint/Grapheme/Word/Sentence/Document). Vocabulary rows (sources, relation types, synsets, etc.) use `tier = 0` as an inert FK placeholder — interpret them via `type_id`, never via tier.

**T0** — the atomic tier. For text: Unicode codepoints, 1,114,112 of them, placed by deterministic law (super-Fibonacci) and never moved. T0 is the fixed reference lattice the entire geometric frame anchors to.

**Merkle identity / hash composition** — T0 leaves: `id = blake3(canonical UTF-8 atom)`. T1+: `id = blake3(0x01 ‖ child_id₁ ‖ child_id₂ ‖ …)` — fixed domain byte `0x01`, **not** the compositional tier. Tier is stored separately in `entities.tier` and is never hashed. Example law (pinned by regress): `word_id('dog') = blake3('\x01' || id('d') || id('o') || id('g'))`.

**Constituent** — an ordered child of a composed entity, carried in the parent's trajectory. Decomposition and reconstruction are exact operations, not statistical ones.

**Canonical name** — a human-readable handle registered in `canonical_names` (id = `canonical_id(name)` = BLAKE3 of UTF-8 name). The static vocabulary (relation types, entity types, trust classes, POS values, sources) ships in the extension seed; dynamic families register at ingest.

**render() / realize()** — readback: entity id → human-readable text. `render` resolves canonical name, codepoint, or recursive text reconstruction; `realize`/`realize_path` produce natural-language renderings of entities and relation paths, direction-aware. Language is a render-time choice, not a property of knowledge.

## Testimony & Adjudication

**Witness** — any source of knowledge: a standards file, a curated lexicon, a corpus, a document, a user prompt, or a deposed AI model. All witnesses enter through the same machinery and differ only in trust class.

**Deposition** — the act of ingesting a witness: decomposing its content into entities and attestations.

**Safetensor snapshot witness** — a HuggingFace-style directory (`config.json` + `tokenizer.json` + `*.safetensors`), deposited via `ModelDecomposer`: recipe parsed from config, vocab from tokenizer, named weight tensors walked by role into tensor-relation testimony. **Not self-contained** — unlike GGUF, a lone `.safetensors` file carries no architecture or vocab.

**GGUF render target** — synthesis output: self-contained runnable artifact (metadata + tokenizer blobs + aligned tensors). Ingest uses distributed safetensor snapshots; export compiles adjudicated arenas back into GGUF.

**Attestation** — one row of evidence in `laplace.attestations`: WHO (source) witnessed WHAT relation (subject, relation-type→`type_id`, object) in what CONTEXT, with what OUTCOME class and observation count. Provenance only — never a value channel; a witness's magnitude is consumed into consensus at ingest and not persisted. Id = BLAKE3 of the canonical 5-tuple (subject, relation type, object, source, context) → re-observation is UPSERT-no-op idempotent.

**Outcome** — the dissent record as a class, never a magnitude: 0 = refute (loss), 1 = draw, 2 = confirm (win).

**Context (context_id)** — the circuit instance of the witnessing: for model witnesses, the layer/head entity; for text, the containing document (stamping this is open work — see OPEN-PROBLEMS). Context is a witness refinement, never part of relation identity.

**Trust class** — a witness's evidentiary rank, itself an entity: `SubstrateMandate`, `StandardsDerived`, `AcademicCurated`, `AcademicCuratedWithUserInput`, `StructuredCorpus`, `UserCuratedResource`, `UserPromptContent`, `AppDerived`, `AIModelProbe`, `AdversarialUntrusted`. A model's testimony is admissible and outranked by the dictionary.

**Consensus** — the adjudicated truth layer: ONE row per (subject, relation type, object) in `laplace.consensus`, carrying Glicko-2 state accumulated over ALL witnesses. Source and context are excluded from consensus identity — they are witnesses, never identity. Id = BLAKE3(subject ‖ relation_type ‖ object|zero16).

**Glicko-2** — the rating system used as the adjudication engine (int64 fixed-point ×1e9 throughout; engine kernel is the single source of math truth). Priors: rating 1500e9, RD 350e9, volatility 0.06e9, τ = 0.5e9. Each witness observation is a game against the neutral 1500 line with score s = ½(1 + tanh(m/M)).

**μ / eff_mu** — effective strength: `rating − 2·RD` (the ~95% conservative lower bound), SIGNED, fixed-point ×1e9. THE one definition, planner-inlined, matched exactly by the consensus expression indexes. Never hand-write the expression. `eff_mu_display` = ÷1e9 at 3 dp.

**RD (rating deviation)** — per-relation uncertainty. High RD = under-witnessed/contested = a frayed edge.

**Refuted** — confidently denied: upper bound `rating + 2·RD` below the neutral 1500e9 baseline. Refuted edges are pruned from traversal and realization but remain visible to ranked reads — dissent is preserved, not erased.

**Period fold** — the consensus materialization: writers accumulate per-relation period partials (games, sum_score, φ) into unlogged staging partitions; `materialize_period_consensus` folds each into consensus via the batch kernel `laplace_glicko2_accumulate_games` (bit-identical to replaying the games one by one, without pushing games×relations rows through the executor). Accumulation invariant: one φ per relation per period.

**φ (phi)** — the opponent deviation in a witness's games, derived from trust (WitnessPhi). Trust→φ mapping currently lives in code constants; moving it to substrate policy tables is open work.

**Witnessed gap** — structural "I don't know": an empty cascade or absent arena. Queryable via `gaps()` / `epistemic_status()`.

**Provable negative** — the closed-world capability: `count(*) = 0` over attestations proves the system has never been told something. Demonstrated (7.2 ms) and impossible in principle for weight-based models.

**The flip** — the canonical live-learning demonstration: prove ignorance at T0, ingest one sentence, prove attributed timestamped knowledge at T2. First performed 2026-06-07 02:35 ("Ahab admired Darcy…").

## Arenas & Relation Types

**Relation type** — a vocabulary entity for a relation (`substrate/kind/NAME/v1` — byte path is stable). Resolved via `relation_type_id(name)`. The attestations/consensus column is `type_id`. Term: **relation type** in code and docs; never confuse with physicality `type` or entity `type_id`.

**Arena** — the relation plane for one relation type; traversal and ranking happen per-arena or across arena sets.

**Tensor-role arenas (the ten)** — `EMBEDS, Q_PROJECTS, K_PROJECTS, V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS, DOWN_PROJECTS, NORM_SCALES, OUTPUT_PROJECTS`: the architecture-agnostic relational algebra of transformation. Model weights testify into them at deposition; synthesis pours from them at export.

**Sequence arenas** — text-side structure: `FOLLOWS, PRECEDES, CO_OCCURS_WITH, OCCURS_IN_CONTEXT`. The document path currently attests `PRECEDES` as immediate adjacency (bigrams).

**Query-time bilinear reads** — `ATTENDS, OV_RELATES, COMPLETES_TO`: composed across arenas at read time; never ingest-written, never gated.

**Cascade** — compiled traversal: the native A* SRF (`astar_path_raw`, C + SPI neighbor provider) over chosen arenas, edges weighted by μ (stronger relation = cheaper hop), refuted edges pruned. NOT recursive SQL, NOT an app loop. `cascade(x,y)` renders the least-cost path between two words.

## Geometry

**S³ / the jar** — the unit 3-sphere in 4D where surface entities live; interior of the 4-ball holds composed centroids. "The jar" is the shared, anchored frame all witnesses' geometry is aligned into.

**Super-Fibonacci** — the deterministic uniform placement law for T0 atoms on S³. Same input ⇒ byte-identical coordinates across every consumer (the 1-ULP determinism law; emit and runtime share one compiled kernel).

**Hilbert key (hilbert_index)** — 16-byte locality-preserving 1-D key from the 4D Hilbert curve (Skilling). Plain B-tree range scan = spatial neighborhood. Identical position ⇒ identical key, which makes multiset lookup (anagrams) a B-tree equality (31 ms vs 27 s spatial; discovered 2026-06-07).

**Physicality** — a per-source per-kind 4D view of an entity: `physicalities(entity_id, source_id, type, coord PointZM, hilbert_index, trajectory, radius_origin, n_constituents, alignment_residual, source_dim, observed_at)` with UNIQUE(entity, source, type). RULED (2026-06-07): physicalities is the ONE geometric home; firefly placements are physicalities rows (per-tensor-role `type`, circuit-entity `source_id`); the separate geometry evidence/consensus tables retire.

**Physicality kind (`type` column)** — what a placement is: `BUILDING_BLOCK`, `CONTENT` (=1, the realized content view), `PROJECTION`, `PROJECTION_OUTPUT`, extended per tensor role for model placements.

**Trajectory** — the constituent sequence as a LINESTRING whose vertices are mantissa-packed: XYZ mantissas carry the child's 128-bit id; M carries ordinal, run_length, and flags. Stores IDENTITY, never coordinates — by law: consensus positions can move; identity is the only stable cargo. T0 constituents are additionally self-describing inline (`vertex_atom`: flag bit 0 + 21-bit codepoint).

**Mantissa packing** — the 212-bit payload split across 4×FP64 mantissas (`laplace_mantissa_pack/unpack`).

**Realized curve** — the geometric form of a sequence: join constituents to their live coordinates and `ST_MakeLine` in ordinal order (`word_curve`). Curve math (Fréchet/Hausdorff) always runs on realized curves, never stored trajectories.

**Two-channel identity (position vs curve)** — discovered law: an entity's POSITION encodes its constituent multiset (anagrams collide exactly — whale/wheal/waleh at geodesic 0); its CURVE encodes constituent order (Fréchet separates what position cannot). Point = composition; curve = sequence.

**Angular / geodesic distance** — the correct S³ metric: `laplace_angular_distance_4d` = acos over normalized 4-vectors. Karcher means and neighbor semantics use this, never the Euclidean chord — though chord and angle are monotone on the sphere, so ND-GIST chord-KNN (`<<->>`) yields exact angular ranking.

**Fréchet / Hausdorff (4D)** — exact curve/point-set distances (`laplace_frechet_4d` Eiter–Mannila discrete Fréchet; `laplace_hausdorff_4d`). On word curves, Fréchet = morphological/shape distance (~3.6 ms/pair measured).

**Karcher mean** — the weighted Riemannian mean on S³: iterative log-map → weighted tangent average → exp-map (`math4d_karcher_mean`, `math4d_log_s3/exp_s3`). Exists, tested; needed only if the optional geometry-consensus view is ever built.

**LE+GSO+PA** — the deposition projection pipeline: Laplacian Eigenmaps (dense or sparse-graph) reduce a witness's native space; Gram-Schmidt orthonormalizes; Procrustes aligns onto the substrate frame, residual recorded. Well-posed BECAUSE of content addressing: shared entities supply exact point correspondences ("the model's king IS the substrate's king"), and the T0 lattice + already-placed entities supply the fixed frame.

**Fireflies** — the instrument (NOT core truth machinery): each witness's placement of an entity is a distinct specimen in the shared jar — Llama's king, Qwen's king, per layer/head/expert: SPECIES of king. Supports per-entity cross-model belief distance, whole-cloud lineage forensics (Hausdorff), per-layer concept flight paths, checkpoint drift, bias geodesics, Voronoi conceptual territories, all viewable in stock GIS tooling. The species are the product; blending them adds no epistemic strength.

**Voronoi territories** — tessellation of placements into per-concept regions: membership-by-geometry, boundary proximity = ambiguity/confusability, empty cells = visible lexical gaps, cross-model territory comparison, and geometric cross-validation of relational taxonomy.

**Dual engine / orthogonality** — the two exact similarity systems: relational (ORDER BY eff_mu over witnessed arenas) and structural (S³/Hilbert/curves). Canonical numerical proof (2026-06-07): whale~while Fréchet 0.1149 with no relation; whale~ship Fréchet 1.7156 with μ 2010 @ 116 witnesses. One cosine cannot hold both truths.

**Frayed edge detection** — queryable ignorance frontier: high-RD relations (`ORDER BY rd DESC`), witnessed gaps, and geometric-proximity-without-relational-testimony (hypothesis candidates). Closes the loop: ingestion is training; frayed edges are curiosity (self-generated reading lists).

## Ingestion & Learning

**Ingestion is training** — the central identity: gradient descent and attestation are two implementations of accumulating corpus structure into arenas. Translation table: training run→`ingest`; epoch→idempotent no-op; curriculum→ladder order; learning rate→trust/φ; checkpoint→the database; fine-tune→more witnesses; catastrophic forgetting→impossible (outvoted, never overwritten); unlearning→source eviction (see OPEN-PROBLEMS §3 for consensus-state resolution paths).

**Decomposer** — a witness adapter implementing the IDecomposer contract: reads a source, emits entities/physicalities (via ContentEmitter) and attestations (via AttestationFactory/TextEntityBuilder) through IngestRunner.

**The ladder** — dependency-ordered seed ingestion: layer 0 unicode → 1 iso639 → 2 {wordnet, ud, tatoeba, atomic2020, conceptnet, wiktionary, opensubtitles, verbnet, propbank} → 3 {omw, framenet, semlink}. Strictly one source at a time by throughput law; each stage idempotent.

**Layer gate (HasLayerCompleted)** — IngestRunner refuses a source until lower layers carry completion markers (attested under `substrate/kind/HasLayerCompleted/<n>/v1`).

**Idempotency law** — content addressing + ON CONFLICT make every ingest re-runnable and convergent; there is no checkpoint apparatus because none is needed. Re-ingesting a model is refused (double-counting guard) — reset is per-source eviction or db-fresh, never bypass.

**db-roundtrip** — the document on-ramp ceremony: record (decompose → entities/physicalities/trajectories via COPY) → attest PRECEDES bigrams → period-fold consensus → reconstruct byte-exact FROM the database and compare. "BIT-PERFECT FROM DATABASE" is the pass line. Proves the store is simultaneously a perfect archive and a semantic decomposition.

**Infinite context** — prompts as testimony: conversation ingested under `UserPrompt/v1`, making context biography (O(log n) recall at any age) rather than a buffer. `converse_turns` is the session cursor; durable attestation of turns is open wiring.

**Witnessed stopwords** — function-word-ness derived from UD's HAS_POS consensus (dominant POS ∈ {DET, ADP, AUX, CCONJ, SCONJ, PRON, PART}) instead of hardcoded lists. Binding diagnosis pending (see OPEN-PROBLEMS).

## Synthesis & Models

**Mold / recipe** — the architecture template (config.json-shaped) synthesis pours into. Recipe-agnostic target_dim: same substrate, any size.

**Render target (GGUF)** — a model file as a BUILD ARTIFACT: compiled from consensus arenas, rebuildable, diffable (two builds differ exactly where consensus changed, witnesses nameable), runnable by stock llama.cpp. The static model reduced to a disposable cache of a living substrate.

**Cell ETL** — model deposition law: every non-zero weight cell is one adjudicated match under its tensor-role relation type, positions aggregating as witnesses; score s = ½(1+tanh(w/M)) with M = pooled tensor RMS; lottery-ticket sparsity, never flat noise thresholds.

**Clean-room model** — a GGUF compiled from enumerated, licensed witnesses with zero model ancestry: certifiable provenance no trained artifact can offer.

**Behavioral equivalence** — the fidelity criterion (NOT bit-identity of blobs): same prompts, same harness, diff continuations. Ground truth = `model-forward-oracle.py` (exact f64 forward pass, deliberately outside Laplace code); harness = `llama_behavioral` against `D:\LlamaCPP\llama-completion.exe`.

**Text→tensor bridge** — the keystone open lemma: the defined mapping from sequence arenas to tensor-role pours enabling the no-ancestor compile ("a model trained by reading").

## Determinism & Operations

**Determinism law** — same input ⇒ byte-identical output across consumers, compilers, OSes: no -ffast-math, FP contraction controlled, one compiled kernel per math truth, perfcache emit byte-reproducible, regress outputs byte-compared. Proven cross-OS 2026-06-07 (8/8 suites byte-identical Linux↔Windows).

**Perfcache** — the T0 runtime blob (85.4 MB): UCD+DUCET compiled to mmap-able tables (records 1,114,112; decomp/compose tables; BLAKE3 integrity trailer) consumed by the UAX#29/UAX#15 state machines. Resolution order: `LAPLACE_PERFCACHE_BIN` → share dir → build-tree walk-up.

**Two-DB law** — `laplace` = CI/production runs; `laplace-dev` = manual/dev. Code defaults to the safe dev DB; explicit env targets prod.

**The slow tier** — tonight's query numbers' honest label: interpreted SQL, scalar per-row kernel calls, no SPI offload, no SIMD batching, sequential MKL, stock MSVC PG, cold sessions, under ingest load. Two to three orders of magnitude of built-or-planned headroom sit above it (see OPEN-PROBLEMS §11).
