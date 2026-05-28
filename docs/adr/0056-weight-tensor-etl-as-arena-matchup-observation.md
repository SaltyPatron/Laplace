# ADR 0056: Weight-tensor static ETL as arena-matchup observation — universal model-ingest extraction pattern

## Status

> **⚠ CORRECTION — 2026-05-28 (Anthony, authoritative; overrides the body below where they conflict):**
>
> An embedding is an **address book of token positions** — **geometry**, not knowledge. Each `embed_tokens` row is the model's positional view of an entity that **already exists** in the substrate (placed by Unicode + the linguistic ladder before any model is touched). It becomes a per-`(entity, source=model)` **PROJECTION physicality** (Procrustes-aligned onto the entity's canonical 4D frame); `lm_head` → **ProjectionOutput physicality**. **`EMBEDS` and `OUTPUT_PROJECTS` are NOT attestation kinds** — treating them as such was the category error. The relational knowledge a model contributes is the **distance between aligned token positions → a Glicko-2 attestation BETWEEN ENTITIES** (King↔Queen), commensurable across models because the edge is entity↔entity, not dim↔dim (this is the cross-model/dim/vocab consensus moat).
>
> The **family table below (≈lines 155-181) is the per-cell "HOW not WHICH" view this ADR's own *Alternatives considered* rejects** — the ADR contradicts itself, and that contradiction is the root cause of the model-ingest flip-flopping across sessions. The table's "object axis = `hidden_dim` / `intermediate_dim`" is the error: **there are no hidden-dim or intermediate-dim entities.** The embedding grounds the model's entire space in the token vocabulary, so **every interior tensor (`Q/K/V/O`, `GATES/UP/DOWN`) maps to token↔token attestations of its mechanical kind — uniformly, exactly like `Q_PROJECTS`.** Each token-pair is a Glicko-2 matchup observation; consensus forms across sources. Nothing here is "unsettled" — it all shakes out as attestations + Glicko-2 because everything maps to tokens. Embedding/lm_head → physicalities (above), the rest → token↔token Glicko-2 matchups.
>
> **Weights are NOT stored as entities and NOT bit-perfect (Vampire mode).** A weight-derived relationship between two entities (King↔Queen) is a **Glicko-2 matchup observation**, not a stored value: the weight is the match *outcome*, the model's source-trust is the *opponent* strength, and the substrate keeps only the **emergent consensus rating** (rating/RD/volatility, accumulated across every source that plays that matchup) — never the raw weight. Stamping a scaled weight straight into the rating column (the shipped `ScaleToRating(weight)` / per-cell-magnitude path in `WeightTensorETL`) IS the category error: `0.00098…` is one match result, meaningless alone. Consensus is what's stored; synthesis regenerates fresh weights *from* the consensus per recipe; the bytes never live in the substrate. Truths cluster, lies scatter. See [`docs/research/grounded-model-codec-foundation-2026-05-28.md`](../research/grounded-model-codec-foundation-2026-05-28.md).

**Proposed** — 2026-05-24
**Amended** — 2026-05-24 (same day): matchup-space-shape correction. Original draft estimated ~10¹³ pairs for a Qwen3-class model by treating per-(layer, head) cells as separate attestations + treating the tokenizer's full vocab as the entity space. Both wrong, per Anthony's 2026-05-24 correction: *"same content = same hash and how many times an AI model would project the same attestation."* The corrected framing:
- **Tokenizer aliases collapse to substrate entities** via [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) canonicalization. SentencePiece `▁walk` / GPT-2 BPE `Ġwalk` / WordPiece `##walk` / plain `walk` all share ONE Text entity. The 152K-vocab estimate becomes ~50–80K unique text entities per real-world model tokenizer.
- **Per-position layer/head attribution is recipe content, NOT per-attestation metadata.** Per [GLOSSARY Attestation Kind](../../GLOSSARY.md): *"Per-position attribution (which layer / which head / which tensor index a calculation lives at) is recipe content — captured in the model's recipe entity (text/JSON describing num_layers, num_heads, per-tensor token vocabularies, layout). The architecture template wires substrate's aggregated typed attestations into the recipe's structural shape at emit time; redundant per-position storage on attestations is not needed."*
- **Within-model aggregation across (layer, head, position) instances** of the same `(subject, kind, object)` matchup converges to ONE attestation row via Glicko-2 update per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) + [R5 attestation idempotency](../../RULES.md). The substrate stores one row per `(subject, kind, object, source, context)` tuple; with `context = NULL` for tensor-calculation kinds (per recipe-carries-position rule), this collapses dramatically.
- **Corrected scale**: unique_entity_pairs × num_mechanical_role_kinds × per-source ≈ ~50K × 50K × 10 ≈ ~10¹⁰ MAX per model before lottery-ticket sparsity, dropping to ~10⁷–10⁸ retained per model. Three orders of magnitude smaller than the original estimate.

The algorithm pseudocode + per-family registry table + performance bounds sections below have been edited in-place to reflect this. ADR-status workflow note: edits-in-place are appropriate for Proposed ADRs; once Accepted, future supersession follows the [ADR README workflow](README.md).

**Authors:** Anthony Hart

Replaces (closed as hallucinations): planning issues #223 (probe-set design per architecture family) and #224 (GPU probe driver coordination). Both presupposed probe-based ingest (Path A — run the model on input data, observe outputs/activations, extract attestations from observations) which contradicts [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) (*"No code is executed. No framework loader is invoked."*).

## Context

[ADR 0055](0055-static-structural-parse-exploded-view.md) locked the **container-ingest** posture: the substrate statically dissects safetensors / PyTorch pickle / ONNX / TensorFlow SavedModel / YOLO native / HDF5 / etc. into an exploded view of substrate entities + typed structural attestations. No code execution; no framework loader invocation.

What [ADR 0055](0055-static-structural-parse-exploded-view.md) doesn't specify: once the exploded view has surfaced the model's weight tensors, **how does the substrate get from a weight tensor to a Glicko-2-rated typed attestation between substrate entities?**

The 2026-05-24 conversation surfaced the canonical framing: *"We're not running the model, we're sparse recording... we ETL the knowledge out of the AI model packaging."* And: *"we know from a layer and its token indexes that these tokens we ingested which mapped to these entities would get these attestations if queried normally... high or low, etc... glicko-2 gets figured out for that and that attestation edge gets recorded... attention, ffn, vision, mlp, convolution, diffusion, etc... doesnt matter... unicode leafs to entities means pixels are entities, audio is an entity, text is an entity, etc."*

The architectural insight has three layers:

1. **An AI model is a frozen ledger of typed-arena-matchup outcomes computed during training.** Each weight tensor cell records the strength of one matchup between substrate entities. For transformer-family `q_proj` at layer L head H, the matchup is `Q_PROJECTS(token_i, token_j)`; the strength is `q_proj[i, :] · k_proj[j, :]ᵀ`. The architecture template knows the matchup type + math + matchup-space shape.

2. **Static computation from weights is the exhaustive equivalent of a probe.** *"these tokens... would get these attestations if queried normally"* — the matchup outcomes a probe would observe are the same outcomes the substrate computes directly from `T's_math(i, j)` on the static weights. The substrate skips the input/output indirection (and the GPU/loader/sampler overhead) and reads the matchup at the layer the model already stored it.

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
    # Phase 1: per-tensor matchup observation
    # For each tensor, statically compute per-(i, j) matchup strength.
    # Within-model (layer, head, expert) instances of the same
    # (subject, kind, object) matchup get aggregated in Phase 2.
    # Per-position attribution is RECIPE CONTENT per GLOSSARY Attestation Kind,
    # NOT per-attestation context. (Was wrong in original draft.)
    per_tensor_observations = {}
    for each tensor T in tensors:
        spec = arch_template.spec_for(T.name)
        # spec carries:
        #   kind_id           — substrate kind entity (Q_PROJECTS / GATES / CONVOLVES_OVER / etc.)
        #   matchup_space     — pair shape: (subject_axis, object_axis); no per-position context axis
        #   math_function     — how to compute matchup_strength(i, j) from T's cells
        #   subject_modality, object_modality
        #   tensor_to_recipe_slot — for the recipe entity to record this T's (layer, head,
        #                           position) layout for emit-time distribution; NOT a
        #                           per-attestation context

        subject_map = modality_bindings[spec.subject_modality]
        object_map  = modality_bindings[spec.object_modality]

        # Block-by-block matchup computation for memory-bounded streaming
        # per tracking issue #222
        for each (i, j) in spec.matchup_space:
            matchup_strength = spec.math_function(T, i, j)
            # Within-tensor only; cross-tensor aggregation in Phase 2
            per_tensor_observations[(T.name, i, j)] = matchup_strength

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

    # Phase 5: observation emission (one attestation per unique (subject, kind, object))
    for (subject_id, kind_id, object_id), aggregate in validated.items():
        (rating_prior, rd_prior, vol_prior) = glicko2_prior_for(
            kind_tier   = arch_template.tier_for(kind_id),  # per ADR 0044
            trust_class = model_entity.trust_class)         # per ADR 0044 (typically tier 7 AI Model)

        initial_rating = scale_aggregated_strength_into_rating(
            aggregate.strength_summary,  # aggregated across all in-model instances
            rating_prior)

        yield AttestationRow {
            subject_id    = subject_id,
            kind_id       = kind_id,
            object_id     = object_id,
            source_id     = model_entity.id,
            context_id    = NULL,            # per-position attribution is recipe content,
                                              # NOT per-attestation context (per GLOSSARY
                                              # Attestation Kind)
            rating        = initial_rating,
            rd            = rd_prior,
            volatility    = vol_prior,
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

| Architecture family | Tensor type | spec.kind_id | spec.matchup_space | spec.math_function |
|---|---|---|---|---|
| Transformer attention | every `q_proj` × every `k_proj` (each layer × head pair) | `Q_PROJECTS` | (text_entity, text_entity) — no per-position context per amendment | `q_proj[i, :] · k_proj[j, :]ᵀ` per (layer, head) instance; aggregate across all in-model instances via Phase 2 |
| Transformer attention | `v_proj` (each layer × head) | `V_PROJECTS` | (text_entity, hidden_dim) | per-cell magnitude; aggregated across in-model instances |
| Transformer attention | `o_proj` (each layer × head) | `O_PROJECTS` | (hidden_dim, text_entity) | per-cell magnitude; aggregated |
| Transformer FFN | `gate_proj` (each layer) | `GATES` | (text_entity, hidden_dim) | SiLU/SwiGLU formulation per recipe; aggregated across layers |
| Transformer FFN | `up_proj` (each layer) | `UP_PROJECTS` | (text_entity, intermediate_dim) | per-cell magnitude; aggregated |
| Transformer FFN | `down_proj` (each layer) | `DOWN_PROJECTS` | (intermediate_dim, text_entity) | per-cell magnitude; aggregated |
| Transformer embedding | `embed_tokens` | `EMBEDS` | (text_entity, embed_dim) | per-cell magnitude (one instance only) |
| Transformer LM head | `lm_head` | `OUTPUT_PROJECTS` | (hidden_dim, text_entity) | per-cell magnitude (one instance only) |
| Transformer norm | `*_norm.weight` (each layer) | `NORMALIZES` | (hidden_dim,) — unary | per-cell magnitude; aggregated across layers |
| **MoE routing** | `gate.weight` (each layer) | `ROUTES_TO_EXPERT` | (text_entity, expert_entity) | routing-logit magnitude; aggregated across layers |
| **MoE expert FFN** | `experts[E].w1/w2/w3` (each layer × each expert) | `EXPERT_GATES / EXPERT_UP_PROJECTS / EXPERT_DOWN_PROJECTS` | (text_entity, hidden_dim) per expert | per-cell magnitude; aggregation strategy per the MoE-per-expert-aggregation ADR (tracking #223) |
| MoE shared expert | `shared_expert.*` (each layer) | `SHARED_EXPERT_*` | (text_entity, hidden_dim) | per-cell magnitude; aggregated across layers |
| **MLA (DeepSeek-V3)** | `q_a_proj` / `q_b_proj` (each layer × head) | `LATENT_Q_PROJECTS` | (text_entity, latent_dim) | per-cell magnitude × decompression math; aggregated |
| **MLA** | `kv_a_proj_with_mqa` / `kv_b_proj` (each layer) | `LATENT_KV_PROJECTS` | (text_entity, latent_dim) | per-cell magnitude × decompression math; aggregated |
| **Mamba SSM** | `A_log` / `D` / `dt_proj` / `x_proj` (each layer) | `SSM_TRANSITIONS` / `SSM_INPUT_PROJECTS` / `SSM_OUTPUT_PROJECTS` | (text_entity, state_dim) or (text_entity, text_entity via state) | discretized SSM update math; aggregated |
| **Vision ViT** | `patch_embed.proj.weight` | `PATCH_EMBEDS` | (patch_entity, feature) | conv-style or linear projection per recipe (one instance) |
| **Vision ViT attention** | `q_proj` × `k_proj` (each layer × head) | `Q_PROJECTS / K_PROJECTS / V_PROJECTS` (same as text) | (patch_entity, patch_entity) | identical math to text-transformer; aggregated across in-model instances |
| **Vision CNN** | `conv*.weight` (4D: out_channels × in_channels × kH × kW; each layer) | `CONVOLVES_OVER` | (input_region_pattern_entity, output_channel_entity) | kernel-weighted region sum; aggregated across layers |
| **Audio STFT projection** | `stft.weight` or analogous (each layer) | `STFT_PROJECTS` | (audio_frame_entity, freq_band_entity) | per-cell magnitude × FFT-bin math; aggregated |
| **Audio attention** | `q_proj` × `k_proj` (each layer × head) | `Q_PROJECTS / K_PROJECTS / V_PROJECTS` | (audio_frame_entity, audio_frame_entity) | same math as text; aggregated |
| **Diffusion U-Net** | conv kernels at each resolution × each layer | `CONVOLVES_OVER` (image-modality variant) | (pixel_region_entity, feature_channel_entity) | kernel-weighted region sum; aggregated across resolutions |
| **Diffusion U-Net cross-attn** | cross-attention `q_proj`/`k_proj`/`v_proj` (each block) | `CROSS_ATTENDS_CONDITION` + Q/K/V variants | (image_region_entity, condition_token_entity) | cross-modal — naturally falls out per pixels-are-entities; aggregated |
| **Encoder-decoder cross-attn** (Florence-2, Grounding-DINO) | cross-attention projections (each block) | `CROSS_ENCODER_DECODER_ATTENDS` + Q/K/V variants | (encoder_output_entity, decoder_token_entity) | cross-modal natural fall-out; aggregated |

The table above is **data registered on architecture-template entities**, not code. Adding a new architecture family = register its specs as meta-attestations on a new architecture-template entity. The `WeightTensorETL` algorithm itself doesn't change.

**Per-(layer, head, position) layout is recorded on the recipe entity** for emit-time distribution per the architecture template, NOT as per-attestation context_id. The recipe carries num_layers / num_heads / per-tensor token vocabularies / etc. per [ADR 0009 recipe extraction](0009-recipe-extraction-and-overrides.md) + [GLOSSARY Recipe](../../GLOSSARY.md). At synthesis time, the architecture template uses the recipe + the aggregated attestations to distribute values back into the recipe's structural shape (inverse of Phase 2 aggregation).

### Multimodal models fall out naturally

For Qwen3-VL (vision + text) the per-layer attention spec registers:

```text
attn.q_proj at layer L head H:
    kind_id = Q_PROJECTS
    matchup_space = (mixed_modal_token, mixed_modal_token)
    subject_modality = MIXED  # vision tokens + text tokens both present
    object_modality  = MIXED
```

When the modality bindings carry both `text_token_vocab` (per-vocab-entry mapping; collapses to ~50K unique substrate text entities via [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) canonicalization — see Status amendment above) + `vision_patch_vocab` (per-patch substrate entities), the matchup-space iteration naturally produces:

- `Q_PROJECTS(text_token_walk, text_token_cat)` — text-text matchup
- `Q_PROJECTS(text_token_walk, vision_patch_of_dog)` — cross-modal matchup
- `Q_PROJECTS(vision_patch_of_dog, vision_patch_of_cat)` — vision-vision matchup
- `Q_PROJECTS(vision_patch_of_dog, text_token_dog)` — cross-modal matchup

All in the same emission pass; no special multimodal codepath. The substrate's typed-attestation graph naturally accommodates this because every entity hash references one entity regardless of modality.

### Cross-source consensus is the Glicko-2 emergent property

Each model's emitted attestations land per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) + [ADR 0044 priors + trust-class taxonomy](0044-attestation-kind-priors-and-source-trust-taxonomy.md). When N models all observe the same `(subject, kind, object, context)` tuple with their own source_id, the substrate's arena-aware Glicko-2 accumulator (`laplace_glicko2_accumulate` aggregate per Story 5.6 / #68, commit `f002e7d`) converges on a cross-source consensus rating weighted by:

- Per-source trust class (per ADR 0044 Part B — typically tier 7 for AI Model)
- Per-kind value tier (per ADR 0044 Part A — typically tier 9 Tensor-Calculation for matchup kinds)
- Arena policy (per ADR 0036 — multi-valued compatible for Q_PROJECTS attestations across models that all agree token X attends to token Y)
- Source-lineage correlation (per ADR 0036 — co-trained-from-same-base models are not independent observations)

The "single-model-probe trust" framing in ADR 0044 T9 *"cluster across many models for higher weight"* now reads literally: cross-source accumulation IS the consensus mechanism. No special multi-source codepath; it's the substrate's standard arena update.

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

- Tokenizer vocab ≈ 152K entries; after tokenizer **surface** strip (`Ġ` / `▁` / `##` per [ADR 0043](0043-composite-decomposer-architecture.md)) and mapping to **observed** substrate text ([ADR 0047](0047-text-decomposer-pure-primitive.md) — no NFC at ingest), many token surfaces alias to the same text entity (`King` vs `ĠKing` → one text entity `King` with separate token entities). Take ~50K unique substrate text entities as a working estimate.
- **Phase 1** (per-tensor matchup computation): ~50K × 50K × 60 layers × 32 heads × Q_PROJECTS-variants ≈ ~10¹⁰ per-tensor matchup outcomes for attention path alone. Each is one FP64 multiply-accumulate; block-by-block computation per [tracking issue #222](https://github.com/SaltyPatron/Laplace/issues/222) keeps RAM bounded.
- **Phase 2** (within-model aggregation collapses (layer, head) instances): the same (subject_entity, Q_PROJECTS, object_entity) matchup contributes from up to 1,920 (60 × 32) per-tensor instances. After aggregation: ~50K × 50K × num_attention_role_kinds (5: EMBEDS / Q+K+V+O / GATES / UP / DOWN / NORMALIZES / OUTPUT — count varies per architecture) ≈ ~10¹⁰ aggregated `(subject, kind, object)` triples per model. MoE adds the routing arena (one set per layer per expert-cluster, also aggregated within model).
- **Phase 3** (lottery-ticket sparsity at the aggregate level): retain ~0.001%–0.01% of aggregated matchups → **~10⁵–10⁷ attestation rows per model**. Bulk-COPY tractable per [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md); fits within sub-GB attestation-table growth per ingested model.
- Streaming output to `SubstrateChange` intent batches per [ADR 0049](0049-substrate-change-intent-type.md), one intent per arena (per kind × per source) chunk.
- `SubstrateCRUD.ApplyStreamAsync` per [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) consumes the stream + bulk COPYs the retained attestations.

**Original draft's ~10⁸–10⁹-per-model estimate was three orders of magnitude too high** because it (a) treated tokenizer aliases as distinct entities, and (b) treated each (layer, head) cell as a distinct attestation. Correcting both via the amendment above brings the realistic per-model retained-attestation count into the 10⁵–10⁷ range. For a 100-model substrate ingest at this corrected scale: ~10⁷–10⁹ total attestation rows across all source models — well within PG's row-handling capacity for the substrate's ~10⁹–10¹⁰ design target per [DESIGN.md I](../../DESIGN.md).

For frontier-scale (Qwen3 480B, DeepSeek 3.2 Speciale, Llama4 Maverick): the per-tensor matchup-space scales with parameter count, but the aggregated unique-entity-pair × kind matchup count is bounded by the tokenizer's unique-entity space (which doesn't scale with parameter count — same ~50–150K vocab range for frontier transformers). Lottery-ticket retention scales sub-linearly. Streaming-ingest discipline per [tracking issue #222](https://github.com/SaltyPatron/Laplace/issues/222) keeps memory bounded; CPU-side static computation is parallelizable per oneTBB per [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md).

**No GPU at any point in this algorithm** — all work is static arithmetic over weight tensors that already live as mmap'd FP arrays per `TensorDtypeDecoder` output. CPU-native end-to-end.

## Consequences

- **One extraction algorithm**, reused across every model decomposer (transformer / MoE / MLA / Mamba / Diffusion / Vision / Audio / Encoder-Decoder / CNN / future). Bug fix in `WeightTensorETL` applies uniformly.
- **Adding a new architecture family = registering data** on a new architecture-template entity (per-tensor spec rows). No new code path. No new tests beyond per-family integration tests.
- **Multimodal models work without special codepaths.** Cross-modal entities are still substrate entities; cross-modal attestations are natural fall-outs of the matchup-space iteration.
- **Cross-source / cross-architecture / cross-modal consensus is one mechanism**: the substrate's arena-aware Glicko-2 accumulator (already shipped as `laplace_glicko2_accumulate`) handles it. Synthesis emitting a custom-recipe model draws on the accumulated cross-source attestation graph naturally.
- **No model invocation at any point**, no framework loader, no GPU at ingest time, no probe-set design needed, no probe driver coordination needed. The closed tracking issues #223 + #224 are replaced by this ADR (and by the architecture-family vocabulary extensions tracked in #221, which becomes a per-family worked-example follow-on to this ADR).
- **The lottery-ticket third pass needs reframing.** [RULES R3](../../RULES.md) + [ADR 0007](0007-lottery-ticket-aware-sparsity.md) currently specify "probe-validated retention test" which presupposes running the model. Under this ADR's posture, that third pass is static-mathematical validation (spectral preservation; singular-value retention; matchup-distribution preservation between sparse and dense subgraphs). R3 + ADR 0007 need amendment — surfaced separately as a doc-amendment item per [R12](../../RULES.md).
- **R8's "GPU at probe time" exception is stale.** With no probe forward pass at ingest, the GPU exception clause has no use case. Surfaced separately as a doc-amendment item.
- **GLOSSARY's "Probe (in ingestion context)" entry is stale.** Same reason. Either delete or convert to a forbidden-historical-pattern reference in the Anti-vocabulary section.
- **ADR 0036 trust class list item 6, ADR 0037 "probe observations" mention, ADR 0043 ModelDecomposer probe-observation thread, ADR 0044 trust class tier 7 naming "AI Model Probe", DESIGN VII probe-observation framing** — all carry the stale probe-framing and need amendments (`probe observation` → `weight-cell matchup observation`). Surfaced separately as doc-amendment items.
- **The architecture-template entity becomes substrate content**, not code. Per-family specs are meta-attestations queryable + extensible without code changes. Aligns with the substrate-content-as-substrate-knowledge principle running throughout the architecture (per [ADR 0011 polymorphic plugins](0011-polymorphic-plugin-architecture.md)).

## Alternatives considered

- **Probe-based attestation extraction (Path A).** Rejected per the 2026-05-24 conversation + per the contradiction with [ADR 0055 static structural parse](0055-static-structural-parse-exploded-view.md). The substrate doesn't load + doesn't execute models; probes presuppose both.
- **Per-architecture-family bespoke extraction code.** Rejected per [STANDARDS Reusable helpers](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md). Duplication anti-pattern. N architectures × bespoke extraction = N drift surfaces.
- **Per-cell extraction (attest every non-zero weight as its own attestation) instead of per-matchup extraction.** Rejected — the per-cell view encodes HOW a token projects but not specifically WHICH other tokens it attends to. The per-matchup view (`Q_PROJECTS(token_X, token_Y) = q_proj[X,:] · k_proj[Y,:]ᵀ`) is the actual semantic relationship + composes naturally via Glicko-2 across models. Per-cell would require a separate join step at query time to reconstruct the matchup.
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
- [GLOSSARY Glicko-2](../../GLOSSARY.md) — *"rate every attestation — the substrate's analog of weight magnitude in an AI model"* (this ADR makes that analogy literal: weight magnitude → Glicko-2 prior)
- [GLOSSARY Arena](../../GLOSSARY.md) + [GLOSSARY Arena Semantics](../../GLOSSARY.md)
- [GLOSSARY Effective Mu](../../GLOSSARY.md)
- [GLOSSARY Universal T0](../../GLOSSARY.md) — pixels-are-entities + audio-is-entities + text-is-entities unification
- [GLOSSARY Lottery-Ticket-Aware Sparsity](../../GLOSSARY.md) — third pass reframing per this ADR
- [GLOSSARY Vampire mode](../../GLOSSARY.md) — drain attestations, discard weight bytes; this ADR specifies HOW to drain
- [GLOSSARY Food principle](../../GLOSSARY.md)
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
- [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) — kind-value tiers + source-trust-class taxonomy (Glicko-2 prior derivation)
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
