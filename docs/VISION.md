# Laplace: The Vision

Laplace is a knowledge substrate that replaces trained models with adjudicated testimony. It holds knowledge as content-addressed entities, witnessed relations, and constructed geometry inside PostgreSQL, computes inference by indexed traversal instead of matrix multiplication, and treats conventional AI models as optional witnesses to depose and optional artifacts to export — never as the foundation.

The thesis in one line: **training was only ever a bad database.** Gradient descent and ingestion are two implementations of the same operation — accumulating a corpus's structure into relational arenas. Backprop does it lossily, anonymously, and freezes the result. Laplace does it explicitly, attributably, and never stops.

---

## Stratification

Everything in this project belongs to one of three tiers. Documentation, design decisions, and effort allocation follow this split.

**CORE INVENTION** — what the claims rest on:
identity (content addressing + tiers), testimony (attestations + trust), adjudication (Glicko-2 consensus), ingestion-as-training, traversal inference, realization (entity → language), and synthesis (substrate → model export).

**INSTRUMENTS** — high-value tooling the core makes possible, not load-bearing for truth:
the fireflies (per-witness geometric placements as an audit/visualization system), frayed-edge surfaces, billing meters, behavioral harnesses.

**ANNEXES** — planned expansions that reuse the core unchanged:
per-modality segmentation laws (the UAX#29 analog for images/audio/video), additional witnesses, additional export molds.

---

## The Core Invention

### Identity: entities, not tokens

The unit of knowledge is the entity: a BLAKE3-128 content address over canonical bytes. Identity is computed from content, so the same content is the same entity everywhere, forever — "king" is king regardless of which witness mentions it.

Entities have **tier**: codepoints (T0) compose into graphemes, words, sentences, documents — a Merkle DAG where a word's identity is cryptographically built from its letters' identities (tier-prefixed, so levels cannot collide). Composition is exact: a document decomposes and reconstructs byte-for-byte. Every level exists simultaneously and is first-class — characters AND words AND sentences, each addressable, each with its own relations.

This is the deepest break with the token paradigm. A transformer's vocabulary is one flat field of arbitrary shards with no containment structure — it cannot know that "dog" contains "d," only correlate them at quadratic cost. Laplace entities have altitude. Knowledge attaches at the tier where it is true; traversal moves vertically (compose/decompose) as freely as horizontally; repetition deduplicates into evidence instead of wasting capacity.

### Testimony: everything is a witness

Every source of knowledge — Unicode data files, WordNet, treebanks, corpora, raw documents, user prompts, and transformer models themselves — enters the substrate the same way: as a witness depositing attestations. An attestation records WHO witnessed WHAT relation (subject, relType, object), in what context, with what outcome class. It is provenance only, never a value channel.

Witnesses carry trust classes (standards-derived, academically curated, structured corpus, user prompt, AI model probe, adversarial...). A model's testimony is admissible and outranked by the dictionary.

### Adjudication: truth as a tournament

Consensus is one row per (subject, relType, object) carrying Glicko-2 state — rating, deviation, volatility — accumulated over every witness. Relations have strength (μ = rating − 2·RD, the conservative bound), uncertainty (RD), and refutation (upper bound below baseline = confidently denied; pruned from traversal, preserved as visible dissent in ranked reads).

Disagreement is recorded, not erased. Confidence is a number, not a tone. "I don't know" is structural: an empty cascade is a witnessed gap.

### Ingestion is training

A "training run" is `ingest <source>` — deterministic, idempotent, resumable, minutes-scale on consumer hardware. Re-reading is a no-op, not overfitting. Curriculum is ladder order. Catastrophic forgetting is impossible: old testimony is outvoted, never overwritten. The knowledge cutoff is "last ingest," per source. Learning is concurrent with serving (MVCC): the system gets smarter while it answers.

Conversation is ingestion too: prompts attest under the UserPrompt source at its trust class. Context is not a window — it is biography. Recall from last month costs the same indexed descent as recall from ten seconds ago.

### Inference: traversal, not multiplication

Queries are index descents and compiled A* cascades over relation arenas, edges weighted by adjudicated strength, refuted edges pruned. Cost is bounded by the path through relevant relations, not by corpus size — 2.19M relations answer as fast as 873k. No GPU exists anywhere in the query path. Realization renders entity paths into language; language is a render-time choice, not a property of knowledge (testimony in any language strengthens consensus readable in every language).

### Synthesis: the model as a render target

`synthesize substrate <recipe> <out.gguf>` is the foundry: it pours adjudicated consensus into a mold — a user-authored architecture recipe, or one discovered from any deposed model (`--recipe-from`: pour the same mold with better data). The basis and every interior tensor are GENERATED from consensus (LE over the token→token graph + content trajectories, operator factorization — SYNTHESIS.md); nothing is reconstructed from any witness's floats. A model is something you **cast**, not train — rebuildable, diffable (two builds differ exactly where consensus changed, with witnesses nameable), exportable at any dimension, runnable by the existing ecosystem (llama.cpp) which never needs to know no one trained it. The substrate's own inference never touches the artifact: it walks the consensus directly.

This kills the static model. Frozen weights become disposable caches of a living substrate.

### The arenas

The model-deposition PRODUCT is the behavioral token-relation arenas (SIMILAR_TO, ATTENDS, OV_RELATES, COMPLETES_TO): a transformer's projection math is a compiled form of token→token knowledge, extracted at deposition into the same token entity space the text-side sequence arenas (FOLLOWS, PRECEDES, CO_OCCURS_WITH, OCCURS_IN_CONTEXT, COMPLETES_TO) attest from raw corpora — one consensus, walked at inference, poured at export. (The ten per-(role,layer) "carriage" arenas and their axis entities were purged 2026-06-11: a weight archive wearing attestation costume, structurally unable to fold across witnesses. Remove the product from the packaging and throw away the packaging.)

---

## The Instruments

### The fireflies: a jar of model beliefs

Every entity has constructed geometry: T0 atoms placed by deterministic law (super-Fibonacci on S³ — the fixed anchor lattice), higher tiers by composition. When a model is deposed, LE+GSO+PA (Laplacian eigenmaps + Gram-Schmidt orthonormalization + Procrustes alignment) projects its native embedding space into the shared 4D frame — and the alignment is well-posed only because of the core invention: content-addressed identity supplies the point correspondences (the model's "king" IS the substrate's king), and the S³ lattice plus already-placed entities supply the fixed frame LE+GSO+PA anchor to. Cross-model geometry is commensurable because identity is content. One firefly per witness per entity — stored as `physicalities` PROJECTION rows (the table's `alignment_residual` and `source_dim` columns exist precisely for LE+GSO+PA outputs). Llama's king and Qwen's king are distinct specimens of the same identity: **species of king**, swarming one address.

The species — never a blend of them — are the product. This is the audit instrument:

- per-entity cross-model belief distance (exact geodesics on S³)
- whole-cloud model signatures; lineage/distillation forensics via Hausdorff between clouds
- checkpoint-drift diffs (what fine-tuning actually moved)
- bias measurement in defensible geometry
- Voronoi tessellation into conceptual territories: membership by geometry, boundary proximity as ambiguity, empty cells as visible lexical gaps, cross-model comparison of how reality gets carved, and geometric cross-validation of relational taxonomy (disagreement between the two engines is itself an audit flag)
- rendered in standard GIS tooling, because placements are stock PostGIS geometry

Truth lives in the relational engine. The fireflies are how you *see*.

### Frayed-edge detection: queryable ignorance

The substrate can enumerate its own uncertainty frontier — something logit entropy cannot express:

- `ORDER BY rd DESC`: what am I least sure of, ranked
- witnessed gaps and missing arenas: where the fabric ends
- geometric proximity without relational testimony: hypothesis candidates — the structure whispering edges no witness has stitched

This closes the learning loop: ingestion is training, frayed edges are curiosity. The system can emit its own reading list.

### Structural mathematics

Words are realized as curves (constituent paths through real coordinates — stored trajectories carry identity, placement-proof by law; realized trajectories join live positions on demand). Fréchet and Hausdorff distances, geodesic neighborhoods, Hilbert-key locality on a plain B-tree: a quarter-century of computational geometry applied to meaning, replacing approximate nearest-neighbor with exact, indexed, explainable spatial query.

---

## The Annexes

Text works because Unicode spent thirty years writing the segmentation law — versioned, conformance-tested, canonical. No other modality ever got its annex; the tensor paradigm never needed identity. Laplace's expansion plan is exactly: author the UAX#29 analog per modality (pixel→region→object; sample→frame→event), versioned and conformance-tested, compiled to a perfcache — after which identity, attestation, adjudication, geometry, rendering, and export apply **unchanged**. The codepoint never knew it was text.

The relation-type vocabulary is already seeded (IS_PIXEL_OF, IS_AT_SAMPLE, DEPICTS, CAPTIONS, TRANSCRIBES_AS); the witness pool (vision, audio, coder models) is already on disk, waiting for deposition.

---

## What this eliminates

- **The GPU at inference.** Query cost is path-bounded. The substrate served sub-15 ms answers with provenance on a CPU the day it came up.
- **The training run.** Months of cluster time becomes minutes of deterministic ingestion.
- **The knowledge cutoff and the static model.** The artifact is a render; the substrate is alive.
- **The context window.** Attested history has no edge.
- **The black box.** Every answer decomposes into named witnesses, ratings, and paths. The crystal ball replaces the black box — and can hold black boxes up to the light: audit, comparison, multi-model consensus, certifiable clean-room exports.
- **The provenance void.** Every source enumerated, licensed, attributed; unlearning by source eviction (resolution path documented in OPEN-PROBLEMS §3).

## What this does not claim (yet)

Open-ended generative fluency at LLM parity, the completed text→tensor compile, frontier-scale empirics, and the modality annexes are the honest frontier — tracked in OPEN-PROBLEMS.md with candidate resolutions. The system's own epistemology applies to itself: claims earn rating through witnesses, and the reproduction scripts are the deposition kit.

## Receipts (measured, 2026-06-07, consumer desktop, no GPU)

- Engine: 289/289 unit tests including UAX#29/#15 conformance against UCD 17.0.0; byte-deterministic perfcache emit; byte-identical behavior across Linux/gcc and Windows/icx.
- Extensions: 8/8 pg_regress suites on stock PostgreSQL 18, byte-identical to Linux-generated expected outputs.
- Cold start: empty database → 1.11M placed Unicode atoms → languages → full WordNet → English Q&A with provenance in **4 minutes 54.86 seconds**.
- Ingestion: 13,300 adjudicated attestations/second sustained, consensus folding included.
- Query: 1.8–14.5 ms warm answers (definition/taxonomy class) with witnesses attached.
