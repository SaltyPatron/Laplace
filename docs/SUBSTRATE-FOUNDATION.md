# SUBSTRATE-FOUNDATION.md — the ratified lens (READ BEFORE TRUSTING ANY OTHER DOC)

**Status:** Authoritative. Ratified directly by Anthony Hart on 2026-05-28. Where any other doc, ADR, issue, or shipped code conflicts with this file **on the conceptual core below, this file wins and the other artifact is the thing to correct.**

## Why this file exists

The other docs/ADRs/issues were largely drafted by an AI assistant across prior sessions and are **corrupted by conventional-AI pattern-matching**. Treating them as source-of-truth re-injected those hallucinations every session. This file is the corrected core, stated once, so corrections can be made *toward* it without re-deriving from corrupted sources and without losing the whole picture in any single file.

Authority order on the conceptual core: **Anthony's corrections (this file) > prose docs/ADRs/issues.** For *verifiable mechanical facts* (schema columns, build/CMake, file paths, function signatures) trust the **running system**, checked live, not prose.

**Supersession (2026-06-03):** `CLAUDE.md` (the inventor's current working agreement) and
`docs/ARCHITECTURE.md` are the present statement of the invention. Anthony's corrections made
*after* this file's ratification supersede it; the places where that happened are marked inline
below. Everything unmarked stands.

---

## The invention in one paragraph

Laplace is a universal, content-addressed, CPU-native knowledge substrate that **dissolves any digital artifact — AI models, corpora, lexicons, images, audio — into grounded semantic facts** and re-synthesizes superior, custom-shaped output on demand. Every atom is a Unicode codepoint; everything composes up a Merkle DAG of content-addressed entities; knowledge is **typed, sourced, Glicko-2-rated attestations** between those entities; geometry on S³ is the **canonical shared embedding frame** every source morphs into (the cross-model moat). It replaces the transformer: stored weights → rated relationship graph; the GEMM forward pass → indexed A\* traversal across many typed arenas; training → authoring facts (CRUD). No GPU, no GEMM at runtime.

---

## The ratified core truths (the lens)

1. **Model ingestion is a streaming O(params) ETL of weight tables — never a recompute.** A weight tensor is a 2D lookup table flattened to a 1D float array; each cell is an *already-computed* relationship. Stream the tensor, emit significant cells as Glicko-2 matchup observations, in parallel (oneTBB). It must scale linearly to frontier models (Qwen3-480B, Llama4 Maverick, DeepSeek MoE, Flux diffusion). **Forbidden:** doing GEMM at ingest (`E·W·Wᵀ·Eᵀ` bilinear over vocab²), materializing a vocab² matchup space, or a flat top-k that discards most of the model. That approach took an hour on a 2 GB model and produced an empty result (646/32000 tokens) — it is the disease, not a tuning knob. *(The earlier "superseded" note here was itself the misreading: CLAUDE.md's "every interior tensor is a token×token bilinear through the embedding" is the READ SEMANTICS — what a stored cell means when composed through the embedding at QUERY time, by μ-ranked joins — never an instruction to materialize the product at ingest. This truth stands exactly as ratified: ETL on conventional AI for AI — stream the cells at rest, resolve the hidden-dim surrogate keys through the source's own embed/lm_head mapping tables, load as adjudicated matches under the ten tensor-role kinds, POSITIONS AGGREGATE as witnesses. Records are bounded by the schema shape — never by depth, never by parameter count.)*

2. **Each weight cell is one Glicko-2 matchup *outcome*; there are many matchups per entity-pair** (across tensor role, head, layer). They accumulate into a consensus rating (consistent → low RD). Weight = outcome; the source model's own trust = opponent strength; store only the emergent consensus, never the weight, never bit-perfect.

3. **The S³ glome IS the canonical embedding space** — every source is morphed in via Procrustes/Laplacian-eigenmaps/Gram-Schmidt onto the Unicode-anchored frame. That shared frame is the cross-model/dim/vocab consensus moat. It is **not** a conventional per-model embedding. **Retrieval is NOT nearest-neighbor:** geometry only *seeds candidates*; what pulls back and how hard is **Glicko-2 effective-μ** across typed arenas (RD, volatility, source trust, lineage, context, arena policy). **Forbidden framing:** "geometry is just an index," "physicalities aren't knowledge." *(Superseded in part — CLAUDE.md, current: "S³ coords are structural, not semantic; geometry does analogy/structure via trajectory operators (Fréchet/intersect/overlap), a different axis from relatedness." Trajectory SHAPE is a real relational-reasoning surface — geometry is never a dumb index — but point-proximity is not relatedness; relatedness is consensus μ. The two axes are decoupled and co-equal.)* The dynamics over the geometry are attestation-based, not distance-based.

4. **Inference is indexed A\*, not brute force and not GEMM.** Hops are index seeks (GIST/Hilbert to seed; `attestations(subject,kind)` + `rating DESC` to expand best-supported arena-neighbors), bounded ≈ O(tier-depth) regardless of substrate size. Each step consults *many typed arenas on the same entities* — strictly more expressive than one untyped GEMM dot-product.

5. **Trust is a Glicko-2 value, self-tuning from cross-source agreement — never a tier or fixed class.** The word "tier" is reserved exclusively for the Merkle stratum (T0 = Unicode codepoints, in all cases). Any "trust tier / TrustClass_* ladder / kind-value tier" is corruption.

6. **Bit-perfect preservation is worthless** — it only returns the file you already had. Dissolve to semantic facts; discard the blob. **Seed-source attestations (WordNet/OMW/UD/Wiktionary/Tatoeba/ConceptNet/Atomic2020) are OPTIONAL enrichment** (independent ground truth for Glicko-2 to adjudicate against); semantic ingest of any model alone is the mandatory spine. The **recipe is a fillable mold** — synthesis pours substrate facts into any chosen shape (dim, dense/MoE, layers, vocab, dtype). Same machinery fills the source's own mold or any other (retarget) — always a re-export of consensus into the chosen shape, never a reproduction of the input ("round-trip" is banned vocabulary for the model path).

7. **Numbers are grounded compositional entities** — codepoint digits composing up the tier ladder; `1` is one shared entity everywhere (text, price, pixel, index, weight); `[3,'.',1,4]` is a real entity. This grounding is why Laplace can do exact math and conventional AI cannot: conventional tokens are "all tug, no ground" (meaning only from attention, numbers shredded by arbitrary tokenization, no canonical `1` for `1+1=2` to anchor to).

8. **An attestation exerts a fact on an entity; entities are/resolve-to tokens; export materializes the consensus of those facts into the mold's tokens.** Authoring facts (CRUD over entities) replaces gradient descent — discrete, sourced, rated, removable. The exported model is "the facts you exerted, materialized into a chosen shape," not a copy or a weight-average.

9. **CPU-native, no GPU, no GEMM at runtime** — verified: this machine has no GPU; the engine links MKL/TBB, not CUDA; the substrate seeded and runs anyway.

10. **Cute names are a tell, not a concept.** "Codec" (implies round-trip preservation — banned), "food," "vampire mode," "build-a-bear" are labels that stand in for understanding. State the mechanism, not the label.

---

## OPEN QUESTIONS — flag, never invent

These are genuinely unsolved. Any doc/ADR that asserts a confident answer here is hallucinated and must be marked OPEN, not "corrected" into a different guess:

- **Interior `d×d` tensor axis → token-entity resolution.** *(SUPERSEDED — answered by the
  set→set correction in CLAUDE.md; this entry must never again be cited as open.)* A hidden
  unit's direction is PLACED on the shared S³ frame by the same morph as tokens; membership in
  the unit's sets is geometric (Voronoi assignment over token placements — never argmax, never
  a magnitude floor, never operand blobs); the unit is one set→set hyperedge observation with
  its signed aggregate strength as magnitude and (layer, head) as witness.
- The exact arena/kind assignment per interior tensor role.
- The synthesis "pour facts into the mold" algorithm at frontier scale.

---

## How to use this file when auditing another doc/ADR/issue

1. Read the target.
2. For each claim, check it against the truths above.
3. If it contradicts a truth → it is corrupt: rewrite it to match, and note what was wrong.
4. If it asserts an OPEN question as settled → replace the false certainty with an explicit OPEN marker citing this file.
5. If it is correct or is verifiable plumbing → leave it, verify mechanical facts live.
6. Never introduce a new confident claim on an OPEN question. Flag, don't fabricate.
