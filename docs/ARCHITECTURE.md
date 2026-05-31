# Laplace — How the Invention Works

> **Status of this document.** Reconstructed from the inventor's direct corrections plus
> verification against the live substrate (DB queries + `laplace inspect` + code reads),
> May 2026. The original `DESIGN.md` / `GLOSSARY` / ADRs that the code comments reference
> were deleted and are not in the repo; **this document is the design-of-record now.** It
> deliberately separates **INTENDED** architecture (how it must work)
> from **CURRENT** state (what the code/DB actually do today, each tagged *verified* when it
> was confirmed by query, not by reading code).
>
> **Polarity — read this.** INTENDED is the **inventor's specification**: authoritative, not a
> hypothesis under evaluation. CURRENT is *this implementation's* progress toward that spec.
> "Verify" here means "does the code match the spec," **never** "does the design work." The §9 items
> are unbuilt implementation work, not doubts about the invention — built to spec, it produces the
> working model.

---

## 0. Methodology (binding — learned the hard way in this repo)

- **Trust but verify against the live system.** Reading code gives you *intent*, not reality.
  "Verified" means: queried the live DB (`psql`), ran `laplace inspect <text>`, or executed the
  path and read the rows. An audit that says "I read the source, did not query the DB" is **not**
  verification. (One `inspect cat` overturned a 17-agent code-reading audit — see §9.) The converse
  also holds: live-DB statistics reflect what is *currently recorded*, defects included — e.g. the
  §9.3 mis-tiering corrupts the `tier` column — so a number "verified from the DB" describes the
  present (buggy) state, not necessarily the intended design. Read structural statistics with the
  active defects in mind.
- **Never pattern-match to conventional AI.** The reflexes — "embeddings give nearest neighbor,"
  "geometry = similarity," "store the model," "inference needs a GPU" — are the *wrong frame* and
  have been the source of every misread here. Everything below is a deliberate inversion of
  conventional AI; hold the inversion, do not translate it back.
- **Understand the whole.** Every layer couples to the others; a local read yields a wrong global
  conclusion. The defects in §9 are not separate bugs — they are one missing primitive.

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
  *(illustrative; grounded in the real `attestations` table + the `attestations_out` SRF that
  `inspect` uses)*
  ```sql
  SELECT object_id, rating          -- consensus μ
  FROM   laplace.attestations
  WHERE  subject_id = :whale
  ORDER  BY rating DESC, rd ASC;     -- index: (rating DESC, rd)
  ```
  A B-tree scan, not a forward pass. `laplace inspect <text>` (`app/Laplace.Cli/Program.cs:160-261`)
  already does this today: resolve text → entity → `entity_facets` + `entity_physicalities` +
  `attestations_out/in ORDER BY rating DESC`.
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
**supersede** the model it was seeded from, not just imitate it. (CURRENT: the query primitives
exist; the richest depend on the missing primitive — materialized consensus, typed-tier NN over it,
and relation trajectories — being built; see §6, §9.)

**CURRENT (verified):** the kernel — ranked-μ neighborhood of a concept — **works** (`inspect`,
returns immediately). The geometric predicates exist as SQL functions. NOT wired: multi-hop
traversal / generation (the cascade/A\* SRF `07_cascade.sql.in` + `engine/core/src/astar.c` are
commented-out stubs); and the `GROUP BY` interpretability is blocked because the evidence layer
currently **drops** per-(layer,head) provenance (§6). The primitives are real; the compositions and
the provenance they need are not yet built.

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
  embedding space stitched by contrastive training. (CURRENT: verified for text — tiers 0–4 are live;
  the non-text decomposers are stubs and chess/DNA/etc. are intended, so this is demonstrated for text
  and designed for the rest.)
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
  `radius_origin` = ‖coord‖ = the norm of the centroid of the children. **Caveat on the live numbers:**
  the `tier` column is corrupted by the mis-tiering defect (§9.3 — single words are recorded as tier-4
  *documents*), so any "radius by tier" statistic measures the *current buggy recording*, not the
  intended hierarchy. As recorded (mean [5th–95th pct]): T0 = 1.000 (atoms, exact — codepoints are
  correctly tier-0), T1 = 0.86 [0.24–1.00], T2 = 0.40 [0.13–0.74], T3 = 0.49 [0.15–0.73], T4 = 0.49
  [0.15–0.73]. The only reading that survives the defect: **atoms sit exactly on the sphere (1.0);
  composites fall inside (<1.0).** That tiers 2–4 look nearly identical is itself an artifact of the
  mis-tiering — per-tier radius is not a meaningful measurement until tiering is fixed.
- **A relation is the same object.** `[King, Q_PROJ, Queen]` is a content-addressed entity whose
  trajectory runs subject → predicate → object — a higher-tier composite built by the *identical*
  cascade. "Edge" and "content" were never two categories: an edge is content whose constituents
  are `(subject, kind, object)`.

**CURRENT (verified):** content trajectories are recorded (kind=1 physicalities). Relations are
**not** yet trajectory entities — they are rows in `attestations` with no geometry column. This is
part of the central divergence (§9).

---

## 5. Geometry (the S³ "glome") — structural, and a relational-reasoning surface

- Every Unicode codepoint is placed on the unit 3-sphere by a **super-Fibonacci** spiral indexed by
  its **DUCET collation rank** (`engine/core/src/unicode_seed.cpp`, `super_fibonacci.c`). Coordinates
  are **structural, not semantic**: position encodes form/composition, not meaning. **Point-proximity
  is NOT relatedness** — two synonyms can sit far apart; two structurally-similar-but-unrelated
  strings can sit close. (verified: live coords; `coordinate-cascade` memory.)
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

**CURRENT (verified) — operative on content; the only gap is relations.** A *content* trajectory IS a
real shape: a path walking across its constituents (each vertex = a constituent entity-id, in
sequence). A shared constituent produces an *identical* vertex (packing is a pure function of the id),
so `ST_Intersects` detects "shares a building block" and `laplace_frechet_4d` over the stored
trajectory measures **constituent-sequence similarity** — both run live today. The id-vs-coordinate
distinction is **not** a limitation: the **perf-cache resolves any codepoint id → its S³ coordinate in
O(1) (microseconds)**, and composite ids → their `physicalities.coord` by index — so the *coordinate*
trajectory is reconstructable from the id trajectory on demand, and you can compare either the
structural shape (ids) or the geometric S³ shape (resolved coords). Storing the id is the compact
lossless truth ("pack = truth, coord = the recoverable view") — which is exactly what the perf-cache
is for. The only genuine gap: **relations have no trajectory yet** (§4), so analogy *across relations*
(`[King,Q_PROJ,Queen] ∥ [Man,Q_PROJ,Woman]`) is INTENDED until relations are carried as trajectory
entities.

---

## 6. Two layers: evidence vs. attestations (the consensus)

This distinction is foundational, and the current code conflates it.

- **Evidence** — the individual observations. Each is a single witness asserting a relation, with
  **full provenance**: which source, which model **layer / head / position**, what magnitude, when.
  (WordNet asserting `dog is_a noun`; Llama's layer-1-head-5 observing `King → Queen` at strength s.)
  Evidence is provenance-rich and powers interpretability (§2), auditing, and the embed species (§8).
  *Intended:* it is deduplicated / run-length-encoded — not stored naively N times — while
  **retaining** provenance. (CURRENT: no such evidence layer runs — re-observations are discarded by
  `ON CONFLICT DO NOTHING` and `observation_count` never increments.)
- **Attestation = consensus.** An attestation is the **materialized** Glicko-2 consensus over all
  evidence for a given `(subject, kind, object[, context])` — **one row**, with source / layer / head
  **out** of the identity. This is what inference reads — directly, no joins.

### The nearest-neighbor replacement

- **Nearest-neighbor = ranked Glicko-2 μ on the consensus layer.** "What is most related to X" = X's
  consensus relations sorted by μ — an exact, single-table index scan. Conventional AI *derives*
  neighbors from an embedding via approximate vector search; Laplace precomputes and ranks. "The same
  thing, easier."
- **Decoupling.** A conventional embedding fuses identity/position and similarity into one vector
  (proximity = meaning). Laplace splits them: **structure → S³ geometry, relatedness → consensus μ.**
  Using geometric proximity *as* relatedness re-fuses what the design separates — the conventional-AI
  error.
- *Intended:* consensus accumulates from **heterogeneous** witnesses — neural (Q/K/V/O, gate,
  up/down) **and** symbolic (`is_a`, `has_a`, `derived_from`, `acronym_of`, …) converging on the same
  relation — and is **materialized** from the evidence (one attestation per relation) so it reads
  directly, not by joins. (Today neither the accumulation nor the materialization runs — see CURRENT.)

**CURRENT (verified) — the conflation:** the `attestations` table today holds per-witness
**evidence**, not consensus — `source_id` is *in* the identity (`AttestationFactory.cs:34`,
`UNIQUE(subject,kind,object,source,context)`), `obs=1` everywhere, and `glicko2_accumulate` (which
would produce the consensus) is **never called**. So there is **no materialized consensus** — it is
reachable only by excessive query-time joins. The evidence is also lossy for the model path: per-(layer,head)
provenance is dropped (Q_PROJECTS `context_id=NULL`, first-witness-wins) — exactly the provenance
§2's interpretability needs. (Substrate-wide, context_id is NULL for ~89% of attestations [45.4M of
51.0M]; ~11% do set it, e.g. UD's deprel — so provenance is dropped for *most* witnesses, not all.) Fix: retain evidence *with* layer/head provenance, and materialize the consensus attestation
via `glicko2_accumulate`.

---

## 7. Weighting — not all witnesses are equal

An observation's influence on a relation's consensus is a product of three ranks:

1. **Kind rank** — kinds carry epistemic superiority: `is_a` ≫ `acronym_of`. Maps to
   `KindValueTier` T1–T11 (`AttestationFactory.cs:150-164,184-197`).
2. **Source trust** — each source has a trust rating. Maps to `TrustClass` TC1–TC10
   (SubstrateMandate 1.00 … AiModelProbe 0.50 … Adversarial 0.00; `AttestationFactory.cs:167-180`).
3. **User / tenant trust** — the user/tenant ingesting content has a trust rating.
   **NOT YET IMPLEMENTED — there is no tenant/user id.** This dimension must be added and folded
   into the consensus weight alongside kind rank and source trust.

Effective seed/accumulation weight ≈ f(kind rank, source trust, tenant trust). Today
`AttestationFactory.CreateWeighted` computes `μ = kindTierμ × strength × sourceTrustWeight`; the
tenant dimension is absent.

---

## 8. Models are seeds, not artifacts

- A model is a **seed datasource**, not normal user content, and is **not stored bit-perfectly**.
  You harvest two extractions and discard the bytes; *non-invertibility is by design, not a defect.*
  - **Semantic extraction → relation evidence** (the model's Q/K/V/O, gate, up/down as witnesses to
    King↔Queen-type relations, accumulating into the §6 consensus).
  - **Physical extraction → embeddings as per-model physicality points.** "King" has **one** content
    identity but a **species** of embeds: King-from-Llama, King-from-Qwen, King-from-DeepSeek — each a
    distinct **Projection** physicality (kind 3/4), source-tagged, embed-typed. **Voronoi cells** over
    the species (stored in physicality records) are the geometric/distributional consensus — the
    "additional semantic value."
  - Content physicality (kind 1) = singular, source-independent, structural. Projection physicality
    (kind 3/4) = plural, per-source, the model embeddings.
- **Re-export = fill the mold.** Take a target architecture's recipe (its *mold* — shapes/layout) and
  pour the substrate's consensus (consensus relations + embed-species/Voronoi) into its slots. The
  output is the **consensus of all ingested models** in the target shape — not a reconstruction of
  any one of them.
- **Ingestion is sublinear in model count (dedup + consensus).** The *first* model balloons the
  substrate — almost everything it asserts is novel. Each subsequent model (even a far larger one like
  Llama-4 Maverick) mostly **re-witnesses relations that already exist**: content deduplicates and
  relations converge to consensus, so the marginal cost is its *novel* relations plus witness updates
  (`glicko2_accumulate`) to existing consensus — not a fresh copy. The substrate is the deduplicated
  *union* of all models' knowledge; adding models sharpens consensus rather than multiplying storage.

**CURRENT (verified):** the Model decomposer writes **no** physicalities (`physicalityCapacity:0`)
and collapses each embedding to an L2-magnitude scalar seeded into a Glicko μ on a *unary*
(node-attached, `object_id=NULL`) attestation — not the embedding vector as a point. (Only Q_PROJECTS
is a true binary edge; EMBEDS / V/O/G/U/D / OUTPUT_PROJECTS are unary.) DB has only kind=1 physicalities; **kind 3/4 = zero.** So the
embed-species / Voronoi layer that feeds mold-filling does not exist, and synthesis can only
broadcast magnitude texture (the "Stream B-minimum" stub). The right fix is to record embeddings as
Projection physicality points, not to "make synthesis reconstruct the model."

---

## 9. Current state — one missing primitive, many symptoms (verified against the live DB)

Every defect below traces to **one** primitive not being built: *relations recorded as
content-addressed trajectory entities, with a two-layer evidence/consensus model — evidence retaining
full provenance, consensus materialized via Glicko-2 — and their geometry exposed.*

1. **Evidence and consensus are conflated** — the `attestations` table holds per-witness *evidence*
   (`source_id` in the identity, `obs=1`), but there is **no materialized *consensus* attestation**;
   consensus is reachable only by excessive query-time joins. Fix: two layers (§6).
2. **`glicko2_accumulate` is never called** — it is implemented + pg_regress-pinned; the writer does
   `ON CONFLICT DO NOTHING`, so no consensus is ever produced. Per-(layer,head) provenance is
   dropped for the model path (Q_PROJECTS `context_id=NULL`; ~89% of all attestations have NULL
   context, ~11% use it), which blocks the `GROUP BY` interpretability (§2).
3. **Attestations attach to tier-4 document roots, not the right-tier entity** — *verified:* a bare
   word "cat" resolves to a **tier-4 document** (9 nodes: 3 codepoints + 3 graphemes + 1 word + 1
   sentence + 1 document), and **~96%** of sampled attestation subjects are tier 4. Lexical facts
   land on document blobs, not word entities — the ~3.41M tier-2 *word* entities exist as DAG
   children, but are not what content-addressing returns or what attestations bind to. (Root cause:
   `text_decomposer.c:286` builds the tier-4 document root unconditionally; `ContentEmitter` returns
   that root.)
4. **Relations have no geometry** — the `attestations` table has no geometry column; relations are
   not trajectory entities (§4).
5. **Only Content physicalities exist** — *verified DB:* kind=1 = 20,957,504; **kind 2/3/4 = 0.**
   The embed-species / physical-embedding layer is absent (§8).
6. **Model embeddings collapse to magnitude scalars** — no embedding points → no species → no
   Voronoi (§8).
7. **Synthesis is a magnitude-broadcast stub** ("Stream B-minimum") — tiles a ≤64-dim spectral basis
   across recipe slots; cannot fill the mold because the structured material (consensus relations +
   embed points) was never recorded.
8. **Convergence breakers** — language entities split `language:en` (ISO 639-1, from
   UD/Wiktionary/ConceptNet) vs `language:eng` (ISO 639-3, from ISO/OMW/Tatoeba): the shared
   `LanguageEntityId.FromIso639_3` helper does **no** conversion — it only lowercases + prefixes
   `language:` — so 2- vs 3-letter inputs fracture *despite the common helper*. And the same assertion
   under different kinds (`HAS_POS` vs `HAS_UPOS`) won't merge. Both fracture omni-glottal / consensus
   convergence.

**Build the one primitive** and convergence, geometric analogy, the embed species, mold-filling
re-export, `GROUP BY` interpretability, and SQL traversal/generation all come online together — they
were always the same mechanism.

---

## 10. The actual schema (for grounding — verified)

- **`laplace.entities`** — identity only. `id` (BLAKE3-128 PK), `tier`, `type_id`,
  `first_observed_by`, `created_at`. No geometry. Live tier counts: 0≈1.12M, 1≈30k, 2≈3.41M, 3≈9.42M,
  4≈7.11M (+ ~52 at reserved high tiers 247/248/250 for bootstrap/sentinel nodes; the 0–4 scheme is
  not exhaustive). **These tier labels are as-recorded and corrupted by the mis-tiering defect
  (§9.3)** — single words are wrapped to tier-4 documents — so the counts are not a clean census of
  words / sentences / documents.
- **`laplace.physicalities`** — geometry. `id`, `entity_id`, `source_id`, `kind`
  (1=Content, 2=BuildingBlock, 3=Projection, 4=ProjectionOutput), `coord` + `trajectory` (both
  untyped PostGIS `geometry`; PointZM / GeometryZM enforced by CHECK constraints, not column typmod),
  `hilbert_index`, `radius_origin` (generated), plus `n_constituents`, `alignment_residual`,
  `source_dim`, `observed_at`. `UNIQUE(entity_id, source_id, kind)`. Live: 20,957,504 rows, **all
  kind=1**.
- **`laplace.attestations`** — typed relations + Glicko-2 state. `id` (BLAKE3 of the 5-tuple),
  `subject_id`, `kind_id`, `object_id?`, `source_id`, `context_id?`, `rating`/`rd`/`volatility`
  (int64 fixed-point ×1e9), `last_observed_at`, `observation_count`.
  `UNIQUE NULLS NOT DISTINCT (subject,kind,object,source,context)`. Live: 51,004,929 (~51.0M) rows.
  **This table is currently the *evidence* layer** (per-witness, source in identity, obs=1); the
  *consensus* layer (§6) is not materialized, and per-(layer,head) provenance is not recorded.
- **Glicko-2 kernel** — full Glickman-2013 implementation in `engine/core/src/glicko2.c`, pinned to
  the paper's worked example; exposed as the `laplace_glicko2_accumulate` SQL aggregate (unused by
  the live write path).
- **Geometry** — `laplace_geom` extension (functions installed in the `public` schema):
  `laplace_distance_4d`, `laplace_dwithin_4d`, `laplace_frechet_4d`, `laplace_hausdorff_4d`,
  `laplace_centroid_4d`, `laplace_radius_origin`.
- **Perf-cache** — `CodepointPerfcache`: a memory-resident map of all 1,114,112 codepoints → (S³
  `coord`, hilbert, hash), the single source shared by the engine + PG extension. Resolves leaf ids →
  coordinates in O(1) (microseconds) — the leaf resolver for the trajectory cascade, and what makes
  id → coordinate reconstruction cheap (§5).
- **Query entry point** — `laplace inspect <text>` (`Program.cs:160-261`) over the
  `entity_facets` / `entity_physicalities` / `attestations_out` / `attestations_in` SRFs.
  *Verification gotcha:* `inspect` prints ids big-endian (`Hi||Lo`) while the stored `bytea` is
  little-endian struct bytes — pasting a printed id into `psql` as `decode(...,'hex')` finds 0 rows.
  Resolve through the SRFs, not the printed hex.

---

## 11. Open design questions (mechanisms named but not yet specified)

The documentation audit flagged that several headline capabilities are *motivated* but their
mechanism/math is not written down. These are the real design work — listed so they are not mistaken
for solved:

- **Consensus update math (the keystone).** §7 gives the *factors* (kind rank × source trust × tenant
  trust × magnitude `strength`) but not how a weighted witness observation maps into a Glicko-2
  update — does weight scale the observation's RD, act as a pseudo-observation count, or set the
  opponent? This is the actual definition of "consensus" and is unspecified.
- **Tenant/user trust.** Not built (no id), and the form of its combination with kind/source rank is
  undefined.
- **Evidence-layer schema.** §6 prescribes "retain evidence *with* layer/head provenance, RLE'd" but
  gives no schema — a separate table? a provenance array on the consensus row? a repurposed
  `context_id`? RLE and per-(layer,head) retention pull in opposite directions and must be reconciled.
- **Voronoi query semantics.** §8 stores embed-species Voronoi cells as "distributional consensus" but
  never says what a query *does* with a cell (nearest-cell assignment? consensus centroid? cell volume
  = cross-model disagreement?).
- **Mold-filling synthesis.** §8 describes re-export by metaphor; the actual map from a consensus
  relation / embed-species cell back to a concrete weight-tensor value is unspecified (the current
  path is the magnitude-broadcast stub, §9.7).
- **Generation termination & distribution-equivalence.** §2 generation needs a stop rule (EOS as a
  relation? a μ threshold? a depth bound) and an argument for *why* a ranked-μ walk reproduces the
  model's conditional distribution rather than a different ranking.
- **Omni-modality ingestion path (INTENDED).** The pixel/audio → integer → codepoint reduction is
  asserted but has no decomposer (Image/Audio are stubs). Treat as intended until built.
