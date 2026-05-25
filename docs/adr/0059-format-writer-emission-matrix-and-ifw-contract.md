# ADR 0059: Format-writer emission matrix + `IFormatWriter` C# plugin contract

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

Tracks planning issue #220.

## Context

[ADR 0058 canonicality criterion](0058-canonicality-criterion-for-ingestible-sources.md) locked the **ingest side**: substrate ingests canonical / developer-shipped artifacts; derived / lossy formats are refused-as-knowledge with existence-recording via `IS_LOSSY_ENCODING_OF`. [ADR 0057 substrate emission discipline](0057-substrate-emission-discipline-product-not-packaging.md) locked the universal Food principle at the emission boundary: product (substrate-canonical content + typed attestations) is bit-perfect emittable, packaging (source-format-specific byte layout) is synthesized fresh per target format.

What's missing on the emit side: the **format-writer matrix** specifying which target formats the substrate can emit to per recipe, and the **C# `IFormatWriter` plugin contract** that every per-format writer implements. [ADR 0011 polymorphic plugin architecture](0011-polymorphic-plugin-architecture.md) names `IFormatWriter` as one of the six plugin interfaces; [DESIGN.md VI](../../DESIGN.md) sketches a C++ interface (`virtual void write(const ModelData&, std::ostream&) = 0;`); but the actual C# contract + the per-format coverage + the per-recipe selection mechanism + the per-format calibration requirement handling are all undocumented.

The 2026-05-24 conversation makes the emit side's asymmetry concrete: *"we EXPORT those because we can."* The substrate's input/output asymmetry per ADR 0058 — canonical-in, any-out — is intentional. The substrate consumes canonical knowledge from a small set of source-format families; it can synthesize many output-format families for downstream consumption because the source content is already in substrate-canonical form + cross-source consensus per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) + [ADR 0056 weight-tensor ETL](0056-weight-tensor-etl-as-arena-matchup-observation.md) makes the substrate's output superior to any single ingested source.

Without this ADR:

- Every emission target reinvents its own writer contract (per-format bespoke wiring; duplication anti-pattern per [STANDARDS Reusable helpers](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md)).
- The substrate's emit scope is implicit (only documented in scattered ADR mentions: GGUF in [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md); AWQ/GPTQ/EXL2/BNB_NF4 in conversation; ONNX/safetensors unspecified).
- Per-recipe selection of emission targets (the `proof_exports` + `compat_exports` recipe fields sketched in [DESIGN.md VIII](../../DESIGN.md)) has no operational specification.
- Per-format calibration requirements (AWQ activation calibration; GPTQ gradient samples; EXL2 measurement passes) have no documented mechanism for handling without running the source model — which contradicts [ADR 0055 static parse](0055-static-structural-parse-exploded-view.md) + [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) + the substrate's no-loader / no-execution posture.

## Decision

**Introduce `IFormatWriter` as the C# canonical plugin interface for per-format emission writers, with a per-target format matrix specifying the substrate's emission scope + per-recipe selection mechanism + per-format calibration handling via substrate state (no source-model invocation).**

### The format-writer matrix

| Target format | Class (per [ADR 0058](0058-canonicality-criterion-for-ingestible-sources.md)) | Writer responsibility | Substrate-state inputs | Calibration mechanism |
|---|---|---|---|---|
| **safetensors** (fp32 / bf16 / fp16) | canonical | Native package shape; HuggingFace-compatible. Tensor-by-tensor write of substrate-aggregated `Q_PROJECTS` / `K_PROJECTS` / `V_PROJECTS` / `O_PROJECTS` / `EMBEDS` / `GATES` / `UP_PROJECTS` / `DOWN_PROJECTS` / `NORMALIZES` / `OUTPUT_PROJECTS` attestations per recipe's layer/head/dim layout. | recipe entity + aggregated attestations per recipe's `knowledge_scope` per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) | none (writer emits at recipe-specified precision; substrate state IS the per-cell value source) |
| **GGUF** (per-quant Q4_K_M / Q5_K_M / Q6_K / Q8_0 / Q4_0 / Q5_0 / etc.) | derived (emit-only) | Per-quant GGUF spec; llama.cpp-compatible. Proof / compatibility artifact per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md). | recipe + aggregated attestations + per-quant block-size + scale/zero parameters per GGUF format spec | none required — GGUF's per-block scale/zero is computed from the substrate-emitted tensor values directly (per-block statistics over the substrate-aggregated weights) |
| **AWQ** (4-bit activation-aware) | derived (emit-only) | Per-channel scale-shift; AWQ-compatible. | recipe + aggregated attestations + per-channel substrate-derived activation distribution proxy (see calibration mechanism below) | substrate-derived: use the substrate's cross-source consensus on `Q_PROJECTS`/`K_PROJECTS`/etc. matchup strengths AS the activation distribution proxy. Per [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) the substrate has accumulated the "would-be-observed-activations" already; AWQ's calibration step normally needs real activations to identify which weight columns to preserve at higher precision, but the substrate's matchup attestations ARE the cross-source-consensus activation distribution. No real forward pass needed. |
| **GPTQ** (gradient-aware post-training) | derived (emit-only) | Per-layer GPTQ optimization; transformers/autoGPTQ-compatible. | recipe + aggregated attestations + per-layer substrate-derived "gradient-proxy" (Hessian approximation from substrate state) | substrate-derived: same principle — GPTQ's per-layer Hessian computation traditionally needs gradient samples; substrate state's matchup-strength variance across sources provides the Hessian-equivalent statistics. Substrate-native realization of the GPTQ optimization. |
| **EXL2** (ExLlamaV2 quantization with measurement passes) | derived (emit-only) | Per-tensor mixed-precision per measurement; ExLlamaV2-compatible. | recipe + aggregated attestations + per-tensor substrate-derived measurement | substrate-derived: same principle. |
| **BNB_NF4 / FP8 / INT8** (bitsandbytes / per-precision) | derived (emit-only) | Per-quant scheme; bitsandbytes / framework-compatible. | recipe + aggregated attestations + per-block scale | none required (block-wise normalization computed from substrate-emitted tensor values) |
| **ONNX** (when emitting as compat downstream) | derived (when emit-only) | ONNX graph + initializers; framework-agnostic runtime compatibility | recipe + aggregated attestations | none required (ONNX is just a different container for the same tensor values) |
| **WordNet-shape output** (synthesized data.noun / data.verb / etc.) | derived (synthesized; per [ADR 0057](0057-substrate-emission-discipline-product-not-packaging.md)) | Princeton WordNet 3.0 file format; emits cross-source-enriched output | substrate-state synsets + lemmas + typed-relation attestations (cross-source consensus from WordNet + OMW + ConceptNet + Wiktionary contributions on same synsets) | none |
| **Wiktionary-shape output** (synthesized per-entry XML) | derived (synthesized) | Wiktionary XML dump format; cross-source-enriched | substrate-state entries + definitions + IPA + etymologies + translations | none |
| **CoNLL-U-shape output** (synthesized UD treebank) | derived (synthesized) | CoNLL-U per-sentence format; cross-source-enriched | substrate-state UD_Sentence + UD_Token entities + morph features + dependency attestations | none |
| **Image emission** (PNG / lossless WebP, etc.) | canonical (lossless) or derived (lossy JPEG / lossy WebP / etc.) | Per-format encoder | substrate-state pixel entities + region structure via CONTENT physicality trajectories per [ADR 0012](0012-mantissa-packing-format.md) | none for lossless; per-format quality knob for lossy |
| **Audio emission** (FLAC / WAV / lossless ALAC, etc.) | canonical (lossless) or derived (lossy MP3 / Opus, etc.) | Per-format encoder | substrate-state audio sample / frame entities | none for lossless; per-format bitrate knob for lossy |
| **Future formats** | per `IFormatWriter` plugin | per-writer | per substrate state | per-format |

The substrate emits to **any target format for which a writer exists**, scoped by the recipe's emission fields. The recipe specifies `proof_exports` (for verification — e.g., GGUF for chat verification per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md)) + `compat_exports` (for downstream consumption — AWQ for deployment, ONNX for cross-framework portability, etc.).

### The `IFormatWriter` C# contract

```csharp
namespace Laplace.Synthesis.FormatWriters.Abstractions;

public interface IFormatWriter : IAsyncDisposable {

    /// <summary>Format this writer emits (e.g., "safetensors", "gguf-q4_k_m",
    /// "awq", "gptq", "wordnet-data.noun", "png", "flac").</summary>
    string FormatId { get; }

    /// <summary>Format class per ADR 0058 (canonical-native /
    /// canonical-lossless / derived-lossy-emit-only / synthesized).</summary>
    FormatClass FormatClass { get; }

    /// <summary>Required calibration data per the format spec. For most
    /// formats: None. For AWQ/GPTQ/EXL2: substrate-state-derived (no real
    /// forward pass per ADR 0055 + ADR 0056). The substrate's
    /// cross-source matchup attestations serve as the activation /
    /// gradient / measurement distribution proxy.</summary>
    CalibrationRequirements CalibrationRequirements { get; }

    /// <summary>Emit one package per recipe + substrate state.
    /// Writer reads:
    ///   - recipe.knowledge_scope → source IDs to include in arena aggregation
    ///   - recipe.layer/head/dim layout → structural shape of the output
    ///   - recipe target-format parameters (e.g., GGUF quant level; AWQ bit-width)
    /// Writer produces:
    ///   - the target file(s) per the format's spec
    ///   - sparse-by-construction per RULES R4 — positions with no
    ///     significant substrate attestation emit exact zero
    ///   - provenance metadata recording the recipe + substrate-state hash
    ///     at emission time
    /// </summary>
    Task<EmissionResult> EmitAsync(
        Recipe recipe,
        ISubstrateReader reader,
        EmissionOptions options,
        IProgress<EmissionProgress>? progress,
        CancellationToken ct = default);
}

public enum FormatClass {
    CanonicalNative,            // safetensors / WordNet-shape / CoNLL-U-shape / PNG / FLAC
    CanonicalLossless,          // lossless WebP / lossless AVIF / lossless HEIC / lossless ALAC
    DerivedLosslyEmitOnly,      // GGUF / AWQ / GPTQ / EXL2 / BNB_NF4 / JPEG / MP3
    SynthesizedFreshFromState,  // synthesized WordNet-shape / Wiktionary-shape / etc. (lossless re emission cross-source-enriched)
}

public sealed record CalibrationRequirements(
    bool NeedsCalibration,
    string CalibrationKind,     // "activation-distribution" / "gradient-samples" / "measurement-passes" / "block-statistics" / "none"
    bool SubstrateStateDerivable  // true when substrate state provides the calibration data without external samples
);

public sealed record EmissionOptions(
    int ParallelWorkers,                // per-format writers may parallelize per-tensor or per-layer
    bool DryRun,                        // synthesize package metadata + size estimate without writing tensor bytes
    string OutputPath,                  // destination file or directory
    IReadOnlyDictionary<string, string> FormatSpecificParams  // per-format knobs (GGUF quant level, AWQ bit-width, etc.)
);

public sealed record EmissionResult(
    string FormatId,
    string OutputPath,
    long BytesEmitted,
    long TensorsEmitted,
    long AttestationsRead,
    long ZeroPositions,                 // sparse-by-construction count
    TimeSpan WallClock,
    Hash128 RecipeHash,                 // recipe entity's content hash at emission time
    Hash128 SubstrateStateHash          // substrate state snapshot hash for reproducibility
);
```

### Substrate-derived calibration for AWQ / GPTQ / EXL2

**Why this works without forward passes**: AWQ / GPTQ / EXL2 traditionally need to observe activations / gradients / measurements during a calibration phase against the trained model. The substrate has accumulated, via [ADR 0056 weight-tensor ETL](0056-weight-tensor-etl-as-arena-matchup-observation.md) across N ingested models, the cross-source consensus on which `(token_i, kind, token_j)` matchups carry the most weight (Glicko-2 effective-μ per [ADR 0036 arena semantics](0036-arena-semantics-and-source-trust.md) + [ADR 0044 priors](0044-attestation-kind-priors-and-source-trust-taxonomy.md)). That consensus IS the activation-distribution proxy — across all the models that have ever observed the token pair, the substrate has the aggregate "this pair fires strongly" knowledge.

Concretely:

- **AWQ's per-channel activation magnitude estimation** → substrate computes per-channel from the substrate-aggregated attestation strengths on that channel's contributing matchups. Cross-source consensus replaces single-model probe observations.
- **GPTQ's per-layer Hessian approximation** → substrate computes from the substrate-aggregated attestation variance across sources for that layer's matchups. High variance across sources = high Hessian eigenvalue → preserve at higher precision.
- **EXL2's per-tensor mixed-precision measurement** → substrate computes from the substrate-aggregated attestation entropy per tensor. High entropy = many distinct token pairs contribute = preserve more precision.

**This is a fundamentally novel quantization mechanism enabled by the substrate's cross-source consensus.** The traditional approaches need a calibration dataset because the model author's training process is opaque to the quantizer; the substrate's quantization works because the substrate IS the cross-source consensus over many models' training processes. No real data needed at calibration time; the substrate has the consensus.

Per-writer implementation specifics (the exact substrate-attestation queries that produce the per-channel / per-layer / per-tensor calibration values) land in each writer's per-writer Story when implemented. This ADR locks the principle; the Stories lock the queries.

### Recipe selection of emission targets

Per recipe JSON shape per [DESIGN.md VIII](../../DESIGN.md):

```json
{
  "based_on": "qwen3-1.5b",
  "name": "qwen3-roundtrip-with-multi-format-emission",
  "knowledge_scope": { "include_sources": ["qwen3-1.5b"], "effective_mu_policy": "source_scoped_roundtrip" },
  "output_format": "native_synthesis_package",
  "proof_exports": [
    { "format": "gguf-q4_k_m", "output_path": "out/qwen3-roundtrip-q4_k_m.gguf" }
  ],
  "compat_exports": [
    { "format": "awq", "output_path": "out/qwen3-roundtrip-awq", "bit_width": 4 },
    { "format": "onnx", "output_path": "out/qwen3-roundtrip.onnx" }
  ]
}
```

`Laplace.Cli synthesize <recipe.json>` per the Justfile resolves each export entry to its `IFormatWriter` plugin via `FormatId`, runs the writer + emits to the specified path. Parallel emission of multiple targets per recipe is supported (different writers run concurrently against the same substrate snapshot).

### Sparse-by-construction emission per RULES R4

Per [RULES R4](../../RULES.md): *"At export, positions in the target tensor with no significant substrate attestation emit zero. This makes emitted models automatically pruned (5–20% non-zero typical), synthesized from arena/source-trust effective support over the selected source scope, cleaned (no gradient jitter, no init residue)."*

Each `IFormatWriter` enforces this:

- Reads substrate state per recipe's `knowledge_scope`
- For each tensor position the recipe layout specifies, looks up the substrate attestation
- If significant (per substrate's per-arena threshold + Glicko-2 effective-μ above the per-source-scope threshold) → write the consensus value
- If insignificant (no attestation or below threshold) → write exact zero
- Writer respects the per-format zero-representation (FP zero for safetensors; quant-format zero for GGUF / AWQ / etc.)

This means **emitted models are intrinsically sparse**. Per R4, 5–20% non-zero is typical. Downstream sparse-aware runtimes (llama.cpp's sparse kernels; vLLM's sparse-aware attention; specialized sparse inference engines) get free speed-up. Non-sparse-aware runtimes pay no penalty (zeros multiply through harmlessly).

### Per-source synthesized writers (non-AI-model formats)

[ADR 0057 emission discipline](0057-substrate-emission-discipline-product-not-packaging.md) established that the substrate can emit a synthesized WordNet-shape output, Wiktionary-shape output, CoNLL-U-shape output, etc. — each per its own `IFormatWriter` plugin. These writers:

- Read substrate state per recipe's `knowledge_scope` (e.g., `include_sources: ["wordnet-3.0", "omw-2024", "wiktionary-en-2024"]` for a cross-source-enriched WordNet emission)
- Construct the target's packaging fresh per format spec
- Are NOT byte-equal to the original source files (per ADR 0057); they're cross-source-enriched + consensus-rated + deduplicated
- Behaviorally equivalent at the typed-knowledge level: any query against the original source returns the same answer (or a better one) against the synthesized output

The per-format synthesized writers (WordNetShape / WiktionaryShape / CoNLLUShape / ConceptNetJSONLD / Atomic2020TSV / etc.) follow the same `IFormatWriter` contract as the AI-model writers, just with different `FormatId` + different substrate-state inputs + different output formats.

### Placement

- **`Laplace.Synthesis.FormatWriters.Abstractions`** (interface + supporting types) under `app/Laplace.Synthesis.FormatWriters.Abstractions/` per [ADR 0026](0026-csharp-project-structure.md). References `Laplace.Engine.Synthesis` for engine-side P/Invoke (writers may delegate hot-path serialization to engine).
- **`Laplace.Synthesis.FormatWriters.<Format>`** per writer (`Safetensors`, `Gguf`, `Awq`, `Gptq`, `Exl2`, `Bnb`, `Onnx`, `WordNetShape`, `WiktionaryShape`, `CoNLLUShape`, etc.). One project per format. New format = new project.
- **Engine-side hot-path writers** in `liblaplace_synthesis` per [ADR 0024](0024-engine-modularization.md) for performance-critical paths (safetensors tensor serialization; GGUF block-quantization; AWQ per-channel scale computation). C# thin wraps via P/Invoke.
- **Plugin discovery** via assembly-scan or MEF `[Export(typeof(IFormatWriter))]` per `Laplace.Cli synthesize` + `Laplace.Endpoints.*` (when an endpoint exposes synthesis via API).

### What `IFormatWriter` does NOT do

- Decide what content to emit (recipe + substrate-state arena consensus does that, via the writer's substrate-state queries).
- Run forward passes against any model (per [ADR 0055](0055-static-structural-parse-exploded-view.md) + [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) — substrate doesn't load / doesn't execute).
- Require real calibration data (substrate state provides the calibration-equivalent statistics per the substrate-derived calibration mechanism above).
- Preserve any source's packaging byte layout (per [ADR 0057](0057-substrate-emission-discipline-product-not-packaging.md) — emission is fresh from substrate state).
- Modify substrate state (writers are read-only against the substrate; emission is a substrate snapshot read + a target file write).

## Consequences

- **The substrate's emit scope is well-defined**: a per-format matrix + a uniform `IFormatWriter` contract + per-recipe selection. Adding a new emission format = new `Laplace.Synthesis.FormatWriters.<Format>` project implementing the interface. No engine changes for non-hot-path writers; no extension changes; no contract drift.
- **Calibration without forward passes** is a substrate-novel capability. Cross-source consensus per [ADR 0036](0036-arena-semantics-and-source-trust.md) IS the activation / gradient / measurement distribution. Substrate-native AWQ / GPTQ / EXL2 emission doesn't require running source models, doesn't need calibration datasets, doesn't pay GPU/loader overhead.
- **Sparse-by-construction emission per R4 is uniformly enforced** across writers. Every emitted format inherits the substrate's intrinsic sparsity (5–20% non-zero typical). Downstream sparse-aware runtimes get free speed-up.
- **Per-recipe parallel emission** of multiple targets against one substrate snapshot. A single `just synthesize <recipe.json>` invocation can produce safetensors + GGUF Q4_K_M + AWQ + ONNX simultaneously, all from the same recipe's `knowledge_scope`-bounded substrate state.
- **The substrate's input/output asymmetry is comprehensively locked**: [ADR 0058 canonicality](0058-canonicality-criterion-for-ingestible-sources.md) covers ingest scope; this ADR covers emit scope. Canonical-in, any-out, with cross-source-enriched output superior to any single source per the Food principle.
- **Per-source synthesized writers** (WordNetShape / WiktionaryShape / etc.) use the same contract as AI-model writers. The substrate's emission discipline (per [ADR 0057](0057-substrate-emission-discipline-product-not-packaging.md)) generalizes: any source's published format can be synthesized fresh from substrate state per its own writer.
- **The C++ `IFormatWriter` sketch in DESIGN.md VI is superseded by the C# contract** per this ADR — same separation-of-concerns reasoning as [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) (orchestration in C# per [ADR 0027](0027-separation-of-concerns-invariants.md) + [RULES R16](../../RULES.md); engine hot-path serialization in C/C++ where it matters).
- **Substrate-derived calibration is a new ADR-level claim that needs empirical validation per writer.** Each AWQ / GPTQ / EXL2 writer Story includes a behavioral-fidelity test: emit at the format → load in the target runtime → verify behavioral alignment under fixed prompts. If substrate-derived calibration doesn't match real-data calibration's output quality, the writer Story may need to surface a fallback (e.g., accept an optional external calibration set as `FormatSpecificParams`).

## Alternatives considered

- **Per-format bespoke writer code with no shared interface.** Rejected — duplication anti-pattern per [STANDARDS](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md). Cross-cutting concerns (sparse-by-construction emission per R4; recipe selection; parallel emission; provenance metadata) reimplemented N times poorly.
- **Use third-party libraries for each emission format** (HuggingFace `transformers` for safetensors; `llama.cpp` for GGUF; `autoGPTQ` for GPTQ; `autoawq` for AWQ; `bitsandbytes` for BNB_NF4; ONNX runtime for ONNX). Rejected — pulls those frameworks as runtime dependencies, requires their Python ecosystems, and pollutes the C# orchestration layer with cross-language interop. Substrate-native writers per the format specs (safetensors / GGUF / etc. all have published format specs the substrate can write directly) keep the dependency surface clean + per-format optimizations possible.
- **Require real calibration data for AWQ / GPTQ / EXL2.** Rejected — contradicts the substrate's no-forward-pass posture per [ADR 0055](0055-static-structural-parse-exploded-view.md) + [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md). The substrate's cross-source consensus IS the calibration data, by construction. Fallback to external calibration sets is available via `FormatSpecificParams` if a specific writer Story validates that substrate-derived calibration falls short for that format.
- **One unified writer per emission family (one writer for all GGUF quant levels; one writer for all AWQ bit widths).** Considered + may be the implementation approach for closely-related variants, but the contract level keeps `FormatId` per-variant for plugin discovery + per-recipe selection. The writer implementation can internally handle multiple variants.
- **Emit at runtime per query** (lazy synthesis: when an endpoint receives a request, synthesize the necessary tensors). Rejected — couples query latency to synthesis cost; emission is a one-shot ETL into the target format, not a per-query operation. The substrate's runtime is cascade A* per [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md); synthesis is a separate operational mode.

## References

- [RULES R4](../../RULES.md) — sparse-by-construction emission (uniformly enforced across writers)
- [RULES R10](../../RULES.md) — polymorphic plugin architecture
- [RULES R16](../../RULES.md) — separation of concerns (writers are C# orchestration; engine hot-path where needed)
- [STANDARDS Reusable helpers](../../STANDARDS.md)
- [GLOSSARY Substrate Synthesis](../../GLOSSARY.md) — output is superior to any single ingested source
- [GLOSSARY Vampire mode](../../GLOSSARY.md) — emission discipline rooted in Vampire mode + Food principle
- [GLOSSARY Zero Calories](../../GLOSSARY.md) — sparse-by-construction emission's downstream consequence
- [DESIGN.md VI](../../DESIGN.md) — polymorphic plugin interfaces (C++ sketch this ADR's C# contract supersedes per ADR 0027 separation-of-concerns reasoning)
- [DESIGN.md VIII](../../DESIGN.md) — Recipe extraction + custom synthesis recipes (recipe JSON shape this ADR realizes)
- [ADR 0009 recipe extraction + overrides](0009-recipe-extraction-and-overrides.md)
- [ADR 0011 polymorphic plugin architecture](0011-polymorphic-plugin-architecture.md) — names IFormatWriter as one of six interfaces
- [ADR 0016 reusable helpers](0016-reusable-helpers-discipline.md)
- [ADR 0024 engine modularization](0024-engine-modularization.md) — hot-path writers in liblaplace_synthesis
- [ADR 0026 C# project structure](0026-csharp-project-structure.md)
- [ADR 0027 separation of concerns invariants](0027-separation-of-concerns-invariants.md)
- [ADR 0036 arena semantics + source-trust consensus](0036-arena-semantics-and-source-trust.md) — substrate-derived calibration via cross-source consensus
- [ADR 0037 layered seed ingestion + model-codec fidelity](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — GGUF as proof artifact
- [ADR 0044 attestation-kind priors + trust-class taxonomy](0044-attestation-kind-priors-and-source-trust-taxonomy.md) — per-source-scope arena policy
- [ADR 0051 IDecomposer C# plugin contract](0051-idecomposer-csharp-plugin-contract.md) — sibling C# plugin contract pattern (this ADR's IFormatWriter mirrors)
- [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) — substrate doesn't load files
- [ADR 0056 weight-tensor static ETL](0056-weight-tensor-etl-as-arena-matchup-observation.md) — what the substrate ingests becomes available for emission
- [ADR 0057 substrate emission discipline](0057-substrate-emission-discipline-product-not-packaging.md) — universal Food principle at emission boundary
- [ADR 0058 canonicality criterion](0058-canonicality-criterion-for-ingestible-sources.md) — input-side asymmetry partner
- Tracking issue #220 — this ADR closes that tracking
- Conversation 2026-05-24: *"we EXPORT those because we can"*; *"we ETL the knowledge out of the AI model packaging"*; canonical-in / any-out asymmetry
