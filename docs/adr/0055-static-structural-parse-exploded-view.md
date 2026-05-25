# ADR 0055: Static structural parse / exploded view — universal container dissection (substrate never loads files)

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

## Context

Per the 2026-05-24 conversation correction: *"for pickle, the python would just be parsed as code/text... we're not doing binary lazy reads of these files... we're literally semantically dissecting them to an 'exploded view'."*

This corrects an earlier (wrong) ADR draft framing that proposed *"Pickle-security policy for PyTorch checkpoints"* — the conventional defensive-loading posture (`weights_only=True`, sandboxed unpickling, allowlist, etc.). That framing **presupposes loading**. The substrate doesn't load. The substrate **statically dissects every container format** into substrate entities + typed attestations, the same way it dissects text via [TextDecomposer (ADR 0047)](0047-text-decomposer-pure-primitive.md) or codepoints via [UnicodeDecomposer](0042-bootstrap-order-and-substrate-canonical-seeding.md).

Without this ADR, every per-source decomposer that touches a binary container (`ModelDecomposer` composite per [ADR 0043](0043-composite-decomposer-architecture.md) hits this for safetensors / PyTorch / ONNX / TF SavedModel / YOLO; future image / audio decomposers hit it for PNG / JPEG / FLAC / MP4; the universal-Food-principle generalizes per the user's framing that "AI models (Vampire mode), document corpora, image archives, video collections, audio libraries, code repositories, scientific datasets, government open data, knowledge graphs, database exports, web archives, ANY structured or semi-structured data ecosystem — is food, not an artifact to preserve" per [GLOSSARY.md Food principle](../../GLOSSARY.md)) would either invent its own defensive-loading shape or import a third-party loader and absorb its semantics + security surface.

Neither is the substrate's posture. The substrate parses container bytes statically, extracts the meaningful pieces as substrate entities with structural typed attestations between them, hands modality-specific payloads off to the appropriate downstream decomposer ([TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md), `TensorDtypeDecoder` per [ADR 0043](0043-composite-decomposer-architecture.md), `ImageDecomposer` / `AudioDecomposer` when those land, `TreeSitterDecomposer` for code), and discards the container per the Food principle.

The key insight: **a PyTorch `.pt` file's pickle bytestream references Python class names** (e.g., `torch.nn.Linear`, `transformers.models.qwen2.Qwen2Attention`). Those references point at **actual Python source code** in the framework's tree. The substrate resolves those references by handing the Python source to [TreeSitterDecomposer (Layer 9 per ADR 0037)](0037-layered-seed-ingestion-and-model-codec-fidelity.md)'s Python grammar — which decomposes it into code-as-substrate-entities the same way TextDecomposer decomposes prose. The pickle file isn't a black box; it's a structural artifact whose Python references resolve to other substrate entities.

## Decision

**The substrate's universal posture toward any container format is: static structural parse, never load, never execute, never invoke framework-native loaders. Every container becomes an "exploded view" of substrate entities + typed structural attestations.**

### Universal pattern (per-container repeat of this shape)

```text
container_bytes (PyTorch .pt / safetensors / ONNX / TF SavedModel / GGUF / HDF5 / ZIP / TAR / Jupyter / etc.)
    │
    │ static structural parser (per-format, format-specific)
    │   - reads bytes by the format's published wire spec
    │   - never invokes the framework's loader
    │   - never executes embedded code
    │   - never deserializes Python objects via pickle protocol
    │
    ▼
exploded view as substrate entities + typed attestations:
    container_entity                          # the file as substrate entity (BLAKE3 of canonical bytes)
        ├─ HAS_FORMAT  → container_format_kind_entity (SafetensorsContainer / PyTorchPickleContainer / etc.)
        ├─ HAS_ENTRY   → entry_entity_1   (offset, length, name attestations)
        ├─ HAS_ENTRY   → entry_entity_2
        ├─ ...
        ├─ REFERENCES_PYTHON_CLASS → python_class_entity (resolved via TreeSitterDecomposer)
        └─ CONTAINS_TENSOR → tensor_entity (handed to TensorDtypeDecoder for canonical-form decode)
```

The structural typed attestations are first-class substrate knowledge; the *container itself* is discarded (Food principle) after the exploded view lands.

### Per-format static parsers

A registry of per-format static parsers under `Laplace.Decomposers.Containers.*` (or analogous engine-side library — see *Placement* below). One parser per format. Each implements:

```csharp
public interface IContainerParser {
    string FormatName { get; }                              // "safetensors" / "pytorch_pickle" / "onnx" / etc.
    Hash128 FormatKindId { get; }                           // bootstrapped at install per ADR 0042
    bool CanParse(ReadOnlySpan<byte> magic);                // format-detection sniffing
    IAsyncEnumerable<ExplodedViewItem> ParseAsync(
        Stream containerStream,
        IContainerParseContext context,
        CancellationToken ct);
}

public abstract record ExplodedViewItem {
    public required Hash128 ContainerId { get; init; }      // parent container's substrate ID

    public sealed record Entry(
        string EntryName,                                   // path within container (e.g., "data/file_0.dat")
        long OffsetBytes,
        long LengthBytes,
        IReadOnlyDictionary<string, string> Metadata) : ExplodedViewItem;

    public sealed record TensorReference(
        string TensorName,                                  // model.layers.0.self_attn.q_proj.weight
        TensorDtype Dtype,                                  // FP16 / BF16 / FP32 / INT8 / etc.
        int[] Shape,
        long OffsetBytes,
        long LengthBytes) : ExplodedViewItem;

    public sealed record PythonClassReference(
        string FullyQualifiedName,                          // torch.nn.Linear
        string ModulePath,                                  // torch.nn
        string ClassName) : ExplodedViewItem;

    public sealed record EmbeddedText(
        string Role,                                        // "config_json" / "tokenizer_json" / "chat_template" / etc.
        string Content) : ExplodedViewItem;                 // handed to TextDecomposer per ADR 0047

    public sealed record EmbeddedContainer(                 // nested containers (ZIP-in-ZIP, etc.)
        string EntryName,
        Stream NestedContainerStream) : ExplodedViewItem;   // recurse via container parser registry
}
```

Per-format implementations:

| Container | Parser | Static parse uses |
|---|---|---|
| **safetensors** | `SafetensorsContainerParser` | Documented header (u64 header length + JSON metadata + tensor data offsets); just reads bytes |
| **PyTorch `.pt` / `.pth`** | `PyTorchPickleContainerParser` | ZIP container static parse + PEP-3154 pickle opcode static parse (no execution; opcodes mapped to structural ExplodedViewItems) + tensor blob extraction by offset |
| **ONNX `.onnx`** | `OnnxProtobufContainerParser` | Published `onnx.proto` schema; protobuf wire-format static parser (every protobuf parser is static by design) |
| **TensorFlow SavedModel** | `TfSavedModelContainerParser` | Published `saved_model.proto` + variable shards format |
| **HDF5 `.h5`** | `Hdf5ContainerParser` | HDF5 superblock + groups + datasets + chunks per the HDF5 specification |
| **YOLO Ultralytics `.pt`** | Same as `PyTorchPickleContainerParser` | Ultralytics ships as PyTorch pickle; same path |
| **GGUF** | `GgufContainerParser` (emit-only per [conversation 2026-05-24](#references)) | Header + metadata + tensor blocks per GGUF spec; only used by [IFormatWriter](0011-polymorphic-plugin-architecture.md) emission path, never ingestion |
| **ZIP / TAR / 7z** | `ZipContainerParser`, `TarContainerParser`, etc. | Standard archive formats; entries become substrate entities; recursively parse per-entry via parser registry |
| **Jupyter `.ipynb`** | `JupyterContainerParser` | JSON parse → code cells → `EmbeddedText { Role = "python_source" }` → TreeSitterDecomposer; markdown cells → `EmbeddedText { Role = "markdown" }` → TextDecomposer; outputs → CONTENT entities |
| **Python `.py` / `.pyi`** | (no separate container parser needed) | Direct hand-off to TreeSitterDecomposer's Python grammar |

### Pickle bytestream static parsing — worked example

PEP-3154 pickle protocol is a stack-based opcode language. Each opcode is one byte (some with operand bytes following). The parser walks the bytestream, tracks a stack of structural items, and emits ExplodedViewItems as opcodes resolve:

```text
Opcode 0x80 PROTO       → header (protocol version)
Opcode 0x95 FRAME       → length-prefixed frame
Opcode 0x8c SHORT_BINUNICODE → string literal (push UTF-8 onto stack)
Opcode 0x94 MEMOIZE     → save top-of-stack into memo
Opcode 0x68 BINGET      → push memo[arg] onto stack
Opcode 0x63 GLOBAL      → emit PythonClassReference { Module: top-1, Name: top }
Opcode 0x52 REDUCE      → emit StructuralCall { Callable: top-1, Args: top } — does NOT execute
Opcode 0x71 BINPUT      → memoize at arg index
Opcode 0x80 PROTO PUT NEWOBJ_EX → emit StructuralInstance { Class: ..., Args: ..., Kwargs: ... }
Opcode 0x86 TUPLE2 / 0x87 TUPLE3 → structural tuple
Opcode 0x65 EMPTY_DICT  → empty dict marker
Opcode 0x5d EMPTY_LIST  → empty list marker
Opcode 0x2e STOP        → end of stream
```

A 100% structural walk; nothing is executed. The output is an `IAsyncEnumerable<ExplodedViewItem>` describing the pickle's logical structure as substrate entities + typed attestations.

When a `GLOBAL` opcode references `torch.nn.Linear`, the parser emits `PythonClassReference { FullyQualifiedName = "torch.nn.Linear", ... }`. Downstream (in `ModelDecomposer` or wherever this exploded view is consumed), that reference is resolved by handing the actual Python source of `torch.nn.Linear` (read from `/opt/laplace/external/torch/torch/nn/modules/linear.py` or analogous) to TreeSitterDecomposer's Python grammar. The Python source becomes substrate entities (class definitions, method bodies, attribute accesses, etc.) with their own typed attestations. The pickle's reference to the class resolves to those substrate entities.

When a tensor blob is referenced (`torch._utils._rebuild_tensor_v2` opcode pattern with offset metadata), the parser emits `TensorReference { TensorName, Dtype, Shape, OffsetBytes, LengthBytes }`. Downstream hands the raw tensor bytes to `TensorDtypeDecoder<FP16/BF16/FP32/etc.>` per [ADR 0043](0043-composite-decomposer-architecture.md), which decodes to canonical numerical form. The substrate then extracts tensor-calculation attestations per the architecture template + lottery-ticket sparsity per [RULES.md R3](../../RULES.md).

### Why no security policy is needed

- No code is executed (no `__reduce__` callbacks invoked, no class instantiation, no method calls).
- No framework-native loader is invoked (no `torch.load`, no `pickle.load`, no `onnx.load`, no `tf.saved_model.load`).
- No arbitrary file paths are followed (parser reads only the container's own bytes plus, optionally, the framework's Python source if Python class references need resolution — and that Python source is treated as text to parse, not code to execute).

The conventional defensive-loading concerns (`weights_only=True`, allowlist, sandbox) presuppose loading. The substrate doesn't load. There's no attack surface to defend.

### Placement

- **Parser registry**: `Laplace.Decomposers.Containers.Abstractions` (the `IContainerParser` interface + `ExplodedViewItem` types) under `app/Laplace.Decomposers.Containers.Abstractions/` per [ADR 0026](0026-csharp-project-structure.md).
- **Per-format parsers**: one project per format under `app/Laplace.Decomposers.Containers.<Format>/` (e.g., `Laplace.Decomposers.Containers.Safetensors`, `Laplace.Decomposers.Containers.PyTorchPickle`, `Laplace.Decomposers.Containers.Onnx`).
- **Engine-side static parsers** for hot-path formats (safetensors, pickle, protobuf): C/C++ implementation in `engine/synthesis` (per [ADR 0024](0024-engine-modularization.md) — synthesis library handles container readers + writers); C# parsers thin-wrap the engine side via P/Invoke. For non-hot-path formats (HDF5, niche framework dumps) pure C# may be fine.
- **`ModelDecomposer` (composite per ADR 0043)** consumes the exploded view: container parser produces the `IAsyncEnumerable<ExplodedViewItem>`; ModelDecomposer's `TextModality` ModalityBinder feeds embedded text through TextDecomposer; ModelDecomposer's `TensorDtypeDecoder` feeds tensor blobs through dtype decoders; ModelDecomposer's `SemanticArchitectureDecomposer` resolves tensor-name → mechanical-role.

### What `IContainerParser` does NOT do

- Hash anything ([HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md) does that, called downstream on the substrate entities derived from the exploded view).
- Touch the database ([SubstrateCRUD / ADR 0050](0050-substrate-crud-write-surface.md) does that, after the source decomposer assembles a SubstrateChange from the exploded view).
- Execute code from the container (the whole point).
- Invoke framework-native loaders (the whole point).
- Decode tensor dtype (handed off to `TensorDtypeDecoder`).
- Decompose embedded text (handed off to TextDecomposer).
- Parse embedded code (handed off to TreeSitterDecomposer).

## Consequences

- **One universal posture toward containers**: never load, always statically dissect. Generalizes Vampire mode beyond AI models to every container format the substrate ever ingests.
- **No security policy needed** for container ingest. The attack surface conventional loaders carry doesn't exist here. The 2026-05-24 conversation's "we don't do binary lazy reads of these files" framing makes this concrete.
- **Per-format parsers are pure functions**: bytes-in → `IAsyncEnumerable<ExplodedViewItem>` out. Trivially testable; trivially composable; trivially extensible to new formats.
- **Python source becomes substrate content** via TreeSitterDecomposer when pickle references resolve. The substrate's understanding of `torch.nn.Linear` isn't a black-box class — it's the actual Python source bytes parsed into AST entities, attested with relationships to other classes (`BASE_CLASS_OF`, `OVERRIDES_METHOD`, etc.).
- **GGUF stays emit-only** per the [prior 2026-05-24 correction](#references) — no GGUF static parser in the ingest registry; only a GGUF writer in [IFormatWriter](0011-polymorphic-plugin-architecture.md) per the format-writer emission matrix.
- **Adds a new C# project family**: `Laplace.Decomposers.Containers.*`. One per container format. Same shape as the per-source decomposer projects.
- **The deleted "Pickle-security policy" planning artifact is replaced** with this — same surface (PyTorch ingest), correct framing (static parse, not defensive load).

## Alternatives considered

- **Use `torch.load(weights_only=True)` for PyTorch checkpoints.** Rejected — invokes Python + the pickle protocol + PyTorch's loader. Security policy debate (which we have no need to engage in). Brings PyTorch as a runtime dependency of the substrate's ingest path. Wrong framing per the 2026-05-24 conversation.
- **Use a sandboxed Python subprocess to deserialize.** Rejected — same problem, more infrastructure. Sandboxing adds a moving part; static parsing eliminates the need.
- **Use each framework's published loader with `safe_mode` / `weights_only`.** Rejected — different frameworks have different safe-mode semantics; ties substrate to N framework runtime versions; doesn't generalize to non-loadable formats (ONNX protobuf, HDF5, YOLO native).
- **Skip parsing the pickle opcode stream; just extract tensor blobs from the ZIP.** Considered — works for the *weight extraction* path but loses the structural metadata (tensor names, dtypes, shapes, model-architecture references in the pickle). Static opcode parsing recovers that structure without execution.
- **Treat PyTorch / ONNX / TF as "opaque containers" and store the bytes as substrate content entities, hand them to a downstream tool for query.** Rejected — violates Food principle (substrate doesn't preserve packaging) and Vampire mode (substrate doesn't preserve weight bytes). Static parse + exploded view is the correct posture.

## References

- [RULES.md R3](../../RULES.md) — lottery-ticket-aware sparsity (applied to tensor blobs post-decode)
- [RULES.md R4](../../RULES.md) — sparse-by-construction emission (per-format writers, emission-only path)
- [RULES.md R10](../../RULES.md) — polymorphic plugin architecture (per-format parsers are plugins)
- [RULES.md R16](../../RULES.md) — separation of concerns (parsing in C/C++ engine where hot-path; orchestration in C#)
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [GLOSSARY.md Food principle](../../GLOSSARY.md) — universal ingestion posture (substrate consumes + discards packaging)
- [GLOSSARY.md Vampire mode](../../GLOSSARY.md) — AI-model-specific instance of Food principle
- [GLOSSARY.md Canonicalization](../../GLOSSARY.md) — lossy conversions are not equivalent under canonicalization (informs why we never invoke framework loaders that might transform tensor values)
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture (IContainerParser fits as a sub-plugin under IDecomposer per ADR 0051)
- [ADR 0024](0024-engine-modularization.md) — engine modularization (hot-path parsers in `liblaplace_synthesis`)
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion (TreeSitterDecomposer at Layer 9 resolves Python class references)
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — multi-modal entity types
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — decomposer scope (containers are part of the source's full ecosystem)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite ModelDecomposer (consumes the exploded view)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) — downstream consumer of EmbeddedText
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — downstream consumer of structural entities
- [ADR 0049 SubstrateChange](0049-substrate-change-intent-type.md) — what the per-source decomposer builds from the exploded view
- [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md) — applies the SubstrateChange
- [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — `ModelDecomposer` per ADR 0043 implements `IDecomposer` and uses `IContainerParser`s
- [PEP 3154 — Pickle protocol 4](https://peps.python.org/pep-3154/) — opcode reference
- [safetensors format spec](https://github.com/huggingface/safetensors)
- [ONNX `onnx.proto`](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto)
- [GGUF spec](https://github.com/ggerganov/ggml/blob/master/docs/gguf.md)
- [HDF5 file format specification](https://docs.hdfgroup.org/hdf5/develop/_f_m_t3.html)
- Conversation 2026-05-24: *"for pickle, the python would just be parsed as code/text... we're not doing binary lazy reads of these files... we're literally semantically dissecting them to an 'exploded view'."*
- Conversation 2026-05-24: prior correction that GGUF / AWQ / GPTQ / EXL2 / BNB_NF4 are emit-only formats (not in ingest scope).
