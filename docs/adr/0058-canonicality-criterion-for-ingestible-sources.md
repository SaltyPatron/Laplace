# ADR 0058: Canonicality criterion for ingestible sources — substrate ingests canonical; derived/lossy is emit-only

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

Tracks planning issue #219.

## Context

The 2026-05-24 conversation surfaced the substrate's binary stance on file-format canonicality:

> *"We dont care about quantized 'mp3' models... we care about safetensors... the real knowledge that isnt compacted, compressed, lost, etc. we EXPORT those because we can. We arent ingesting AWQ and GGUF for example. We DO ingest the yoloscript or whatever those file types are... if they are 'the developer version', we ingest it... follow?"*

[GLOSSARY Canonicalization](../../GLOSSARY.md) codifies the entity-level rule:

> *"The type-specific, **lossless** normalization applied to raw input bytes before hashing. Different encodings that decode to the same canonical content yield the same entity ID — UTF-8 vs UTF-16 of identical codepoint sequences; PNG vs lossless WebP of identical pixel grids; FLAC vs WAV of identical PCM samples. **Lossy conversions are NOT equivalent under canonicalization** — a JPEG is a different entity from the PNG it was derived from (different pixel values after decode); an MP3 is a different entity from a FLAC of 'the same audio' (different PCM); a quantized GGUF is a different entity from the safetensors it was derived from (different tensor values). Cross-format equivalence between lossy variants is an attestation (e.g., `IS_LOSSY_ENCODING_OF`), not an identity collapse."*

What's missing: the **decision rule** that bridges from this entity-level canonicalization invariant to the **ingest-scope policy** — when a per-source `IDecomposer` per [ADR 0051](0051-idecomposer-csharp-plugin-contract.md) encounters an artifact, how does it decide whether to ingest it as a knowledge source or refuse it (and what counts as refusal)?

Without this ADR, every per-source decomposer (especially `ModelDecomposer` per [ADR 0043 composite architecture](0043-composite-decomposer-architecture.md), where the AWQ/GPTQ/EXL2/BNB_NF4/GGUF ecosystem creates the most concrete decision pressure) invents its own canonicality heuristic. Drift: one decomposer might treat AWQ as ingestible-with-lower-trust; another might silently fall back when canonical isn't available; a third might lose attestation provenance from derived artifacts entirely. The same architectural drift the substrate-emission-discipline ADR ([ADR 0057](0057-substrate-emission-discipline-product-not-packaging.md)) just locked at the EMIT boundary needs a complementary lock at the INGEST boundary.

The canonicality question is also asymmetric across source classes:

- **Linguistic resources** (WordNet 3.0, OMW packs, UD treebanks, Wiktionary XML dumps, Tatoeba sentences, ConceptNet JSON-LD, Atomic2020 TSV): each source has ONE canonical form — the publisher's published artifact. No "lossy AWQ-equivalent" exists for `data.noun` or for a CoNLL-U file. The canonicality criterion is trivial for these.
- **Image / audio corpora**: lossless formats (PNG, lossless WebP, FLAC, WAV, AIFF) are canonical; lossy formats (JPEG, lossy WebP, lossy AVIF/HEIC, MP3, Opus at lossy bitrates, AAC) are derived. The canonicality criterion is concrete + well-known.
- **AI models**: this is where the criterion gets most concrete + most consequential. The publisher (HuggingFace / model author) typically publishes fp32 or bf16 safetensors as the canonical artifact; the community produces AWQ / GPTQ / EXL2 / BNB_NF4 / GGUF derivatives for runtime efficiency. The substrate ingests the canonical safetensors; the derivatives are emit-only via [`IFormatWriter`](0011-polymorphic-plugin-architecture.md) per [ADR 0057 emission discipline](0057-substrate-emission-discipline-product-not-packaging.md).
- **Code / document corpora**: text source is canonical; processed/minified/compiled artifacts are derived. The canonicality criterion is the source vs the build output distinction.

This ADR codifies the criterion universally + specifies decomposer responsibility + specifies how the substrate records the existence of derived artifacts without ingesting their content.

## Decision

**The substrate ingests canonical artifacts as knowledge sources. Derived/lossy artifacts are emission targets, NOT ingestion sources. When a derived artifact appears in an ingestion context, the substrate records its EXISTENCE via `IS_LOSSY_ENCODING_OF` attestations to its canonical source (if known), but does NOT extract knowledge from its content.**

### The canonicality criterion (concrete heuristics)

For any artifact a per-source decomposer encounters, the criterion to classify it as CANONICAL or DERIVED:

| Heuristic | If TRUE → indicator | If FALSE → indicator |
|---|---|---|
| **(1) Origin** | Published by the upstream author at their canonical distribution (HuggingFace model card, official git repo, Princeton WordNet release, Wikimedia Wiktionary dump, etc.) | Published by a community-converted derivative redistribution → DERIVED |
| **(2) Precision** | Full precision for the source's domain (fp32 / bf16 / fp16 for AI weights; PNG / FLAC for image / audio; UTF-8 source text for linguistic; Python `.py` source for code) | Reduced precision (4-bit / 5-bit / 6-bit / 8-bit quantization for AI weights; JPEG / MP3 for image / audio; compiled `.pyc` / minified `.js.min` for code) → DERIVED |
| **(3) Calibration step** | No calibration data baked in; the artifact is the model author's training output as-is | Has a calibration step / dataset baked in (AWQ's activation calibration; GPTQ's gradient samples; EXL2's measurement passes) → DERIVED |
| **(4) Roundtrippability** | Can be losslessly reconstructed to the publisher's intended bit-pattern | Lossy conversion produced it from a canonical source → DERIVED |
| **(5) Format ecosystem role** | Listed as canonical in the format's own ecosystem (safetensors per HF docs; FLAC per audio communities; PNG per W3C) | Listed as a runtime / compatibility / deployment optimization layer (GGUF per llama.cpp; AWQ per the AWQ paper; ONNX where shipped as a downstream conversion from a canonical PyTorch checkpoint) → DERIVED |

An artifact is CANONICAL when heuristics (1)+(2)+(4) all hold (with (3) and (5) as supporting evidence). When any of (1), (2), or (4) fails, the artifact is DERIVED.

### Per-format classification matrix

The categorization the substrate operates against:

| Format / class | Class | Ingest? | Emit? | Notes |
|---|---|---|---|---|
| **safetensors** (fp32 / bf16 / fp16, HuggingFace-published) | CANONICAL | yes | yes (native) | Substrate's native AI-model package shape per [R4](../../RULES.md). |
| **PyTorch `.pt` / `.pth`** (developer checkpoint `state_dict`) | CANONICAL | yes | optional | Developer version, full precision. Per [ADR 0055 exploded view](0055-static-structural-parse-exploded-view.md): parsed via PEP-3154 opcode static parse (no execution). Pickle protocol does not constitute "lossy"; opcode parsing recovers structural metadata identically across PyTorch versions for the same checkpoint. |
| **TensorFlow SavedModel** | CANONICAL | yes | optional | Developer version: graph + variables + signatures, the original-publication form for TF-native models. |
| **ONNX (when shipped by the model author)** | CANONICAL-ish | yes (with provenance check) | yes (compat) | Distinguish dev-shipped ONNX from a downstream compatibility conversion. The dev-shipped form is ingest scope; the derived form is emit-only. The decomposer applies heuristic (1) + checks the model card. |
| **YOLO `.pt` + `.yaml`** (Ultralytics dev format) | CANONICAL | yes | optional | "Yoloscript or whatever those file types are" per the 2026-05-24 conversation — the developer version. `/vault/models/yolo11x` is exactly this. |
| **Framework-native developer formats** (Florence-2 native; SAM native; Whisper native; vendor-specific .bin formats; Mamba SSM official releases) | CANONICAL | yes | per-format | If the model author ships it as the source of truth, it's in scope. |
| **GGUF** (per-quant Q4_K_M / Q5_K_M / Q6_K / Q8_0 / etc.) | DERIVED / lossy | **NO** | yes (proof / compat) | llama.cpp runtime / deployment optimization. Per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) GGUF is the substrate's proof export shape for chat-verification round-trips; never an ingest source. |
| **AWQ** (Activation-aware Weight Quantization, 4-bit) | DERIVED / lossy | **NO** | yes (compat) | Activation calibration step baked in → fails heuristic (3). Substrate emits AWQ when recipe specifies it. |
| **GPTQ** (Gradient-aware Post-Training Quantization) | DERIVED / lossy | **NO** | yes (compat) | Gradient samples baked in → fails heuristic (3). Emit only. |
| **EXL2** (ExLlamaV2 quantization) | DERIVED / lossy | **NO** | yes (compat) | Measurement passes baked in → fails heuristic (3). Emit only. |
| **BNB_NF4 / FP8 / INT8 quantized** (bitsandbytes 4-bit normalfloat, etc.) | DERIVED / lossy | **NO** | yes (compat) | Reduced precision → fails heuristic (2). Emit only. |
| **PNG / lossless WebP / lossless AVIF / lossless HEIC** | CANONICAL | yes | yes (native) | Lossless image formats; pixel content reconstructs bit-identical. |
| **JPEG / lossy WebP / lossy AVIF / lossy HEIC** | DERIVED / lossy | **NO** | yes (compat) | Lossy compression → different pixel values after decode. Emit only when recipe specifies. |
| **FLAC / WAV / AIFF / lossless ALAC** | CANONICAL | yes | yes (native) | Lossless audio formats; PCM samples reconstruct bit-identical. |
| **MP3 / Opus (lossy) / AAC / lossy formats** | DERIVED / lossy | **NO** | yes (compat) | Lossy compression → different PCM after decode. Emit only. |
| **Python `.py` / `.pyi` source** | CANONICAL | yes | yes | Source text is canonical; parsed via TreeSitterDecomposer per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) Layer 9. |
| **Python `.pyc` / `.pyo` bytecode** | DERIVED | **NO** | optional | Compiled bytecode is a build artifact. Source `.py` is the canonical form. |
| **Minified JS / CSS / production bundles** | DERIVED | **NO** | optional | Minification is a build step. Source is canonical. |
| **WordNet `data.noun` / `data.verb` / etc.** | CANONICAL | yes | yes (synthesized) | Princeton WordNet 3.0's published format. Per [ADR 0057 emission discipline](0057-substrate-emission-discipline-product-not-packaging.md), substrate may synthesize a WordNet-shape output but not byte-equal to the original. |
| **CoNLL-U treebanks (UD)** | CANONICAL | yes | yes (synthesized) | Universal Dependencies publisher's native format. |
| **Wiktionary XML dumps** | CANONICAL | yes | yes (synthesized) | Wikimedia's published export format. |
| **Tatoeba CSV / per-language audio** | CANONICAL | yes | yes (synthesized) | Tatoeba's published format. |
| **ConceptNet JSON-LD** | CANONICAL | yes | yes (synthesized) | ConceptNet's published format. |
| **Atomic2020 TSV** | CANONICAL | yes | yes (synthesized) | Atomic2020's published format. |

### When only a derived artifact is available

If a user / pipeline hands the substrate a derived artifact (e.g., they have GGUF but not the canonical safetensors), the per-source decomposer:

1. **Refuses to ingest the artifact's content as knowledge.** The substrate does NOT extract attestations from GGUF's quantized weight tensors, JPEG's decoded pixel values, MP3's decoded PCM, etc.
2. **Records the artifact's existence** via a substrate entity for the derived artifact (content-addressed by its canonical bytes per [ADR 0015 BLAKE3](0015-blake3-for-entity-hashing.md)) — this entity carries source-format meta-attestations (e.g., `HAS_FORMAT → GGUF_Q4_K_M_format_entity`, `HAS_QUANT_METHOD → GGUF_Q4_K_M_method_entity`).
3. **Links to canonical source if known**, via an `IS_LOSSY_ENCODING_OF` attestation per [GLOSSARY Canonicalization](../../GLOSSARY.md). When the substrate later ingests the canonical safetensors, the substrate already has the derived entity recorded; the `IS_LOSSY_ENCODING_OF` attestation makes the relationship queryable. When the canonical isn't known, the derived entity stands alone with `HAS_FORMAT` meta-attestations but no canonical-source cross-link.
4. **Surfaces to the operator** via the per-IDecomposer's progress reporting + logs per [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md): *"Refused to ingest GGUF artifact at /vault/models/.../qwen3-q4_k_m.gguf — derived/lossy per ADR 0058. Recorded artifact existence. Canonical safetensors not present in `/vault/models/Qwen--Qwen3.../`."*
5. **Does NOT silently fall back to ingesting the derived as a degraded source.** No "ingest with lower trust class" optimization is permitted at this layer. The user can manually elevate trust per [ADR 0044 trust class taxonomy](0044-attestation-kind-priors-and-source-trust-taxonomy.md) if they have explicit reason (e.g., the derived artifact is the only one ever published for a particular model), but that requires an explicit user-authorized override + the substrate records the override in its provenance trail.

### Per-decomposer responsibility (per ADR 0051 IDecomposer contract)

Each per-source `IDecomposer` per [ADR 0051](0051-idecomposer-csharp-plugin-contract.md) is responsible for:

1. **Implementing the canonicality criterion for its source family.** Per-decomposer ADR documents which formats it considers canonical vs derived (the per-source rows of the matrix above are the starting point for AI / image / audio decomposers; linguistic decomposers typically have one canonical format only).
2. **Refusing to ingest derived artifacts as knowledge sources.** Following the 5-step protocol above when a derived artifact is encountered.
3. **Recording derived artifact existence** as substrate entities + meta-attestations (HAS_FORMAT, IS_LOSSY_ENCODING_OF, HAS_QUANT_METHOD, etc.). The cross-source-canonicality kind vocabulary (`IS_LOSSY_ENCODING_OF` + `HAS_FORMAT` + format-specific subtypes) bootstraps at install per [ADR 0042 Stage 3](0042-bootstrap-order-and-substrate-canonical-seeding.md) as modality-agnostic kinds.

### What this ADR does NOT do

- Specify the `IFormatWriter` plugins' emission semantics (covered by [ADR 0057 emission discipline](0057-substrate-emission-discipline-product-not-packaging.md) + the format-writer emission matrix per tracking issue #220).
- Specify how the substrate parses any specific container format (per [ADR 0055 static structural parse](0055-static-structural-parse-exploded-view.md) — IContainerParsers do that).
- Specify the per-source attestation-kind vocabulary (each per-source decomposer ADR documents its own).
- Override [ADR 0044 trust class taxonomy](0044-attestation-kind-priors-and-source-trust-taxonomy.md). Trust class is per-source; canonicality is per-artifact-per-source. Both apply — a canonical source can be at any trust class (foundational / standards / academic / model-probe / etc.); a derived artifact from a high-trust source is still refused-as-knowledge regardless of source trust class.

## Consequences

- **The substrate's ingest scope is well-defined**: canonical / developer-shipped artifacts only. No silent ingest of derived artifacts. No quietly-bake-in-quantization-as-knowledge path.
- **The substrate can still REFERENCE derived artifacts** via `HAS_FORMAT` + `IS_LOSSY_ENCODING_OF` attestations on substrate entities for the derived artifacts. The substrate knows that `qwen3-q4_k_m.gguf` exists, is a GGUF Q4_K_M derivation of the canonical Qwen3 safetensors, lives at a particular path / URL / hash. This is fact-about-the-derivative without ingesting the derivative's content.
- **`IFormatWriter` plugins handle emission** to any of the derived formats per recipe. The substrate's input/output asymmetry is locked: input is canonical-only; output can synthesize any format the recipe specifies + a writer exists for.
- **Per-decomposer ADRs document their per-source canonicality matrix.** WordNetDecomposer's ADR notes "WordNet has one canonical format (Princeton 3.0 release files); no canonicality decision is needed at ingest time." ModelDecomposer composite per [ADR 0043](0043-composite-decomposer-architecture.md) carries the full matrix above for its AI-model artifacts.
- **No fallback ingest pathway** — operators encountering a derived-only-available case must explicitly authorize override per the trust-class extension mechanism in [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md). The default is refusal-with-existence-recording.
- **Cross-source consensus is unaffected.** The substrate's arena-aware Glicko-2 accumulator per [ADR 0036](0036-arena-semantics-and-source-trust.md) operates on canonical source attestations only; derived artifacts contribute zero knowledge attestations. The cross-source consensus per [ADR 0056 weight-tensor ETL](0056-weight-tensor-etl-as-arena-matchup-observation.md) accumulates from canonical safetensors of multiple models, not from their GGUF/AWQ derivatives.
- **The 2026-05-24 'we EXPORT those because we can' framing** is operationally enforced: the substrate's input/output policy is intentionally asymmetric. The substrate consumes canonical knowledge; it can synthesize any downstream-compatible format from substrate state.
- **Aligns with the Food principle + Vampire mode**: the substrate consumes canonical artifacts (the developer's published knowledge), drains the typed-attestation matchups via [ADR 0056 ETL](0056-weight-tensor-etl-as-arena-matchup-observation.md), discards the source packaging per [ADR 0057 emission discipline](0057-substrate-emission-discipline-product-not-packaging.md). Derived artifacts wouldn't add knowledge value above what the canonical already provides; ingesting them would inflate substrate state with lossy versions of knowledge the substrate already has.

## Alternatives considered

- **Ingest derived artifacts at lower trust class.** Rejected per the 2026-05-24 conversation: *"We dont care about quantized 'mp3' models."* Ingesting AWQ/GPTQ/etc. would inflate substrate state with attestations derived from lossy reconstructions of values the canonical source has at full precision. Trust class is the wrong dial — quantization loss isn't about the source's credibility; it's about the artifact's information content vs the canonical.
- **Ingest BOTH canonical and derived when both are present** (record cross-source consensus across the lossless + lossy reconstructions). Rejected — wastes ingest cost on knowledge that's already in the canonical at higher fidelity. The derived's matchup values are guaranteed to be approximations (per quantization theory) of the canonical's matchup values; adding them as "independent sources" double-counts a single training run.
- **Ingest derived as a fallback when canonical isn't available, with a degraded trust class.** Rejected as default behavior — silent fallback creates substrate state that depends on which file the user happened to have available, not on the actual source's published canonical knowledge. Explicit override mechanism per [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) is available for the rare case (e.g., a model never published in canonical form), but the default is refuse-with-existence-recording.
- **Define canonicality per-source rather than universally.** Rejected — the heuristic set above generalizes across modalities. Per-format classification varies, but the criterion is one set of rules. Per-source ADRs apply the rules to their specific format ecosystems; they don't reinvent the criterion.
- **Treat the canonicality decision as a per-decomposer policy with no shared ADR.** Rejected — exactly the duplication anti-pattern [STANDARDS Reusable helpers](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md) forbid. Without this ADR, every model decomposer (and every image/audio decomposer when those land) would silently invent its own AWQ-handling, GGUF-handling, JPEG-handling, MP3-handling policy. One ADR; one policy; per-decomposer realization.

## References

- [RULES R3](../../RULES.md) — lottery-ticket-aware sparsity (operates on canonical weight values, not derived approximations)
- [RULES R4](../../RULES.md) — sparse-by-construction emission (emit downstream formats from substrate state)
- [STANDARDS Reusable helpers](../../STANDARDS.md) + [STANDARDS Canonicalization discipline](../../STANDARDS.md)
- [GLOSSARY Canonicalization](../../GLOSSARY.md) — the entity-level lossless-vs-lossy invariant this ADR operationalizes at the ingest boundary
- [GLOSSARY Vampire mode](../../GLOSSARY.md) — universal AI-model-specific Food principle instance
- [GLOSSARY Food principle](../../GLOSSARY.md) — universal ingestion posture
- [GLOSSARY Substrate Synthesis](../../GLOSSARY.md) — fresh emission from substrate state
- [DESIGN.md VII — Model-codec fidelity](../../DESIGN.md)
- [DESIGN.md VIII — Recipe extraction + custom synthesis recipes](../../DESIGN.md)
- [ADR 0009](0009-recipe-extraction-and-overrides.md) — recipe extraction
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — `IDecomposer` + `IFormatWriter` plugin families
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3 content-addressing
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics + source trust (separate axis from canonicality)
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion + model-codec fidelity (canonical safetensors is the model-ingest target)
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (IS_LOSSY_ENCODING_OF + HAS_FORMAT kinds bootstrap at Stage 3)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite ModelDecomposer (where canonicality decisions get most concrete)
- [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) — trust-class taxonomy (orthogonal to canonicality)
- [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — per-decomposer canonicality-checking responsibility
- [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) — surfaces refusal-with-existence-recording via progress reporting
- [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) — container parsing (precondition to canonicality check)
- [ADR 0056 weight-tensor ETL as arena-matchup observation](0056-weight-tensor-etl-as-arena-matchup-observation.md) — what happens to a canonical AI model after canonicality check passes
- [ADR 0057 substrate emission discipline](0057-substrate-emission-discipline-product-not-packaging.md) — the emit-side complement (substrate emits to derived formats but never preserves source packaging)
- Tracking issue #219 — this ADR closes that tracking
- Conversation 2026-05-24: *"We dont care about quantized 'mp3' models... we care about safetensors... we EXPORT those because we can. We arent ingesting AWQ and GGUF for example. We DO ingest the yoloscript or whatever those file types are... if they are 'the developer version', we ingest it... follow?"*
