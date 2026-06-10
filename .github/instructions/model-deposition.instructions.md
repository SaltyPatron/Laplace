---
applyTo: "{app/Laplace.Decomposers.Model/**,app/Laplace.Engine.Synthesis/**,engine/synthesis/**,docs/**}"
description: "Use when touching safetensors, model ETL, tensor-role arenas, model deposition, synthesis, epistemology, or ingestion docs."
---

# Laplace Model Deposition Rules

Laplace does not treat AI models as oracles, training targets, embedding stores, raw-weight archives, or generic ML artifacts. A model is a witness package to be deposed into substrate testimony.

## Safetensor snapshots vs GGUF

- **Ingest unit** = HuggingFace snapshot **directory**: `config.json` (recipe) + `tokenizer.json` (vocab/merges) + one or more `*.safetensors` (named weight tensors). Validated by `SafetensorSnapshotWitness` — a lone `.safetensors` file is never sufficient.
- **GGUF** = synthesis **render target**: self-contained (metadata + tokenizer + tensors in one file). Do not conflate ingest (distributed snapshot witness) with export (compiled GGUF artifact).
- CLI: `laplace ingest safetensors <snapshot-dir>`. `ingest model` is legacy alias only.

## Safetensors and model files

- Read safetensors through normal file operations: parse the 8-byte little-endian header length, parse JSON tensor metadata, then seek to `HeaderBytes + data_offsets` and stream/read tensor bytes from the file.
- Safetensors bytes and decoded tensor cells are transient input only. Do not store raw model files, raw weights, tensor rows, tensor cells, or reconstructable tensor payloads in substrate tables.
- Do not replace this path with framework loaders, Python model APIs, hidden inference calls, or assumptions that the whole model must be materialized through a neural runtime.
- Preserve dtype semantics. Decode supported safetensors numeric/bool dtypes explicitly. For unsupported or different containers, fail loudly. Never ingest zeros or placeholders as a convenience.
- Treat tensor names, shapes, dtype, offsets, and source file path as container facts. Do not infer missing tensors by pattern unless the model recipe explicitly defines the equivalent.

## What extraction means here

- Model ETL deposes a model by reading tensor data, deriving witness testimony, and then discarding the raw tensor payload. It is not raw-weight storage, fine-tuning, distillation, benchmark evaluation, prompt probing, or conventional embedding similarity search.
- Distinguish live code from design/handoff notes. `ModelArenaPlan` defines the tensor-role arena contract used by synthesis/export: `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORM_SCALES`, `OUTPUT_PROJECTS`. Before claiming ingest fills those arenas, verify the current `ModelTableETL` implementation.
- In the current `ModelTableETL` path, tensor bytes feed four token→token relation types: `SIMILAR_TO` (embedding cosine), `ATTENDS` (Q·K bilinear), `OV_RELATES` (V·O projection vs unembedding), and `COMPLETES_TO` (FFN contraction vs unembedding). Intermediate spaces (neuron, attn_dim) are contracted away inside native kernels and never surface as substrate entities. `ModelDecomposer.InitializeAsync` seeds tensor-role relation types for the export contract.
- A deposed model has trust class `AIModelProbe`. Its testimony is admissible and intentionally outranked by curated sources such as dictionaries, standards, and academic resources.
- The model's magnitude is consumed during ingest/adjudication. `AttestationRow` carries score/phi transiently for period folding; persisted `laplace.attestations` stores provenance, outcome class, timestamps, and counts, not raw values or reconstructable per-witness scores.
- Model-related physicalities are only present where the live code emits `PhysicalityRow` values, such as tokenizer/text structure or the separate `TokenS3Morph` path. Do not claim current `ModelTableETL` emits firefly placements unless the code does so.

## Geometry and math boundaries

- Truth lives in relational consensus. Geometry is an audit and comparison instrument, not the truth engine.
- Per-model/per-circuit placements are specimens in `physicalities`; do not blend them into a consensus coordinate unless implementing an explicitly requested derived view.
- SVD/tensor decomposition is export/synthesis machinery unless a task explicitly concerns `tensor_decompose` or an export proof. Do not force model deposition through generic SVD, PCA, cosine-similarity, clustering, or benchmark patterns.
- C# orchestrates and marshals. Core math belongs in engine C/C++ kernels or existing native interop; do not inline new math truth in C# when an engine kernel exists or should exist.

## Implementation posture

- Follow the existing ETL shape in `SafetensorsContainerParser`, `WeightTensorETL`, and `ModelTableETL` before designing new abstractions.
- Prefer deterministic, content-addressed, idempotent deposition. Re-ingest should be a no-op or a guarded refusal, not double-counted evidence.
- Keep context entities explicit for model layer/head/circuit testimony. Referenced entities must be deposited before relations that point at them.
- When uncertain, read `docs/EPISTEMOLOGY.md`, `docs/INGESTION.md`, `docs/ARCHITECTURE.md`, `docs/GEOMETRY.md`, and `docs/OPEN-PROBLEMS.md` before changing behavior.