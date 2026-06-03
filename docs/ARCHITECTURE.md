# Laplace — How the Invention Works

> **This document is the specification of how Laplace works** — the design-of-record, reconstructed
> from the inventor's direct corrections (the original `DESIGN.md` / `GLOSSARY` / ADRs were deleted).
> It is **authoritative**: it describes the invention, not a hypothesis under evaluation, and it carries
> **no build-status, "verified," or live-DB counts** — those rot the instant the code or DB changes,
> and progress belongs in the task list, never in the spec.

---

## 0. Reading this (binding)

- **Never pattern-match to conventional AI.** The reflexes — "embeddings give nearest neighbor,"
  "geometry = similarity," "store the model," "inference needs a GPU" — are the *wrong frame* and the
  source of every misread here. Every section is a deliberate inversion of conventional AI; hold the
  inversion, do not translate it back.
- **Understand the whole.** Every layer couples to the others — content = relation = trajectory, the
  signed consensus, the geometry, and the cognition layer are one mechanism; a local read yields a
  wrong global conclusion.

---

## 1. Thesis

Conventional AI locks knowledge in billions of opaque floats, re-learned redundantly inside every
model, executable only by dense matrix multiplication on a GPU, inspectable by no one. Laplace
inverts each property:

- **Black box → crystal ball.** Knowledge is explicit: content-addressed entities and trust-rated
  relations with full provenance. Every association is queryable, with *who attested it* and *how
  strongly*.
- **No GPU.** Inference, generation, audit, and interpretability are SQL queries — ranked graph
  traversal + geometric trajectory ops + `GROUP BY` aggregation over an indexed substrate (B-tree on
  rating, GIST + 4D-Hilbert on the S³ geometry) — not dense GEMM. See §2.
- **Audit any model.** A model is ingested as one *witness*; its internal associations sit in the
  same space as human knowledge and can be compared to multi-source consensus, and its components
  characterized by aggregation (§2).
- **Exact & deterministic.** Compensated f64, fixed reduction order, content-addressed identity —
  bit-reproducible and auditable, unlike stochastic training.

---

## 2. The payoff: inference, generation, AND interpretability are SQL queries

This is the point of the whole system. Once a model is ingested (§8), running, understanding, and
auditing it are all *querying* it:

- **Retrieve** — "everything whale-related the model knows," ranked, as a single indexed read
  (latency not yet benchmarked; `inspect` returns interactively):
  *(grounded in the real `consensus` table + its `consensus_out` / `completions` / `top_relations`
  SRFs — the consensus layer is what inference reads; `attestations` is per-witness evidence)*
  ```sql
  SELECT object_id, rating          -- consensus μ
  FROM   laplace.consensus
  WHERE  subject_id = :whale
  ORDER  BY rating DESC, rd ASC;     -- index: (rating DESC, rd)
  ```
  A B-tree scan, not a forward pass. `laplace inspect <text>` already does this today: resolve
  text → entity (engine TextDecomposer + HashComposer) → `entity_facets` + `entity_physicalities` +
  `consensus_out/in` (the ranked-μ consensus read) + `attestations_out/in` (the per-witness
  evidence audit).
- **Traverse / reason** — multi-hop is the same primitive composed (`WITH RECURSIVE` over highest-μ
  edges), optionally pruned by geometry (`laplace_frechet_4d`, `ST_Intersects`, `dwithin` on the
  trajectory/coord geometries with the GIST + Hilbert indexes).
- **Generate / reconstruct the output tree** — generation is querying the next highest-consensus
  continuation from the current context; composed, it reconstructs the model's *entire lexical output
  tree* without ever running it — "for this prompt, here are the responses TinyLlama could produce,"
  each branch ranked by consensus μ. No softmax over a 50k logit vector computed by GEMM; a ranked
  graph walk. This is generation and interpretability at once: you can *see* the model's possible
  outputs as a queryable tree, not just sample one path.
- **Interpret** — characterize the model's mechanics with plain `GROUP BY` over **evidence** (§6).
  Group the tokens an attention head relates and find what they share: if **layer-1, head-5**'s
  tokens all carry `is_a noun` consensus and little else, head-5 is a *noun-ness* detector. Iterated
  over every head / layer / projection, simple SQL tells you what each component does — mechanistic
  interpretability as queries, not activation-probing.

**Why this eliminates the GPU:** the expensive part of a transformer — attention (Q·K) and the
projections — is *precomputed once at ingest* into ranked, indexed, trust-rated edges and geometric
trajectories. At query time the "forward pass" is index lookups + a graph walk on a CPU. The network
becomes a queryable database; retrieval, inference, generation, audit, and interpretability are one
act: a SQL query.

### Each step has signals a forward pass never had

A conventional forward-pass step exposes essentially one signal: a blended attention distribution
(softmax over Q·K) plus dense activations — opaque, lossy, single-metric. At each step over the
substrate you have a *battery* of orthogonal, exact, indexed signals, each a cheap query:

- **Ranked-μ consensus neighbors** — relatedness, *with* an uncertainty (rd) and full provenance.
- **Tiered neighbors** — neighbors at every *compositional* tier of the UAX-29-style cascade
  (grapheme → word → sentence → document, and the modality-agnostic analogues), **and** per *kind* /
  *trust* tier (`is_a`-neighbors vs synonym- vs co-occurrence-, §7). A model attests at essentially
  *one* tier — wordforms with grapheme flags — whereas the substrate attests at *every* tier, so you
  can ask for neighbors at whatever granularity you want.
- **Fréchet (geometric) neighbors** — a *second, independent* notion of "close": structurally /
  analogically near trajectories (`laplace_frechet_4d`), distinct from edge-relatedness.
- **Intersection / usage** — `ST_Intersects` over trajectories answers "what else *uses* this?" —
  every composite or relation whose path runs through this entity.
- **Full mention pull** — because everything is content-addressed and composites embed their child
  ids, you can pull *everything that ever referenced this id* — its complete occurrence/provenance
  set.

So inference does not merely *replicate* the forward pass cheaply; each step can draw on many exact,
typed, auditable signals the original model never had and cannot expose. The substrate can
**supersede** the model it was seeded from, not just imitate it.

---

## 3. The one law: frame-invariance ("content is identity, source is witness," generalized)

The substrate is the **invariant** that remains when you quotient out the observer frame. The frame
is gauge; the content-addressed consensus is the invariant. This single law has four faces:

- **Omni-glottal** — invariant across language. All tongues bottom out at the Unicode codepoint
  floor (and DUCET collation is itself a multilingual ordering); a concept is the consensus of all
  languages that witness it, not an English token with translations bolted on.
- **Omni-modality / data-type-agnostic** — invariant across sense *and data type*. Everything is the
  same structure: atoms composing into higher tiers. Text uses the UAX-29 cascade (codepoint →
  grapheme → word → sentence → document); the *identical* machinery models pixels → patches → regions
  → image, samples → frames → track, moves → positions → game, bases → codons → genes → genome. A
  modality — or a chess game, or a DNA sequence — is a *region* of one substrate, not a separate
  embedding space stitched by contrastive training.
- **Omni-model** — invariant across model. Any model is a witness; the substrate is the consensus
  of all models + all human knowledge.
- **Omni-temporal** — invariant across time. Glicko-2 volatility / RD-decay weights recency; the
  substrate accumulates and forgets.
- **Omni-source** — invariant across the *kind* of source. A curated dataset (WordNet, ConceptNet,
  UD, Tatoeba, Wiktionary, OMW, ATOMIC, …) and a neural model are **the same thing to the substrate**:
  witnesses. This is *why* all those datasets are ingested — not as a model's training corpus, but as
  independent witnesses that (a) each cover a different *facet* of knowledge (taxonomy, commonsense,
  syntax, translation, etymology, multilingual, causal/social), (b) cross-corroborate into consensus,
  and (c) form the human-knowledge ground truth a model is then audited against. Because source-type
  is just a frame, **either alone suffices**: datasets-only or models-only each reconstruct the same
  *kind* of queryable substrate. Ingesting both is strictly better — more witnesses sharpen consensus,
  and symbolic and neural knowledge validate each other.

A *language*, a *modality*, a *model*, a *moment*, a *source kind*, a *user* are all **witnesses**,
never identities. Identity is content; everything else accumulates into trust. That is what makes the
substrate **actually universal** — the one representation all knowledge, from any source, in any
language or modality, converges into.

---

## 4. The core primitive: content = relation = a content-addressed trajectory

There is **one** kind of object: a content-addressed entity (BLAKE3-128 Merkle id) with a
**trajectory** geometry.

- **Atoms** (codepoints) are points on S³.
- **Composites** (graphemes → words → sentences → documents) are **trajectories** through their
  constituents' points: `physicalities.trajectory` (GeometryZM), where each vertex packs a
  constituent's entity_id; `id = merkle(tier, [child_ids])`; `coord = centroid(child coords)`;
  `radius_origin` = ‖coord‖ = the norm of the centroid of the children. **Atoms sit exactly on the
  sphere (radius 1.0); composites fall inside it (< 1.0)** — a composite's coord is the average of its
  constituents' points, which pulls toward the interior.
- **A relation is the same object.** `[King, Q_PROJ, Queen]` is a content-addressed entity whose
  trajectory runs subject → predicate → object — a higher-tier composite built by the *identical*
  cascade. "Edge" and "content" were never two categories: an edge is content whose constituents
  are `(subject, kind, object)`.

---

## 5. Geometry (the S³ "glome") — structural, and a relational-reasoning surface

- Every Unicode codepoint is placed on the unit 3-sphere by a **super-Fibonacci** spiral indexed by
  its **DUCET collation rank** (`engine/core/src/unicode_seed.cpp`, `super_fibonacci.c`). Coordinates
  are **structural, not semantic**: position encodes form/composition, not meaning. **Point-proximity
  is NOT relatedness** — two synonyms can sit far apart; two structurally-similar-but-unrelated
  strings can sit close.
- **Trajectory shape, however, is relational.** Spatial operators over trajectories relate A to B —
  a *different axis* from relatedness ranking:
  - `laplace_frechet_4d` / `ST_FrechetDistance` → **analogy**: `[King,Q_PROJ,Queen]` ∥
    `[Man,Q_PROJ,Woman]`; "king − man + woman = queen" becomes *curve similarity*, not vector
    arithmetic.
  - `ST_Intersects` → **shared constituent** (two relations through one entity; two words sharing a
    morpheme).
  - `ST_Overlaps` → **shared substructure.**
- The machinery exists and runs: PostGIS on the geometry columns + `laplace_geom`'s 4D
  Fréchet/Hausdorff (`06_st_4d.sql.in`; the functions are installed in the `public` schema).

A *content* trajectory is a real shape: a path walking across its constituents (each vertex = a
constituent entity-id, in sequence). A shared constituent produces an *identical* vertex (packing is a
pure function of the id), so `ST_Intersects` detects "shares a building block" and `laplace_frechet_4d`
over the trajectory measures **constituent-sequence similarity**. The id-vs-coordinate distinction is
not a limitation: the **perf-cache resolves any codepoint id → its S³ coordinate in O(1)**, and
composite ids → their `physicalities.coord` by index — so the *coordinate* trajectory is reconstructable
from the id trajectory on demand, and you can compare either the structural shape (ids) or the geometric
S³ shape (resolved coords). Storing the id is the compact lossless truth ("pack = truth, coord = the
recoverable view") — which is exactly what the perf-cache is for. Analogy *across relations*
(`[King,Q_PROJ,Queen] ∥ [Man,Q_PROJ,Woman]`) follows the same way once relations carry their
trajectory (§4).

---

## 6. Two layers: evidence vs. attestations (the consensus)

Evidence and consensus are two layers, not one.

- **Evidence** — the individual observations. Each is a single witness asserting a relation, with
  **full provenance**: which source, which model **layer / head / position**, what magnitude, when.
  (WordNet asserting `dog is_a noun`; Llama's layer-1-head-5 observing `King → Queen` at strength s.)
  Evidence is provenance-rich and powers interpretability (§2), auditing, and the embed species (§8).
  It is deduplicated / run-length-encoded — not stored naively N times — while **retaining** provenance.
- **Attestation = consensus.** An attestation is the **materialized** Glicko-2 consensus over all
  evidence for a given `(subject, kind, object[, context])` — **one row**, with source / layer / head
  **out** of the identity. This is what inference reads — directly, no joins.

### The nearest-neighbor replacement

- **Nearest-neighbor = ranked Glicko-2 μ on the consensus layer.** "What is most related to X" = X's
  consensus relations sorted by μ — an exact, single-table index scan. Conventional AI *derives*
  neighbors from an embedding via approximate vector search; Laplace precomputes and ranks. "The same
  thing, easier."
- **"Nearest-neighbor" is plural — several co-equal axes, not one.** Relatedness (ranked μ) is one
  axis. **Structural shape** is a second, fully independent one: Fréchet/Hausdorff over content
  *trajectories* answers "what is shaped like this" — compare *Moby Dick* to the *Bible* as whole
  trajectories; μ has nothing to do with it, and it cascades across tiers (document, sentence,
  word-grapheme shape). **Proximity / containment** (`dwithin`, `ST_Intersects`) is a third. All are
  modality-blind and therefore **cross-modal** — a pixel building-block is content like any wordform:
  it can be a prompt, Fréchet-compared to a text trajectory, or related by μ (prompt with a patch,
  traverse into text; prompt with text, traverse into pixels; an image model ingests the same way).
  Pick the axis — or combine them — for the question; never crown one as "the" mechanism.
- **Decoupling.** A conventional embedding fuses identity/position and similarity into one vector
  (proximity = meaning). Laplace splits them: **structure → S³ geometry, relatedness → consensus μ.**
  Using geometric proximity *as* relatedness re-fuses what the design separates — the conventional-AI
  error.
- Consensus accumulates from **heterogeneous** witnesses — neural (Q/K/V/O, gate, up/down) **and**
  symbolic (`is_a`, `has_a`, `derived_from`, `acronym_of`, …) converging on the same relation — and is
  **materialized** from the evidence (one row per relation) so inference reads it directly, not by joins.

### Signed consensus — confirm = win, refute / repel = loss, dissent folded in

Consensus μ is **signed** (native Glicko-2 win/loss against a neutral baseline), not a positive-only
accumulator:

- A witness that **confirms / attracts** a relation is a **win** → μ up.
- A witness that **refutes / repels** it (a model's negative QK score; a source asserting it does not
  hold; the Gödel Engine proving it false, §11) is a **loss** → μ down.
- μ ≫ neutral = confirmed; μ ≈ neutral with high **volatility** = contested / unknown; μ ≪ neutral =
  refuted. `ORDER BY μ DESC` ranks confirmed at the top, refuted at the bottom. The sign *is* the
  truth-state — QK attends/repels, is/is-not, confirmed/refuted all collapse into one signed μ.

**Dissent is folded in and weighted — never discarded.** A minority / repelling witness is not
steamrolled by the majority: it casts its weighted loss, so μ settles at the **balance of all
witnesses** and **volatility rises** to record the disagreement — "some sources disagree," not "ignore
the dissenters." The evidence layer retains *who* dissented (which model / layer / head repelled, with
provenance), so the minority view stays visible and auditable, and high-volatility relations are
exactly what the Gödel Engine (§11) queues to investigate. The engine's own refutation is **one more
weighted vote** into that balance (heavy — high-trust, carries a proof), not an erasure of the crowd.

**The matchup (DERIVED — it is Glicko-2 with two mappings, not a new system).** The relation is the
player; each witness observation is a match against a neutral baseline opponent (μ₀ = 0, the 1500
line).

1. **Outcome** `s_j = ½(1 + tanh(m_j / M)) ∈ (0,1)`, from the witness's **signed** strength `m_j`:
   `+|q·k|` attracts → `s→1` (win); `−|q·k|` or a categorical refutation → `s→0` (loss); neutral → ½;
   a categorical confirm (`is_a`) is `m_j→+∞ ⇒ s=1`. `M` = the per-arena magnitude scale, measured
   from the arena's own magnitude distribution at ingest (stored in `arena_m` for audit), never
   hand-set.
2. **Weight = opponent precision, NOT a bolted-on multiplier.** The witness weight
   `w_j = kind_rank × source_trust × tenant_trust ∈ (0,1]` maps to the opponent's rating deviation
   `φ_j` (high trust → low φ → `g(φ_j)≈1`; crank → high φ → `g(φ_j)≈0`). Glicko's own
   `g(φ)=1/√(1+3φ²/π²)` then weights the update sums `Σ_j g(φ_j)(s_j−E_j)` and `v⁻¹=Σ_j g(φ_j)²E_j(1−E_j)`.
   The rating-deviation machinery already in `glicko2.c` **is** the trust weighting — no new term.

Then it is stock Glicko-2 (the paper-pinned kernel). The headline behaviours are **native, not tuned**:
- **One high-trust proof out-votes N cranks** — cranks have `g≈0`, so their terms vanish from both
  sums; the proof (`g≈1, s=0`) dominates. (Not a multiplier I pick; the trust→φ→g map does it.)
- **Saturation** — confirming an already-high-μ relation gives `s−E≈0` (tiny move); refuting it gives
  `s−E≈−1` (big drop). Established truths are stable; refuting one bites hard.
- **Dissent → volatility** — mixed wins/losses pull μ to the weighted balance and raise σ (contested),
  recorded not discarded.
- **Incremental / sublinear** — each model ingest is one rating period folded in via
  `laplace_glicko2_accumulate`; a re-witness is another (saturating) match, a novel relation is a new
  player.

The **only calibration** (empirical, tune-once — NOT design choices): `M`, the
trust→φ map shape (e.g. `φ_j = φ_min + (φ_max−φ_min)(1−w_j)`), and φ₀. A per-"arena"/type difference is
one line: what `m_j` it emits and its `kind_rank`; the framework is uniform.

Passive witnessing is positive-accumulating — a falsehood with few witnesses (flat earth) stays feeble
by sparsity, no refutation needed — but active refutation and signed sources make μ genuinely signed: a
contradiction lowers μ **as a Glicko loss, not a subtraction**.

---

## 7. Weighting — not all witnesses are equal

An observation's influence on a relation's consensus is a product of three ranks:

1. **Kind rank** — kinds carry epistemic significance/importance: `is_a` ≫ `acronym_of`, and
   synonym ≠ hyponym ≠ meronym ≠ antonym (equivalence vs hierarchical vs partitive vs oppositional
   are *different* weights, some same-direction, some opposing). This is a **real significance
   ranking, NOT the `KindValueTier` T1–T11 axis** — those tiers are a coarse placeholder; the genuine
   kind-significance scale that feeds the matchup weight is left to set (§10). The many trust /
   significance levels exist precisely so each (kind, source) weights its matchup differently.
2. **Source trust** — each source has a trust rating. Maps to `TrustClass` TC1–TC10
   (SubstrateMandate 1.00 … AiModelProbe 0.50 … Adversarial 0.00; `AttestationFactory.cs:167-180`).
3. **User / tenant trust** — the user/tenant ingesting content has a trust rating.
   **NOT YET IMPLEMENTED — there is no tenant/user id.** This dimension must be added and folded
   into the consensus weight alongside kind rank and source trust.

Effective seed/accumulation weight ≈ f(kind rank, source trust, tenant trust). Today
`AttestationFactory.CreateWeighted` computes `μ = kindTierμ × strength × sourceTrustWeight`; the
tenant dimension is absent.

---

## 8. Models are seeds, ingested as token×token bilinear circuits (NOT a codec)

A model is a **witness/seed**, not stored content, and there is **no codec — no encode/decode, no
round-trip**. "codec"/"roundtrip" is the conventional-AI frame and is banned for model ingestion,
query, and export. A model is *decomposed* into the substrate (witness → consensus), the substrate is
*queried*, and a model of any chosen shape is *synthesized* from consensus. The input model is not
kept as itself; it dissolves into the deduplicated union.

**Characterize a model by the math it performs, never by a label it never advertises.** Each
tensor/layer runs a fixed, finite set of operations, and that set is **modality-blind** — the same
matmuls / softmax / norm whether the tokens are text, pixels, audio, DNA, or chess moves; modality
enters only at the input embedding and the output unembedding. A tensor *is* the operation it
performs, and that operation, read back through the embedding, *is* a relationship between content.

### Every interior tensor is a token×token bilinear circuit through the embedding

The weights encode relationships between content (tokens), recovered by reading each tensor's fixed
bilinear form back into token space via the embedding `E` (and unembedding `E_U`). This is the
QK/OV-circuit + FFN-as-key-value-memory decomposition:

- **QK circuit** (Wq, Wk): `score(i,j) = E·Wq·Wkᵀ·Eᵀ` — token *i* attends to token *j*. A token×token
  relation (pre-softmax compatibility).
- **OV circuit** (Wv, Wo): `E·Wv·Wo·E_Uᵀ` — attending to token *i* shifts the prediction toward token
  *j*. **One** token×token relation from V and O together — not two per-token scalars.
- **FFN memory** (up/gate = keys, down = values): `(E·Wup)·(E_U·Wdown)ᵀ` — an input token fires the
  neurons that write toward an output token.
- **embed_tokens / lm_head** = `E` and `E_U` themselves → **Projection physicalities** (per-token
  *placements*), not relations; they are the sandwich the bilinears are read through.
- **norms** → recipe scaling.

**Nonlinearities are runtime, never attested.** Softmax, SiLU/GELU, and the SwiGLU gate (`gate ⊙ up`)
are *data-dependent* — they depend on the actual input at inference — so ingest records only the
**static** bilinear structure each weight imposes; the gating/normalization is applied at
query/generation time. Collapsing a tensor to a **per-token magnitude scalar** (the prior code)
destroys the relation by reducing the dim axis to one number — that is the thing this replaces, and
it is non-negotiable: never magnitude.

### A model says nothing the seed corpora can't

Because every tensor is a token×token relationship, a model's attestations are the **same kind of
object** as a corpus's relations: `[dog, contextually-relates, bark]` recovered from a model's QK
circuit and the same edge from a text corpus are *one consensus relation, different witnesses*. So
**datasets-only, models-only, or both** reconstruct the same substrate (§3). A model adds witness
strength, statistical coverage, and per-(layer,head) provenance — not a new *kind* of thing. This is
`HAS_POS`≡`HAS_UPOS` at the tensor level.

### Physical extraction — the embed species

"King" has one content identity but a **species** of embeds: King-from-Llama, King-from-Qwen, … each a
distinct **Projection** physicality (kind 3/4), source-tagged. Voronoi cells over the species are the
geometric/distributional consensus. Content physicality (kind 1) = singular, structural; Projection
(kind 3/4) = plural, per-source.

### Re-export = fill the mold = the ingestion run backward

Synthesis is the **exact inverse** of ingestion, computed at runtime from consensus: take a target
recipe (any dim / layers / routing / rope / lora — the *mold*) and **factor each consensus circuit
back into that mold's weight tensors at the recipe's rank**, via SVD (Eigen/oneMKL) through the
spectral token basis — QK→q/k, OV→v/o, FFN→up/down. Whatever the forward pass computed token×token
*from* the weights, export reconstructs weights *from* the consensus token×token. The output is the
**consensus of all ingested models** in the chosen shape — never a reconstruction of any one, never
bit-perfect. Fill the mold.

### Ingestion is sublinear in model count (dedup + consensus)

The *first* model balloons the substrate — almost everything it asserts is novel. Each subsequent
model (even a far larger one) mostly **re-witnesses relations that already exist**: content
deduplicates and relations converge to consensus, so the marginal cost is its *novel* relations plus
witness updates (`glicko2_accumulate`) to existing consensus — not a fresh copy. Stronger still: even
the *first* model mostly re-witnesses relations the **seed corpora** already established. The
substrate is the deduplicated *union* of all witnesses; adding models sharpens consensus rather than
multiplying storage.

---

## 8a. The decomposer contract — how any source becomes substrate

A decomposer turns a source (a corpus, a standard, a model) into **content-addressed entities +
attestations** — witnesses, never identities (§3). One contract binds every decomposer, seed corpus
and model alike:

- **Content-address everything.** Identity is `merkle(tier, [child_ids])` over the content, computed
  identically for every source, so the *same* word / sentence / relation from two sources is the
  *same entity* — convergence is automatic at the identity layer.
- **Bind at the natural tier.** A lone word attests on its word entity, not a wrapping document
  (content used to bind to the wrapping document instead of the word; now fixed).
- **Normalize source kinds into Laplace's internal modalities.** Corpora are *seeds*, never
  bit-perfectly preserved. `HAS_POS` (WordNet) and `HAS_UPOS` (UD) mean the same thing → one internal
  part-of-speech kind; `HAS_DEPREL` and friends likewise map to internal kinds; a model's QK/OV/FFN
  circuits map into the same relatedness modalities the corpora populate. Antipodal/opposite kinds
  stay distinct. This is what makes witnesses **co-assert** — without it, every relation is a
  singleton and there is no consensus.
- **Resolve reference data through the substrate's own seeded reference, AT INGEST — never hardcode,
  never join at runtime.** Omni-glottal example: any language reference — any ISO 639 code form
  (`en` / `eng` / 639-2B / 639-2T), any BCP-47 tag (`en-US`), any name in any language (`English`,
  `français`) — resolves to the **one** canonical 639-3 language entity through an app-side resolution
  index built from the *attested* ISO + Unicode reference (the "perf-cache" principle: load the
  reference once, resolve at ingest). Entries **unify at write time**, so inference reads need **no
  runtime joins**. *That is why ISO and Unicode are seeded* — they are the reference layers the
  substrate normalizes itself against, not loose files to look up. (`LanguageReference` does this for
  languages today: code-precedence over names, 639-1/2B/2T → 639-3, retirements + IANA Preferred-Value;
  the codepoint→script→language→family graph is the next layer.)
- **app vs AI separation.** The resolution index, the perf-cache, code/normalization tables are
  *internal app functionality* (non-attested plumbing). The consensus substrate is the *AI* (attested
  knowledge). Never conflate them — a runtime join to resolve `en`→`eng` is the app failing to unify
  at ingest, not the AI.
- **Incremental + idempotent — never nuke-to-re-ingest.** The writer is content-addressed
  (`ON CONFLICT DO NOTHING`), so re-ingesting identical content is a **no-op** and cannot double-count.
  Seed the reference layers **once** into a persistent base; layer each model/source on top
  incrementally; to re-run a single source after a code change, **evict only that source's evidence**
  (`DELETE … WHERE source_id = X`), never reset the whole DB. No resume journal exists — re-ingestion
  is idempotent by content-addressing alone. Consensus for the touched relations re-accumulates at
  the next ingest period (watermark-windowed), never by a batch pass. This is what makes ingestion
  sublinear (§8).
- **Exact, on the perf stack.** Compensated f64, fixed reduction order, MKL/TBB/AVX2 + Eigen/Spectra —
  never a managed scalar GEMM, never top-k truncation, never an approximation.

---

## 9. The data model

- **`laplace.entities`** — identity only. `id` (BLAKE3-128 PK), `tier`, `type_id`,
  `first_observed_by`, `created_at`. No geometry — geometry lives in `physicalities`.
- **`laplace.physicalities`** — geometry. `id`, `entity_id`, `source_id`, `kind`
  (1=Content, 2=BuildingBlock, 3=Projection, 4=ProjectionOutput), `coord` + `trajectory` (PostGIS
  PointZM / GeometryZM via CHECK), `hilbert_index`, `radius_origin` (generated), `n_constituents`,
  `alignment_residual`, `source_dim`, `observed_at`. `UNIQUE(entity_id, source_id, kind)`. Content
  physicality (kind 1) is singular/structural; Projection (kind 3/4) is the per-model embed species
  (§8).
- **`laplace.attestations`** — the per-witness **evidence** layer: one Glicko-2 OBSERVATION per
  witness, never accumulated state. `id` (BLAKE3 of the 5-tuple), `subject_id`, `kind_id`,
  `object_id?`, `source_id`, `context_id?`, `score` (the ½(1+tanh(m/M)) outcome), `opponent_rd`
  (witness weight → opponent φ), `arena_m` (the per-arena M actually used — audit),
  `last_observed_at`, `observation_count` (int64 fixed-point ×1e9).
  `UNIQUE NULLS NOT DISTINCT (subject,kind,object,source,context)` — **source IN the identity**, full
  provenance retained (model layer/head in `context_id`). The accumulated `rating`/`rd`/`volatility`
  live ONLY on `consensus`.
- **`laplace.consensus`** — the materialized **consensus** layer (§6): one signed Glicko-2 row per
  `(subject, kind, object)`, **source AND context (model layer/head) OUT of identity** — both are
  witnesses, never identity. `id` (BLAKE3 of the 3-tuple), `rating`/`rd`/`volatility`,
  `witness_count`, `last_observed_at`; `UNIQUE NULLS NOT DISTINCT (subject,kind,object)`.
  Accumulated **at ingest** via the `laplace_glicko2_accumulate` aggregate (`incremental_consensus`
  per ingest period; `materialize_period_consensus` for the production writer) — there is **no batch
  rebuild**; that pattern is forbidden by design. **Inference reads this** — a sorted index scan on
  `rating`, not joins over evidence.
- **Glicko-2 kernel** — full Glickman-2013 implementation in `engine/core/src/glicko2.c`, exposed as
  the `laplace_glicko2_accumulate` SQL aggregate; computes the signed win/loss consensus update of §6.
- **Geometry** — `laplace_geom` (in the `public` schema): `laplace_distance_4d`, `laplace_dwithin_4d`,
  `laplace_frechet_4d`, `laplace_hausdorff_4d`, `laplace_centroid_4d`, `laplace_radius_origin`, plus
  PostGIS `ST_Intersects` / `ST_Overlaps` / `ST_VoronoiPolygons` on the geometry columns.
- **Perf-cache** — `CodepointPerfcache`: a memory-resident map of all 1,114,112 codepoints → (S³
  `coord`, hilbert, hash), shared by the engine + PG extension. Resolves leaf ids → coordinates in
  O(1) — the leaf resolver for the trajectory cascade (§5), the same app-side resolution-index
  principle the language reference uses (§8a).
- **Query entry point** — `laplace inspect <text>` resolves text → entity (engine TextDecomposer +
  HashComposer) and reads its facets via the `entity_facets` / `entity_physicalities` /
  `attestations_out` / `attestations_in` SRFs.

---

## 10. Calibration & open parameters

Values the spec leaves to **set** (calibration constants — tune-once) and a few
**design choices**. These are knobs, not gaps in how the invention works:

- **Consensus calibration (§6).** The magnitude scale `M` (per arena), the trust→φ map shape (e.g.
  `φ_j = φ_min + (φ_max−φ_min)(1−w_j)`), and the baseline φ₀. The matchup math itself is fixed (§6).
- **Tenant/user trust (§7).** The third trust factor — its id and how it combines with kind-rank ×
  source-trust — set when the app/auth layer lands.
- **Evidence-layer provenance retention.** How per-(layer,head) provenance is carried on the evidence
  layer — a provenance array, a repurposed `context_id`, or a side table — reconciled with
  run-length dedup of re-observations.
- **Voronoi query semantics (§8).** What a query does with an embed-species cell: nearest-cell
  assignment, consensus centroid, or cell volume as cross-model disagreement.
- **FFN rank strategy (§8).** The FFN circuit's dot dimension is the intermediate width (≫ head_dim),
  which does not fit the 64-dim eigenmaps basis — both FFN ingest scoring and FFN export need a rank
  choice.
- **Generation termination (§2).** The stop rule for the ranked-μ walk (EOS as a relation, a μ
  threshold, a depth bound) and the distribution it reproduces.
- **The Gödel Engine's policy (§11).** Refutation-weight scaling, task generators, the
  self-improvement rule — set when the cognition layer is built.
- **Non-text modalities (§3).** The pixel/audio/DNA/chess decomposers — the same cascade, applied to
  each modality's atoms.

---

## 11. The Gödel Engine — the cognition layer (how this becomes AGI)

The substrate (§1–§9) is **knowledge**: content, relations, signed consensus. The **Gödel Engine** is
the layer above it that **thinks** — it queues and queries tasks, researches, infers, proves and
refutes, and acts. The substrate is its memory; the engine is its cognition. It is what turns a
queryable knowledge base into a reasoning agent.

- **It needs genuine `false`.** Reasoning is not just believing the weighted crowd — it forms
  hypotheses, tests them, and **refutes** the wrong ones. "this relation is false" is a first-class
  act: the engine casts a weighted **loss** (§6 signed consensus) that drives the relation's μ
  negative, carrying its derivation / proof as provenance. Without `false` the engine cannot prune,
  cannot conclude, and re-explores dead ends forever. (This is *why* μ must be signed — §6.)
- **It runs on the same substrate — self-reference, the "Gödel."** A relation is content (§4), so a
  verdict is a relation *about* a relation; tasks, hypotheses, and the engine's own reasoning state are
  *also* content. The engine reasons over a substrate that **includes itself** — it attests verdicts on
  its own conclusions and can improve by reasoning about its own relations.
- **The thinking loop, driven by the signed consensus:**
  - **Prune** — skip relations whose μ is refuted (negative).
  - **Detect contradiction** — a relation pulled hard both ways (near-neutral μ + high volatility, or
    both a proven-true and a proven-false verdict) → queue a **resolve** task.
  - **Research the gaps** — `unknown` / unprovable relations are where it queues **research** tasks;
    incompleteness drives curiosity.
  - **Gate inference / generation** — queries respect verdicts: even a high-witness-μ relation is
    skipped / down-ranked once the engine has proven it false (proof beats popularity by **out-voting it
    with weight**, §6 — never by erasing the crowd; dissent stays recorded).

It sits above the knowledge substrate and is the layer that makes Laplace a reasoning agent rather than
a queryable knowledge base; its policy knobs (refutation-weight scaling, task generators, the
self-improvement rule) are in §10.
