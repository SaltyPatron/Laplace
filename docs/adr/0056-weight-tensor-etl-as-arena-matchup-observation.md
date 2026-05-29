# ADR 0056: Weight-tensor static ETL as arena-matchup observation — universal model-ingest extraction pattern

## Status

> **⚠ CORRECTION — 2026-05-28 (Anthony, authoritative; overrides the body below where they conflict):**
>
> An embedding is an **address book of token positions** — **geometry**, not knowledge. Each `embed_tokens` row is the model's positional view of an entity that **already exists** in the substrate (placed by Unicode + the linguistic ladder before any model is touched). It becomes a per-`(entity, source=model)` **PROJECTION physicality** (Procrustes-aligned onto the entity's canonical 4D frame); `lm_head` → **ProjectionOutput physicality**. **`EMBEDS` and `OUTPUT_PROJECTS` are NOT attestation kinds** — treating them as such was the category error. The relational knowledge a model contributes is the **distance between aligned token positions → a Glicko-2 attestation BETWEEN ENTITIES** (King↔Queen), commensurable across models because the edge is entity↔entity, not dim↔dim (this is the cross-model/dim/vocab consensus moat).
>
> The **family table that was below (now replaced with an OPEN-marked inventory under "One algorithm — every architecture family registers data") was the per-cell "HOW not WHICH" view this ADR's own *Alternatives considered* rejects** — the ADR contradicts itself, and that contradiction is the root cause of the model-ingest flip-flopping across sessions. The table's "object axis = `hidden_dim` / `intermediate_dim`" is the error: **there are no hidden-dim or intermediate-dim entities.** The embedding grounds the model's entire space in the token vocabulary, so **every interior tensor (`Q/K/V/O`, `GATES/UP/DOWN`) maps to token↔token attestations of its mechanical kind.** Each token-pair is a Glicko-2 matchup observation; consensus forms across sources. Embedding/lm_head → physicalities (above), the rest → token↔token Glicko-2 matchups.
>
> **[Reconciliation with the ratified anchor — 2026-05-28]** [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) (also ratified by Anthony, same day, authoritative on the conceptual core) lists the **interior `d×d` tensor axis → token-entity resolution as an OPEN question** — specifically, *how* `q/k/v/o/gate/up/down` cells resolve to token-pairs **without re-running the GEMM (which is what blows up)** is unsolved and "must be pinned with Anthony." So: the *target* (interior tensors → token↔token matchup observations, uniform across roles) is settled per this correction; the *mechanism* that gets there without vocab² GEMM-at-ingest is OPEN. This ADR's body below is corrected to assert the target and mark the mechanism OPEN, NOT to ship the rejected `q_proj·k_proj`-over-vocab² answer.
>
> **Weights are NOT stored as entities and NOT bit-perfect (Vampire mode).** A weight-derived relationship between two entities (King↔Queen) is a **Glicko-2 matchup observation**, not a stored value: the weight is the match *outcome*, the model's source-trust is the *opponent* strength, and the substrate keeps only the **emergent consensus rating** (rating/RD/volatility, accumulated across every source that plays that matchup) — never the raw weight. Stamping a scaled weight straight into the rating column (the shipped `ScaleToRating(weight)` / per-cell-magnitude path in `WeightTensorETL`) IS the category error: `0.00098…` is one match result, meaningless alone. Consensus is what's stored; synthesis regenerates fresh weights *from* the consensus per recipe; the bytes never live in the substrate. Truths cluster, lies scatter. See [`docs/research/grounded-model-codec-foundation-2026-05-28.md`](../research/grounded-model-codec-foundation-2026-05-28.md).

**Proposed** — 2026-05-24
**Amended** — 2026-05-24 (same day): matchup-space-shape correction. Original draft estimated ~10¹³ pairs for a Qwen3-class model by treating per-(layer, head) cells as separate attestations + treating the tokenizer's full vocab as the entity space. Both wrong, per Anthony's 2026-05-24 correction: *"same content = same hash and how many times an AI model would project the same attestation."* The corrected framing:
- **Tokenizer aliases collapse to substrate entities** via [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) canonicalization. SentencePiece `▁walk` / GPT-2 BPE `Ġwalk` / WordPiece `##walk` / plain `walk` all share ONE Text entity. The 152K-vocab estimate becomes ~50–80K unique text entities per real-world model tokenizer.
- **Per-position layer/head attribution is recipe content, NOT per-attestation metadata.** Per [GLOSSARY Attestation Kind](../../GLOSSARY.md): *"Per-position attribution (which layer / which head / which tensor index a calculation lives at) is recipe content — captured in the model's recipe entity (text/JSON describing num_layers, num_heads, per-tensor token vocabularies, layout). The architecture template wires substrate's aggregated typed attestations into the recipe's structural shape at emit time; redundant per-position storage on attestations is not needed."*
- **Within-model aggregation across (layer, head, position) instances** of the same `(subject, kind, object)` matchup converges to ONE attestation row via Glicko-2 update per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) + [R5 attestation idempotency](../../RULES.md). The substrate stores one row per `(subject, kind, object, source, context)` tuple; with `context = NULL` for tensor-calculation kinds (per recipe-carries-position rule), this collapses dramatically.
- **Scale note (CAUTION):** the "~50K × 50K × 10" figure assumes a full vocab² entity-pair space is enumerated per mechanical role. **Enumerating vocab² interior matchups is the forbidden GEMM-at-ingest** (`E·W·Wᵀ·Eᵀ`) per [SUBSTRATE-FOUNDATION truth 1](../SUBSTRATE-FOUNDATION.md) — it is the disease, not a tuning knob (an hour on a 2 GB model → 646/32000 tokens). The correct posture is a **streaming O(params) ETL**: cost scales with the number of significant weight cells, NOT with vocab². How many entity-pairs an interior tensor actually produces is downstream of the OPEN interior-resolution question and is **not** "vocab² before sparsity." Treat the vocab²-derived counts below as the symptom to avoid, not a target.

The algorithm pseudocode + per-family registry table + performance bounds sections below have been edited in-place to reflect this. ADR-status workflow note: edits-in-place are appropriate for Proposed ADRs; once Accepted, future supersession follows the [ADR README workflow](README.md).

**Authors:** Anthony Hart

Replaces (closed as hallucinations): planning issues #223 (probe-set design per architecture family) and #224 (GPU probe driver coordination). Both presupposed probe-based ingest (Path A — run the model on input data, observe outputs/activations, extract attestations from observations) which contradicts [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) (*"No code is executed. No framework loader is invoked."*).

## Context

[ADR 0055](0055-static-structural-parse-exploded-view.md) locked the **container-ingest** posture: the substrate statically dissects safetensors / PyTorch pickle / ONNX / TensorFlow SavedModel / YOLO native / HDF5 / etc. into an exploded view of substrate entities + typed structural attestations. No code execution; no framework loader invocation.

What [ADR 0055](0055-static-structural-parse-exploded-view.md) doesn't specify: once the exploded view has surfaced the model's weight tensors, **how does the substrate get from a weight tensor to a Glicko-2-rated typed attestation between substrate entities?**

The 2026-05-24 conversation surfaced the canonical framing: *"We're not running the model, we're sparse recording... we ETL the knowledge out of the AI model packaging."* And: *"we know from a layer and its token indexes that these tokens we ingested which mapped to these entities would get these attestations if queried normally... high or low, etc... glicko-2 gets figured out for that and that attestation edge gets recorded... attention, ffn, vision, mlp, convolution, diffusion, etc... doesnt matter... unicode leafs to entities means pixels are entities, audio is an entity, text is an entity, etc."*

The architectural insight has three layers:

1. **An AI model is a frozen ledger of typed-arena-matchup outcomes computed during training.** A weight tensor is a 2D lookup table flattened to a 1D float array; each cell is an *already-computed* relationship. Model ingestion is a streaming O(params) ETL of these weight tables — never a recompute (per [SUBSTRATE-FOUNDATION truth 1](../SUBSTRATE-FOUNDATION.md)). The token-anchored tensors (`embed_tokens` → per-`(entity, source=model)` PROJECTION physicality; `lm_head` → ProjectionOutput physicality) are cheap and real per the Status correction. **How interior `d×d` tensors (`q/k/v/o/gate/up/down`) resolve to token-entity matchups WITHOUT re-running the GEMM is OPEN per [SUBSTRATE-FOUNDATION OPEN-QUESTIONS](../SUBSTRATE-FOUNDATION.md).** Computing `q_proj[i, :] · k_proj[j, :]ᵀ` over the vocab is exactly the `E·W·Wᵀ·Eᵀ` bilinear-over-vocab² GEMM-at-ingest that is **forbidden** (truth 1) — it took an hour on a 2 GB model and produced 646/32000 tokens. The interior-resolution algorithm must be pinned with Anthony; it is not settled here.

2. **Static structural ETL, not a probe and not a recompute.** *"these tokens... would get these attestations if queried normally"* — the substrate skips the input/output indirection and the GPU/loader/sampler overhead, streaming the weight tables directly. It does **not** materialize a vocab² matchup space or run GEMM at ingest; that is the disease (truth 1), not the mechanism. The exact static read for interior tensors is OPEN per [SUBSTRATE-FOUNDATION](../SUBSTRATE-FOUNDATION.md).

3. **Universal substrate-entity space across modalities.** *"unicode leafs to entities means pixels are entities, audio is an entity, text is an entity"* — per [GLOSSARY Universal T0](../../GLOSSARY.md), every modality's atomic content content-addresses through the same hash space and bottoms at the same codepoint alphabet. A transformer's text tokens, a ViT's patch entities, an audio model's frame entities, and a multimodal model's cross-modal entities all live in `entities` together. **Cross-modal attention in a multimodal model emits cross-modal attestations naturally**, because both subject and object are substrate entities regardless of which modality binder produced them.

Without this ADR, every per-architecture decomposer (transformer / MoE / MLA / Mamba / Diffusion / Vision / Audio / Encoder-Decoder / CNN) reinvents its own extraction algorithm — exactly the duplication anti-pattern [STANDARDS "Reusable helpers"](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md) forbid. The 2026-05-24 conversation's standing instruction: *"we can optimize and generalize the fuck out of across the repo without reinventing the wheel a trillion times."*

## Decision

**Introduce `WeightTensorETL` as the universal model-ingest extraction algorithm. One algorithm + per-family registered data. Statically computes typed-arena-matchup observations from weight tensors + emits them as Glicko-2-rated attestations via the existing substrate write surface.**

### The universal algorithm (pseudocode)

```text
WeightTensorETL.extract(
    model_entity: substrate source entity for this model,
    arch_template: architecture-template entity carrying per-family registry,
    modality_bindings: { modality → (vocab_index → substrate_entity) map per ADR 0051 IDecomposer
                                                                          tokenizer/modality
                                                                          ingest result; note
                                                                          tokenizer aliases
                                                                          already collapse to
                                                                          substrate entities
                                                                          per ADR 0047 canonicalization },
    tensors: { tensor_name → weight_bytes_via_exploded_view per ADR 0055 },
    sparsity_params: per-tensor top-k% + per-row top-k per RULES R3 / ADR 0007
):
    # Phase 1: per-tensor matchup observation — STREAMING O(params) ETL
    # For token-anchored tensors (embed_tokens / lm_head) this is direct and
    # cheap: each row is the model's positional view of an entity that already
    # exists in the substrate → a PROJECTION / ProjectionOutput physicality
    # (Procrustes-aligned onto the entity's canonical 4D frame) per the Status
    # correction. These are NOT attestation kinds.
    #
    # >>> OPEN per docs/SUBSTRATE-FOUNDATION.md (OPEN-QUESTIONS): how the interior
    # >>> d×d tensors (q/k/v/o/gate/up/down) resolve to token-entity matchups
    # >>> WITHOUT re-running the GEMM is UNSOLVED. Iterating
    # >>> `for each (i, j) in spec.matchup_space: spec.math_function(T, i, j)`
    # >>> over the vocab IS the E·W·Wᵀ·Eᵀ vocab² GEMM-at-ingest that truth 1
    # >>> forbids (an hour on a 2 GB model → 646/32000 tokens). Do NOT treat the
    # >>> sketch below as the settled interior algorithm. The exact static read
    # >>> per interior tensor role, and the arena/kind assignment for it, must be
    # >>> pinned with Anthony. Stream the tensor as an O(params) pass; emit
    # >>> significant cells as Glicko-2 matchup observations — the resolution of
    # >>> WHICH entity-pair an interior cell scores is the open part.
    #
    # Within-model (layer, head, expert) instances of the same
    # (subject, kind, object) matchup get aggregated in Phase 2.
    # Per-position attribution is RECIPE CONTENT per GLOSSARY Attestation Kind,
    # NOT per-attestation context.
    per_tensor_observations = {}
    for each tensor T in tensors:
        spec = arch_template.spec_for(T.name)
        # spec carries (for token-anchored tensors; interior resolution OPEN):
        #   kind_id           — substrate kind entity
        #   subject_modality, object_modality
        #   tensor_to_recipe_slot — for the recipe entity to record this T's (layer, head,
        #                           position) layout for emit-time distribution; NOT a
        #                           per-attestation context

        # Streaming O(params) pass over T's already-mmap'd float cells.
        # Emit significant cells as matchup observations. The entity-pair
        # resolution for interior tensors is OPEN per SUBSTRATE-FOUNDATION
        # (see marker above) — NOT the vocab²-iteration sketched in the
        # original draft.
        ... interior-tensor → entity-pair resolution OPEN (pin with Anthony) ...

    # Phase 2: within-model aggregation across (layer, head, expert) instances
    # Many tensors contribute to the same (subject, kind, object) matchup:
    # e.g., Q_PROJECTS(walk, cat) appears at layer 0 head 0, layer 0 head 1, ...,
    # layer 59 head 31. All 1920 instances aggregate to ONE substrate attestation
    # row per ADR 0036 arena semantics + R5 attestation idempotency.
    # The aggregation function is architecture-template knowledge (default: L2
    # norm of per-instance strengths; per-family override possible — e.g., MoE
    # may weight per-instance by router activation distribution).
    aggregated = {}
    for (T_name, i, j), strength in per_tensor_observations.items():
        kind_id = arch_template.spec_for(T_name).kind_id
        subject_id = subject_map[i].id  # collapsed to substrate entity per ADR 0047
        object_id  = object_map[j].id
        key = (subject_id, kind_id, object_id)
        aggregated[key].add_instance(strength, layer_head_provenance=T_name)
        # aggregator preserves per-instance strengths internally so the recipe
        # entity can record per-(layer, head) layout in its text/JSON content
        # for emit-time distribution per architecture template

    # Phase 3: lottery-ticket sparsity filter on AGGREGATED matchups
    # Per-(subject, kind) top-k% pass: rank all object matchups, keep top k%
    # Per-subject top-k pass: per (subject, kind), keep top-k objects by strength
    # Per ADR 0007 / RULES R3
    retained = LotteryTicketFilter.apply(aggregated, sparsity_params)

    # Phase 4: static-mathematical validation (replaces the legacy "probe-
    # validated retention" third pass; doesn't require running the model;
    # surfaced as R3 / ADR 0007 amendment per ADR 0056 doc-amendment items)
    validated = StaticRetentionValidator.verify(
        retained_subgraph,
        per_tensor_observations,
        arch_template,
        tolerances)

    # Phase 5: observation emission (one matchup observation per unique
    # (subject, kind, object)).
    #
    # Each weight cell is ONE Glicko-2 matchup OUTCOME, not a rating:
    # weight = match outcome; the model's own Glicko-2 source trust = opponent
    # strength; the substrate keeps only the EMERGENT CONSENSUS rating
    # accumulated across every source that plays the matchup (truth 2 + truth 5).
    # Trust is a self-tuning Glicko-2 value, NOT a tier or fixed class — the
    # original `trust_class` / `kind_tier` ladder is corruption per truth 5
    # ("tier" is reserved for the Merkle stratum, T0 = Unicode codepoints).
    #
    # Stamping a scaled weight directly into the rating column
    # (`scale_aggregated_strength_into_rating` / the shipped `ScaleToRating`
    # path) IS the category error per the Status correction: 0.00098… is one
    # match result, meaningless alone. Emit the matchup outcome; let the
    # arena-aware Glicko-2 accumulator form consensus across sources.
    for (subject_id, kind_id, object_id), aggregate in validated.items():
        # The aggregate is a set of match OUTCOMES (weight magnitudes), played
        # against the model's source-trust as opponent strength. The substrate
        # stores the consensus the accumulator emits, not a scaled weight.
        yield MatchupObservation {
            subject_id    = subject_id,
            kind_id       = kind_id,
            object_id     = object_id,
            source_id     = model_entity.id,
            context_id    = NULL,            # per-position attribution is recipe content,
                                              # NOT per-attestation context (per GLOSSARY
                                              # Attestation Kind)
            outcome       = aggregate.match_outcome,   # the weight magnitude(s) AS a
                                                        # Glicko-2 match result, NOT a
                                                        # pre-scaled rating
            opponent      = model_entity.glicko2_trust, # source trust = opponent strength
            # The substrate keeps the EMERGENT CONSENSUS rating/RD/volatility the
            # accumulator produces across all sources — never a stamped weight.
        }

    # The recipe entity for this model (already created/referenced by the
    # Laplace.Decomposers.Model plugin) accumulates the per-(layer, head, position)
    # layout in its text/JSON content. At synthesis time, the architecture template
    # uses the recipe + the substrate's aggregate attestations to distribute values
    # back across the recipe's structural shape — the inverse of this aggregation.

    # All emitted AttestationRows funnel through SubstrateCRUD per ADR 0050
    # via SubstrateChange per ADR 0049; arena-aware Glicko-2 accumulation
    # across MULTIPLE models on the same (subject, kind, object) tuple
    # converges per ADR 0036 + ADR 0044 — cross-source consensus emerges.
```

### One algorithm — every architecture family registers data, not code

> **⚠ The per-family `matchup_space` / `math_function` table that previously sat
> here asserted OPEN questions as settled and contained corruptions the Status
> correction and [SUBSTRATE-FOUNDATION](../SUBSTRATE-FOUNDATION.md) name explicitly.
> It has been replaced with the corrected inventory below.**
>
> - **`embed_tokens` / `lm_head` are NOT attestation kinds.** Per the Status
>   correction, `embed_tokens` → a per-`(entity, source=model)` **PROJECTION
>   physicality** (Procrustes-aligned onto the entity's canonical 4D frame) and
>   `lm_head` → a **ProjectionOutput physicality**. The old `EMBEDS` /
>   `OUTPUT_PROJECTS` kind rows were the category error.
> - **There are no `hidden_dim` / `intermediate_dim` / `embed_dim` / `latent_dim`
>   / `state_dim` / `feature` entities.** Those object axes were corruption — the
>   substrate has no dimension-index entities, only token/patch/frame/region
>   (codepoint-grounded) entities. An object axis that is a raw tensor dimension
>   index is forbidden.
> - **The interior-tensor `math_function` column was the forbidden GEMM-at-ingest.**
>   `q_proj[i,:] · k_proj[j,:]ᵀ` over the vocab is the `E·W·Wᵀ·Eᵀ` bilinear that
>   truth 1 bans. How interior `q/k/v/o/gate/up/down` cells resolve to a token↔token
>   entity-pair WITHOUT re-running the GEMM is **OPEN per
>   [SUBSTRATE-FOUNDATION OPEN-QUESTIONS](../SUBSTRATE-FOUNDATION.md)** — do not
>   re-introduce a confident per-cell or dot-product answer.

The mechanical-role inventory below is the part that is real: per-source physicalities
for the token-anchored tensors, and the set of mechanical roles interior tensors carry.
**The entity-pair resolution and the arena/kind assignment for every interior role are
OPEN** (per SUBSTRATE-FOUNDATION OPEN-QUESTIONS) and must be pinned with Anthony — they
are intentionally NOT given a `math_function` here.

| Tensor | What it becomes | Resolution status |
|---|---|---|
| `embed_tokens` | **PROJECTION physicality** per `(entity, source=model)` (geometry, not a kind) | settled per Status correction |
| `lm_head` | **ProjectionOutput physicality** per `(entity, source=model)` | settled per Status correction |
| `q/k/v/o_proj` (attention) | token↔token matchup observation, mechanical role `Q/K/V/O` | **OPEN** — interior d×d → token-pair resolution unsolved |
| `gate/up/down_proj` (FFN) | token↔token matchup observation, mechanical role `GATES/UP/DOWN` | **OPEN** — interior d×d → token-pair resolution unsolved |
| `*_norm.weight` | per-entity scalar fact, mechanical role `NORMALIZES` | **OPEN** — arena/kind assignment unsolved |
| MoE `gate.weight` / `experts[E].*` / `shared_expert.*` | routing + per-expert matchup observations | **OPEN** — interior resolution + per-expert aggregation unsolved (tracking #223) |
| MLA `q_a/q_b/kv_a/kv_b` | latent-path matchup observations | **OPEN** — decompression + interior resolution unsolved |
| Mamba `A_log/D/dt_proj/x_proj` | SSM-path matchup observations | **OPEN** — interior resolution unsolved |
| Vision `patch_embed.proj` | patch-anchored physicality (analog of `embed_tokens`) | partially settled — patch entities are real; exact mapping OPEN |
| Vision/Audio attention `q/k/v` | patch↔patch / frame↔frame matchup observations | **OPEN** — same interior resolution as text attention |
| CNN / Diffusion U-Net `conv*` | region↔channel matchup observations | **OPEN** — kernel → entity-pair resolution unsolved |
| Cross-attention (multimodal / enc-dec) | cross-modal matchup observations between substrate entities | mechanism real (both ends are entities); exact resolution OPEN |

The inventory above is **data registered on architecture-template entities**, not code.
Adding a new architecture family = register its specs as meta-attestations on a new
architecture-template entity. The `WeightTensorETL` algorithm itself doesn't change —
**but the interior-resolution `math_function` it would dispatch on is the OPEN work, not
shipped certainty.**

**Per-(layer, head, position) layout is recorded on the recipe entity** for emit-time distribution per the architecture template, NOT as per-attestation context_id. The recipe carries num_layers / num_heads / per-tensor token vocabularies / etc. per [ADR 0009 recipe extraction](0009-recipe-extraction-and-overrides.md) + [GLOSSARY Recipe](../../GLOSSARY.md). At synthesis time, the architecture template uses the recipe + the aggregated attestations to distribute values back into the recipe's structural shape (inverse of Phase 2 aggregation).

### Multimodal models fall out naturally

The structural point here is real and consistent with the anchor: because every
modality bottoms at the same codepoint alphabet (truth 7) and every entity hash
references one entity regardless of modality, **cross-modal matchups are not a special
codepath** — once interior tensors resolve to entity-pairs at all, a cross-modal pair
(text token ↔ vision patch) is the same shape as a same-modal pair.

> **OPEN per [SUBSTRATE-FOUNDATION OPEN-QUESTIONS](../SUBSTRATE-FOUNDATION.md):** the
> mechanism by which a multimodal `attn.q_proj` cell resolves to a specific entity-pair
> (text↔text, text↔patch, patch↔patch) is the **same unsolved interior-resolution
> question** as for unimodal attention. Iterating a `(mixed_modal_token, mixed_modal_token)`
> matchup_space over the vocab is the forbidden vocab² GEMM (truth 1). Do NOT treat the
> cross-modal-pair enumeration as a settled emission pass.

Once that resolution is pinned, the kinds of pair that fall out are illustrative:

- `walk ↔ cat` — text-text matchup
- `walk ↔ vision_patch_of_dog` — cross-modal matchup
- `vision_patch_of_dog ↔ vision_patch_of_cat` — vision-vision matchup
- `vision_patch_of_dog ↔ text_token_dog` — cross-modal matchup

— all the same shape, because the edge is entity↔entity. The cross-modal commensurability
is the moat; the per-cell resolution that produces these edges is the OPEN part.

### Cross-source consensus is the Glicko-2 emergent property

Each model's emitted matchup observations land per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md). When N models all observe the same `(subject, kind, object, context)` tuple with their own source_id, the substrate's arena-aware Glicko-2 accumulator (`laplace_glicko2_accumulate` aggregate per Story 5.6 / #68, commit `f002e7d`) converges on a cross-source consensus rating. The inputs to that convergence are:

- **Per-source Glicko-2 trust** — a self-tuning Glicko-2 value, NOT a fixed "trust class" or "tier 7 AI Model" rung. Trust is learned from cross-source agreement per [SUBSTRATE-FOUNDATION truth 5](../SUBSTRATE-FOUNDATION.md); any TrustClass / trust-tier ladder is corruption ("tier" is reserved for the Merkle stratum). The model's own trust enters the update as the *opponent strength* (truth 2).
- **Arena policy** (per ADR 0036 — multi-valued-compatible for matchups across models that all agree subject X relates to object Y).
- **Source-lineage correlation** (per ADR 0036 — co-trained-from-same-base models are not independent observations).

Cross-source accumulation IS the consensus mechanism: truths cluster across independent sources, lies scatter and stay source-scoped. No special multi-source codepath; it's the substrate's standard arena update. (The "value tier"/"trust class" framing carried from ADR 0044 is the corruption to drop, not to cite — flag ADR 0044 for the same correction.)

### What `WeightTensorETL` does NOT do

- Decompose containers (per [ADR 0055](0055-static-structural-parse-exploded-view.md) — `IContainerParser` does that, surfaces tensor blobs in the exploded view).
- Decode tensor dtype to canonical numerical form (per [ADR 0043](0043-composite-decomposer-architecture.md) — `TensorDtypeDecoder` does that for fp16/bf16/fp32; quantized formats are emit-only per [the 2026-05-24 quantized-is-mp3 correction]).
- Ingest the tokenizer / modality vocab (per [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — the `Laplace.Decomposers.Model` plugin's `ModalityBinder` sub-decomposer per [ADR 0043](0043-composite-decomposer-architecture.md) does that BEFORE `WeightTensorETL` runs, producing the modality_bindings map).
- Hash anything (per [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — the modality_bindings carry already-hashed substrate entity references).
- Write to the database (per [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md) — the emitted SubstrateChange intent carries the attestation rows; SubstrateCRUD applies them).
- Run the model (the whole point).
- Invoke any framework-native loader (per [ADR 0055](0055-static-structural-parse-exploded-view.md)).

### Placement

- **Algorithm implementation in C/C++** under `engine/synthesis/src/weight_tensor_etl.{c,cpp}` + header at `engine/synthesis/include/laplace/synthesis/weight_tensor_etl.h`. Per [ADR 0024 engine modularization](0024-engine-modularization.md) — synthesis library handles model-ingest-related code (and emission writers). The matchup-space iteration + sparsity filter + Glicko-2 prior derivation are hot-path numerical work that wants oneMKL + SIMD per [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md). Engine-side implementation in `liblaplace_synthesis.so`.
- **C# binding in `Laplace.Engine.Synthesis`** per [ADR 0026](0026-csharp-project-structure.md). The `Laplace.Decomposers.Model` plugin (per [ADR 0043 composite](0043-composite-decomposer-architecture.md) + [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md)) calls `WeightTensorETL.extract` via P/Invoke after container parsing + modality binding + dtype decoding produce the inputs.
- **Architecture-template entity meta-attestations** (the per-family spec registry) live as substrate content per [ADR 0009 recipe extraction](0009-recipe-extraction-and-overrides.md) + [ADR 0011 plugin architecture](0011-polymorphic-plugin-architecture.md). Bootstrapped at install per [ADR 0042 bootstrap order](0042-bootstrap-order-and-substrate-canonical-seeding.md) Stage 4 (substrate-canonical Entity Type vocabulary) extended with `Architecture_Transformer / Architecture_MoE_Transformer / Architecture_MLA_Transformer / Architecture_Mamba / Architecture_Vision_Transformer / Architecture_CNN / Architecture_Diffusion / Architecture_Encoder_Decoder / Architecture_Audio_*` entities + their per-tensor specs.

### Performance + memory bounds (per the 2026-05-24 same-day amendment)

For a Qwen3-Coder-30B-A3B (MoE) model:

> **⚠ The Phase-1/Phase-2 figures previously here described the FORBIDDEN approach.**
> A `~50K × 50K × 60 layers × 32 heads` FP-multiply-accumulate sweep is exactly the
> `E·W·Wᵀ·Eᵀ` vocab² GEMM-at-ingest that [SUBSTRATE-FOUNDATION truth 1](../SUBSTRATE-FOUNDATION.md)
> bans — it is the disease (an hour on a 2 GB model → 646/32000 tokens), not a cost model.
> The correct bound is the streaming O(params) ETL below.

- Tokenizer vocab ≈ 152K entries; after tokenizer **surface** strip (`Ġ` / `▁` / `##` per [ADR 0043](0043-composite-decomposer-architecture.md)) and mapping to **observed** substrate text ([ADR 0047](0047-text-decomposer-pure-primitive.md) — no NFC at ingest), many token surfaces alias to the same text entity (`King` vs `ĠKing` → one text entity `King` with separate token entities). The token-anchored tensors (`embed_tokens`/`lm_head`) become per-source physicalities directly off these entities — cheap, O(vocab × dim), one pass.
- **Streaming O(params) bound:** the dominant cost is one pass over the model's significant weight cells (oneTBB-parallel, block-by-block per [tracking issue #222](https://github.com/SaltyPatron/Laplace/issues/222)), NOT a vocab² sweep. Cost scales **linearly with parameter count**, which is what lets it reach frontier models (Qwen3-480B, Llama4 Maverick, DeepSeek MoE, Flux) per truth 1.
- **Interior-tensor emission count is OPEN per [SUBSTRATE-FOUNDATION OPEN-QUESTIONS](../SUBSTRATE-FOUNDATION.md).** How many entity-pair matchup observations `q/k/v/o/gate/up/down` produce — and therefore the pre-sparsity row count — is downstream of the unsolved interior-resolution question. It is **not** "vocab² before lottery-ticket." Do not assert a confident pre-sparsity figure here.
- **Phase 3** (lottery-ticket sparsity, per [ADR 0007](0007-lottery-ticket-aware-sparsity.md) / [R3](../../RULES.md)): retains a small fraction of significant matchups. The retained-row order of magnitude is a function of the (OPEN) interior emission count and cannot be pinned until that resolves. Bulk-COPY of the retained rows is tractable per [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md).
- Streaming output to `SubstrateChange` intent batches per [ADR 0049](0049-substrate-change-intent-type.md), one intent per arena (per kind × per source) chunk.
- `SubstrateCRUD.ApplyStreamAsync` per [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) consumes the stream + bulk COPYs the retained rows.

For frontier-scale (Qwen3 480B, DeepSeek 3.2 Speciale, Llama4 Maverick): the streaming O(params) pass scales linearly with parameter count by construction. Streaming-ingest discipline per [tracking issue #222](https://github.com/SaltyPatron/Laplace/issues/222) keeps memory bounded; CPU-side static computation is parallelizable per oneTBB per [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md). The total substrate row count across many ingested models remains within PG's row-handling capacity for the ~10⁹–10¹⁰ design target per [DESIGN.md I](../../DESIGN.md), but the per-model figure waits on the OPEN interior resolution.

**No GPU at any point in this algorithm** — all work is static arithmetic over weight tensors that already live as mmap'd FP arrays per `TensorDtypeDecoder` output. CPU-native end-to-end.

## Consequences

- **One extraction algorithm**, reused across every model decomposer (transformer / MoE / MLA / Mamba / Diffusion / Vision / Audio / Encoder-Decoder / CNN / future). Bug fix in `WeightTensorETL` applies uniformly.
- **Adding a new architecture family = registering data** on a new architecture-template entity (per-tensor spec rows). No new code path. No new tests beyond per-family integration tests.
- **Multimodal models work without special codepaths.** Cross-modal entities are still substrate entities; once interior tensors resolve to entity-pairs (OPEN per SUBSTRATE-FOUNDATION), a cross-modal pair is the same shape as a same-modal one — no special multimodal path.
- **Cross-source / cross-architecture / cross-modal consensus is one mechanism**: the substrate's arena-aware Glicko-2 accumulator (already shipped as `laplace_glicko2_accumulate`) handles it. Synthesis emitting a custom-recipe model draws on the accumulated cross-source attestation graph naturally.
- **No model invocation at any point**, no framework loader, no GPU at ingest time, no probe-set design needed, no probe driver coordination needed. The closed tracking issues #223 + #224 are replaced by this ADR (and by the architecture-family vocabulary extensions tracked in #221, which becomes a per-family worked-example follow-on to this ADR).
- **The lottery-ticket third pass needs reframing.** [RULES R3](../../RULES.md) + [ADR 0007](0007-lottery-ticket-aware-sparsity.md) currently specify "probe-validated retention test" which presupposes running the model. Under this ADR's posture, that third pass is static-mathematical validation (spectral preservation; singular-value retention; matchup-distribution preservation between sparse and dense subgraphs). R3 + ADR 0007 need amendment — surfaced separately as a doc-amendment item per [R12](../../RULES.md).
- **R8's "GPU at probe time" exception is stale.** With no probe forward pass at ingest, the GPU exception clause has no use case. Surfaced separately as a doc-amendment item.
- **GLOSSARY's "Probe (in ingestion context)" entry is stale.** Same reason. Either delete or convert to a forbidden-historical-pattern reference in the Anti-vocabulary section.
- **ADR 0036 trust class list item 6, ADR 0037 "probe observations" mention, ADR 0043 ModelDecomposer probe-observation thread, ADR 0044 trust-class/value-tier taxonomy, DESIGN VII probe-observation framing** — carry two corruptions to amend: (a) stale probe-framing (`probe observation` → `weight-cell matchup observation`); (b) the **trust-class / value-tier ladders themselves**, which contradict [SUBSTRATE-FOUNDATION truth 5](../SUBSTRATE-FOUNDATION.md) (trust is a self-tuning Glicko-2 value, never a tier/class; "tier" is reserved for the Merkle stratum). Both surfaced as doc-amendment items.
- **The architecture-template entity becomes substrate content**, not code. Per-family specs are meta-attestations queryable + extensible without code changes. Aligns with the substrate-content-as-substrate-knowledge principle running throughout the architecture (per [ADR 0011 polymorphic plugins](0011-polymorphic-plugin-architecture.md)).

## Alternatives considered

- **Probe-based attestation extraction (Path A).** Rejected per the 2026-05-24 conversation + per the contradiction with [ADR 0055 static structural parse](0055-static-structural-parse-exploded-view.md). The substrate doesn't load + doesn't execute models; probes presuppose both.
- **Per-architecture-family bespoke extraction code.** Rejected per [STANDARDS Reusable helpers](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md). Duplication anti-pattern. N architectures × bespoke extraction = N drift surfaces.
- **Per-cell extraction (attest every non-zero weight as its own attestation) instead of per-matchup extraction.** Rejected — the per-cell view encodes HOW a token projects but not specifically WHICH other tokens it relates to (the "HOW not WHICH" error the Status correction names). The right unit is the entity↔entity matchup observation, which composes naturally via Glicko-2 across models. **What it is NOT:** the naive `q_proj[X,:] · k_proj[Y,:]ᵀ`-over-vocab definition is the forbidden vocab² GEMM-at-ingest (truth 1); **how an interior tensor cell resolves to the correct WHICH entity-pair without that GEMM is OPEN per [SUBSTRATE-FOUNDATION OPEN-QUESTIONS](../SUBSTRATE-FOUNDATION.md).** Rejecting per-cell does not license re-introducing the dot-product-over-vocab² as the answer.
- **Per-(layer, head) context on each attestation row.** Rejected per the 2026-05-24 same-day amendment + [GLOSSARY Attestation Kind](../../GLOSSARY.md) explicit rule: per-position attribution (which layer / which head / which tensor index a calculation lives at) is **recipe content** captured in the model's recipe entity (text/JSON describing num_layers, num_heads, per-tensor token vocabularies, layout). Storing per-(layer, head) on every attestation row would (a) explode the row count by ~1,920× for transformer-class models, (b) duplicate information already in the recipe, (c) prevent the natural within-model aggregation across (layer, head) instances of the same matchup, (d) prevent cross-model consensus from working uniformly (different models with different layer counts couldn't compare on per-(layer, head) granularity). The architecture template handles emit-time distribution from aggregated attestations back into the recipe's structural shape; no per-attestation context_id needed for tensor-calculation kinds.
- **Single-pass extraction without lottery-ticket sparsity.** Rejected per [R3](../../RULES.md) — flat thresholds destroy content; multi-pass lottery-ticket is the only correct filter shape. The static-mathematical validation third pass is open work per the R3/ADR 0007 amendment.
- **Run the model once on a probe set to validate sparsity retention.** Rejected — would require model invocation, contradicting [ADR 0055](0055-static-structural-parse-exploded-view.md). Static-mathematical validation is the substrate-native alternative.
- **Decompose models layer-by-layer at runtime as queries arrive (lazy ingest).** Rejected — ingest is a one-time deterministic ETL into substrate state per [ADR 0006 sibling artifacts pattern](0006-perfcache-and-db-seed-siblings.md) + [DESIGN III three-phase architecture](../../DESIGN.md). Lazy ingest would couple query latency to ingest cost and break cross-source consensus accumulation.

## References

- [RULES R3](../../RULES.md) — lottery-ticket-aware sparsity (third pass needs reframing under this ADR)
- [RULES R4](../../RULES.md) — sparse-by-construction emission (downstream of this ADR's output)
- [RULES R5](../../RULES.md) — attestation idempotency
- [RULES R6](../../RULES.md) — DB as dumb columnar store
- [RULES R7](../../RULES.md) — determinism by construction (static ETL preserves determinism naturally)
- [RULES R8](../../RULES.md) — no GPU at runtime (the probe exception is stale under this ADR)
- [RULES R10](../../RULES.md) — polymorphic plugin architecture
- [RULES R14](../../RULES.md) — C ABI at engine boundaries
- [RULES R16](../../RULES.md) — separation of concerns (engine: math; orchestration: C#)
- [STANDARDS "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS Testing](../../STANDARDS.md)
- [GLOSSARY Glicko-2](../../GLOSSARY.md) — *"rate every attestation — the substrate's analog of weight magnitude in an AI model"* (per [SUBSTRATE-FOUNDATION truth 2](../SUBSTRATE-FOUNDATION.md): a weight magnitude is one match *outcome*, NOT a rating/prior; the substrate stores the emergent consensus, never a scaled weight)
- [GLOSSARY Arena](../../GLOSSARY.md) + [GLOSSARY Arena Semantics](../../GLOSSARY.md)
- [GLOSSARY Effective Mu](../../GLOSSARY.md)
- [GLOSSARY Universal T0](../../GLOSSARY.md) — pixels-are-entities + audio-is-entities + text-is-entities unification
- [GLOSSARY Lottery-Ticket-Aware Sparsity](../../GLOSSARY.md) — third pass reframing per this ADR
- [GLOSSARY Vampire mode / Food principle](../../GLOSSARY.md) — the *mechanism* these labels stand for: dissolve the model to semantic facts and discard the weight bytes (bit-perfect preservation is worthless per [SUBSTRATE-FOUNDATION truth 6](../SUBSTRATE-FOUNDATION.md)). Per truth 10 the cute names are a tell, not a concept — state the mechanism, not the label.
- [GLOSSARY ModelDecomposer](../../GLOSSARY.md)
- [DESIGN.md VII — Lottery-ticket-aware sparsity](../../DESIGN.md)
- [DESIGN.md VIII — Recipe extraction + custom synthesis recipes](../../DESIGN.md)
- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perfcache + DB seed sibling pattern (model ingest is the second-order analog: each model's weight tensors and the substrate's accumulated attestation cloud are siblings derived from common ground)
- [ADR 0007](0007-lottery-ticket-aware-sparsity.md) — lottery-ticket-aware sparsity (third pass reframing surfaced here)
- [ADR 0009](0009-recipe-extraction-and-overrides.md) — recipe extraction
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture (architecture-template entities)
- [ADR 0016](0016-reusable-helpers-discipline.md) — DRY (this ADR is the one-algorithm-per-extraction-task realization for model ingest)
- [ADR 0024](0024-engine-modularization.md) — engine modularization (placement in liblaplace_synthesis)
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure
- [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md) — MKL + Spectra + TBB integration (matchup-space iteration is the hot-path numerical work)
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics + source-trust consensus (Glicko-2 accumulator)
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion + model-codec fidelity (probe-observation framing surfaced for amendment)
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (architecture-template entities bootstrap at Stage 4)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite ModelDecomposer (ContainerFormat × TensorDtypeDecoder × SemanticArchitectureDecomposer × ModalityBinder; this ADR is the SemanticArchitectureDecomposer's extraction algorithm)
- [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) — attestation-kind priors (NOTE: its kind-value-tier / source-trust-class *ladders* contradict [SUBSTRATE-FOUNDATION truth 5](../SUBSTRATE-FOUNDATION.md) and are flagged for amendment; trust is a self-tuning Glicko-2 value, not a class)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) — modality binder for text models
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — content-addresses entities the matchup observations reference
- [ADR 0049 SubstrateChange](0049-substrate-change-intent-type.md) — emission intent type
- [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md) — emission write surface
- [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — `Laplace.Decomposers.Model` plugin host
- [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) — orchestration (streaming WeightTensorETL output through SubstrateCRUD)
- [ADR 0053 perfcache compile-time build pipeline](0053-perfcache-compile-time-build-pipeline.md) — analogous compile-time build pattern (architecture-template entities are perfcache-style canonical content)
- [ADR 0054 selective deployment profiles](0054-selective-deployment-profiles.md) — model ingest is FULL_SERVER only
- [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) — container parsing precondition; this ADR consumes the exploded view's tensor blobs
- Closed planning issues #223 (probe-set design) + #224 (GPU probe driver coordination) — superseded by this ADR
- Conversation 2026-05-24: *"we ETL the knowledge out of the AI model packaging"* + *"we know from a layer and its token indexes that these tokens... would get these attestations if queried normally"* + *"attention, ffn, vision, mlp, convolution, diffusion, etc... doesnt matter"* + *"unicode leafs to entities means pixels are entities, audio is an entity, text is an entity"*
