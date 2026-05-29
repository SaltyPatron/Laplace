# ADR 0043: Composite decomposer architecture (ModelDecomposer worked example)

## Status

**Accepted** — 2026-05-23

## Context

[ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) established that each `IDecomposer` plugin's scope is its **domain's full data ecosystem**. For simple domains (Atomic2020, Tatoeba) a monolithic decomposer suffices. For complex domains (AI models, multilingual lexical resources, parsable code) the data varies along multiple **orthogonal axes** that don't compose into a single linear ingestion path:

- **AI models** vary by container format (safetensors / GGUF / ONNX / PyTorch / TF), tensor dtype + quantization (FP32 / FP16 / BF16 / FP8 / Q4_K_M / Q8_0 / GPTQ / AWQ / ...), semantic architecture (Llama / Mistral / Qwen / Phi / Mamba / Diffusion / ViT / MoE / encoder-decoder / CNN), and input modality (text / image / audio / multimodal). A monolithic `TransformerModelDecomposer` collapses all these axes into one plugin and forces every combination through the same code path.
- **Universal Dependencies** ships as 250+ treebanks across ~140 languages, with per-treebank conventions for XPOS tagsets, morphological features, and dependency labels. A monolithic `UDDecomposer` would need a per-treebank switch tower.
- **Wiktionary** has per-language-edition XML schemas (the English dump's section structure differs from the French / German / Chinese editions); content sections (Definition / Etymology / IPA / Inflections / Translations) need distinct parsers.

A monolithic decomposer per these domains either (a) forces all variations through one code path with conditional logic everywhere, or (b) requires a separate top-level plugin per variation (re-fragmenting what ADR 0041 was meant to consolidate).

## Decision

Decomposers are **composite by default for multi-axis domains**, monolithic only when the domain has no orthogonal variation. The composition has a stable shape with four (or fewer, as applicable) plugin axes:

### Four-axis composition (ModelDecomposer worked example)

```
ModelDecomposer<ContainerFormat>
    ├── ContainerFormat<T>                       ─ parameter
    │      parses bytes-on-disk → enumerable
    │      (tensor_name, shape, dtype, raw_bytes,
    │       recipe_metadata)
    │
    ├── TensorDtypeDecoder<Dtype>                ─ composed
    │      decodes raw tensor bytes → canonical numerical form
    │
    ├── IArchitectureTemplate<Family>   ─ composed
    │      maps tensor names → (layer, head, computational role)
    │      knows what tensors mean in this architecture
    │
    └── ModalityBinder<Modality>                 ─ composed
           supplies substrate entities to bind tensor calculations against
```

Plugin axes:

**1. ContainerFormat** (parameter `T` to ModelDecomposer):
- `SafetensorsContainer`, `GGUFContainer`, `ONNXContainer`, `PyTorchContainer` (pickle .pt/.bin), `TensorFlowSavedModelContainer`.
- Each provides: parse header / metadata, enumerate tensors with names + shapes + dtypes + raw-byte regions, expose a tensor-accessor API.

**2. TensorDtypeDecoder** (composed; one per dtype encountered in the model):
- `FP32Decoder`, `FP16Decoder`, `BF16Decoder`, `FP8_E5M2Decoder`, `FP8_E4M3Decoder`.
- `INT32Decoder`, `INT16Decoder`, `INT8Decoder`, `UINT8Decoder`.
- `Q4_0Decoder`, `Q4_K_MDecoder`, `Q5_0Decoder`, `Q5_K_MDecoder`, `Q6_KDecoder`, `Q8_0Decoder`, `Q8_KDecoder` (GGUF quantization schemes).
- `BNB_NF4Decoder`, `GPTQDecoder`, `AWQDecoder`, `EXL2Decoder` (other quantization schemes).
- Each decodes raw bytes → canonical FP32 (or whatever the substrate's canonical numerical form is — TBD per attestation arena policy).

**3. IArchitectureTemplate** (composed; one per architecture family — the existing plugin interface from [ADR 0011](0011-polymorphic-plugin-architecture.md), used bidirectionally):
- `LlamaTemplate` (Llama / Mistral / Qwen / Phi / Gemma / TinyLlama — all share the same transformer-block structural pattern with per-vendor parameter differences).
- `MoETransformerTemplate` (Mixtral / Qwen3-MoE / DeepSeek-V2 / DeepSeek-V3 — adds a router + expert MLPs).
- `MambaTemplate` (state-space matrices A / B / C / D + conv + projection).
- `DiffusionTemplate` (U-Net / DiT variants).
- `VisionTransformerTemplate` (ViT / CLIP-vision / DINO).
- `EncoderDecoderTemplate` (T5 / BART / mBART / MarianMT).
- `CNNTemplate` (ResNet / EfficientNet / ConvNeXt).

Each template is a **recipe** (per GLOSSARY "Recipe"): a structural pattern + parameter slots (hidden_dim / num_layers / num_heads / num_kv_heads / vocab_size / intermediate_size / RoPE_theta / activation / etc.).

Each template is **bidirectional**:
- **Ingest direction**: given a model file + parameter values, the template determines what each tensor MEANS mechanically (Q projection at layer L head H; gate projection at layer L; etc.). The template does NOT carry a hardcoded vendor-naming-convention map — that would put the map outside the Merkle DAG (un-queryable, un-rateable, un-mergeable). Instead, **vendor naming conventions are themselves substrate content**: the substrate accumulates typed attestations of kind `TENSOR_NAME_MEANS_MECHANICAL_ROLE` (or similar) between text entities (the vendor's tensor name, e.g., `"model.layers.0.self_attn.q_proj.weight"`) and mechanical-role kind entities (e.g., `Q_PROJECTS`), sourced to the vendor (e.g., `Meta_Llama_naming_convention_source` or `Mistral_AI_naming_convention_source`) and rated by Glicko-2 like any other attestation. The ingestion path queries this attestation graph to resolve vendor tensor names → mechanical roles; novel names trigger either consensus inference (shape + position + usage pattern) or honest abstention.
- **Synthesis direction**: given a recipe (template + parameter values + knowledge scope), the template knows what tensors need to be emitted, in what shape, populated from which substrate attestation kinds. Recipe parameters are user-authorable — the substrate can emit custom recipes that don't match any ingested vendor's shape (per RULES R4 + ADR 0009 + ADR 0010).

Cross-vendor consensus is built across the same mechanical-role attestation kinds while preserving source-scoped rows: Llama-derived `Q_PROJECTS(walk -> ed)` and Mistral-derived `Q_PROJECTS(walk -> ed)` remain separate `(subject, kind, object, source, context)` attestation states, and traversal/synthesis aggregates them through arena-aware effective mu because the kind is mechanical, not vendor-named. The substrate's view of vendor naming itself is also consensus: when two vendors agree on a name's meaning, those observations cluster; when they disagree, the dispute lives in substrate (`Llama_source attests "q_proj" means Q_PROJECTS`; `HuggingFace_transformers_default_source` attests same or different meaning with its own source-scoped rating), resolvable via arena semantics rather than hardcoded plugin overrides.

**4. ModalityBinder** (composed; one per input modality the model accepts):
- `TextModality` — tokenizer ingest (BPE / SentencePiece / WordPiece / TikToken); BPE markers stripped via canonicalization; vocab entries dedup against Unicode text entities.
- `ImageModality` — patch encoder; produces pixel / patch / region entities cross-referencing the Unicode-canonical text representation of color values (Universal T0).
- `AudioModality` — mel-spectrogram / discrete-token codec; produces audio frame / sample entities.
- `MultimodalModality` — composition for multimodal models (LLaVA, CLIP, etc.).

The `ModelDecomposer` itself is a **choreographer**: detect container (T) → enumerate tensors → for each tensor, decode bytes per dtype → route to architecture decomposer for semantic interpretation → stream significant cells as Glicko-2 matchup observations between substrate entities supplied by the modality binder, sourced to the model entity. This is a streaming O(params) ETL of the already-computed weight cells (per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 1) — never a GEMM-at-ingest recompute, never a vocab² matchup space, never a flat top-k that discards most of the model. Weights are dissolved into rated relationship facts and the blob is discarded; bit-perfect preservation is a non-goal (truths 2 + 6).

> **OPEN per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md):** for the directly token-anchored tensors (`embed_tokens` / `lm_head`) the cell → token-entity binding is real and cheap. For the **interior `d×d` tensors** (`q/k/v/o/gate/up/down`), *how each cell resolves to token entities without re-running the GEMM* is unsolved — to be pinned with Anthony. The choreography above describes container/dtype/role routing (settled plumbing); it does **not** settle interior cell → token resolution. The "supplies substrate entities to bind tensor calculations against" responsibility of `ModalityBinder` is therefore OPEN for interior tensors, not a closed mechanism. Do not read this ADR as asserting an answer there.

### Generalization to other domains

The pattern transfers:

- **UDDecomposer**`<Treebank>` parameterized over treebank ID (en_ewt, fr_gsd, zh_gsdsimp, ...) + composed of `CoNLLUParser` + `MorphologicalFeatureNormalizer` + `DependencyArcBuilder`.
- **WiktionaryDecomposer**`<LanguageEdition>` parameterized over which language edition's XML dump + composed of `XMLDumpParser` + per-section-type sub-decomposers (`DefinitionSectionDecomposer`, `EtymologySectionDecomposer`, `IPASectionDecomposer`, `InflectionsSectionDecomposer`, `TranslationsSectionDecomposer`).
- **TreeSitterDecomposer**`<Grammar>` parameterized over the 303 grammars + composed of `GrammarRuleExtractor` + `HighlightQueryParser` + (when ingesting code) `ParseTreeWalker`.
- **WordNetDecomposer** = composition (no parameter — single English WordNet) of `SynsetParser` + `SenseIndexer` + `GlossExtractor` + `RelationLinker` + `MorphExceptionLoader`.
- **OMWDecomposer**`<LanguagePack>` parameterized over which language's WordNet pack + composed of the same sub-parsers as WordNetDecomposer.

### What stays monolithic

Simple-shape decomposers where no orthogonal axis exists:
- **TatoebaDecomposer**: one ecosystem, one ingestion path (sentence dump + pair links + audio + speakers).
- **Atomic2020Decomposer**: single relation-triple format.
- **UnicodeDecomposer**: ecosystem is large but composes linearly (UCDXML → supplementary text → UCA → Unihan → emoji → segmentation auxiliary).
- **ISODecomposer**: similar (ISO 639-3 + 15924 + 10646 + BCP-47 + CLDR validity + IANA + LoC + SIL + Glottolog all consume linearly).

For ConceptNet (which has sub-source provenance per assertion), the sub-source provenance is *data*, not *plugin variation* — it goes in attestation rows (`source_id` of each assertion), not in a sub-decomposer plugin.

## Consequences

- AI model ingestion gets clean format/dtype/architecture/modality factorization. Adding GGUF support is one new ContainerFormat plugin, not a forked ModelDecomposer. Adding Q4_K_M support is one new TensorDtypeDecoder. Adding Mamba support is one new IArchitectureTemplate.
- UD treebanks become per-treebank parameterizations of one decomposer; per-treebank conventions become data passed to the sub-decomposers, not bespoke plugins.
- Wiktionary editions become per-edition parameterizations.
- Plugin surface increases (one composite top-level decomposer plus many sub-decomposers per axis) but each sub-decomposer is small + tightly focused.
- Issue tracking shifts: one top-level Decomposer issue per domain (already opened as #183–#191 + ISODecomposer pending) + one sub-issue per sub-decomposer plugin. Acceptance criteria for the composite decomposer = composition acceptance over its sub-plugins; acceptance for each sub-plugin = its specific axis's responsibility.

## Alternatives considered

- **One top-level plugin per (domain × variation) combination.** Rejected — re-fragments what ADR 0041 consolidated; massive plugin surface.
- **Monolithic decomposers with switch-on-variation logic.** Rejected — degrades into spaghetti as variations accumulate; testing matrix explodes.
- **Compositional pattern only for AI models; everything else monolithic.** Rejected — UD treebanks + Wiktionary editions + tree-sitter grammars need the same shape; cherry-picking AI models is inconsistent.

## References

- [ADR 0011](0011-polymorphic-plugin-architecture.md) — `IDecomposer` interface
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — Decomposer scope = full domain ecosystem (the top-level principle)
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered decomposer order
- [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) — weight-tensor ETL as streaming arena-matchup observation (the ingest mechanism this composite routes into)
- [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) — ratified conceptual core (streaming O(params) ETL; no GEMM-at-ingest; no bit-perfect goal; interior d×d cell → token resolution OPEN)
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — type + kind vocabulary
- [GLOSSARY.md](../../GLOSSARY.md) — "Decomposer", "Per-decomposer ecosystems", "TransformerModelDecomposer" (to be rewritten as ModelDecomposer<T>)
