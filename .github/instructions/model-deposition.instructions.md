---
applyTo: "{app/Laplace.Decomposers.Model/**,app/Laplace.Engine.Synthesis/**,engine/synthesis/**,docs/**}"
description: "Use when touching safetensors, model ETL, model deposition, the foundry (synthesize substrate), or ingestion docs."
---

# Laplace Model Deposition Rules

Laplace does not treat AI models as oracles, training targets, embedding stores, raw-weight
archives, or generic ML artifacts. A model is a witness package: ingest unboxes its testimony
and throws away the packaging; export pours adjudicated consensus into a new mold. Remove the
product from the packaging and throw away the packaging.

## Safetensor snapshots vs GGUF

- **Ingest unit** = HuggingFace snapshot **directory**: `config.json` (recipe) + `tokenizer.json` (vocab/merges) + one or more `*.safetensors` (named weight tensors). Validated by `SafetensorSnapshotWitness` — a lone `.safetensors` file is never sufficient.
- **GGUF** = the foundry's render target: self-contained (metadata + tokenizer + tensors in one file). Do not conflate ingest (distributed snapshot witness) with export (cast GGUF artifact).
- CLI: `laplace ingest safetensors <snapshot-dir>`. `ingest model` is legacy alias only.

## Safetensors and model files

- Read safetensors through normal file operations: parse the 8-byte little-endian header length, parse JSON tensor metadata, then seek to `HeaderBytes + data_offsets` and stream/read tensor bytes from the file.
- Safetensors bytes and decoded tensor cells are transient input only. Do not store raw model files, raw weights, tensor rows, tensor cells, or reconstructable tensor payloads in substrate tables. Glicko-2 state is adjudication, never a float codec — a stored per-witness score that inverts to a weight is value-channel smuggling and is banned.
- Do not replace this path with framework loaders, Python model APIs, hidden inference calls, or assumptions that the whole model must be materialized through a neural runtime.
- Preserve dtype semantics. Decode supported safetensors numeric/bool dtypes explicitly. For unsupported or different containers, fail loudly. Never ingest zeros or placeholders as a convenience.
- Treat tensor names, shapes, dtype, offsets, and source file path as container facts. Do not infer missing tensors by pattern unless the model recipe explicitly defines the equivalent.

## What deposition means here

- ALL model math reduces to token→token relations. Every operation in the stack is either a direct A↔B comparison or a set-against-set comparison; norms, softmax, activations, and residuals are calibration/aggregation of those comparisons and fold away. `ModelTableETL` is the deposition: tensor bytes feed four token→token relation types — `SIMILAR_TO` (embedding cosine), `ATTENDS` (γ-folded Q·K bilinear), `OV_RELATES` (V·O projection vs unembedding), `COMPLETES_TO` (FFN contraction vs unembedding) — plus tokenizer testimony (`MERGES_WITH`, `TOKEN_MAPS_TO`), recipe scalars (`HAS_*`), and the S3 morph (per-witness Projection physicalities). Intermediate spaces (neuron, attn_dim, kv_dim, layers, heads) are contracted away inside native kernels and never surface as substrate entities or relation types.
- Layer is witness provenance, never identity: it rides `context_id` (a `Model_Layer` context entity), so the same token pair folds across layers — cross-layer agreement is the strongest testimony. Anything that puts layer, head, or dim index into a relation TYPE or an entity id is the condemned cell-archive pattern; the tombstone test `TensorRoleArenas_Purged_FallToProbationary` pins it dead.
- Record counts are claim-shaped (above-noise token pairs, Zipf-bounded, folding across witnesses), never parameter-shaped. A bigger model testifies more reliably about the same claim space; it does not get more rows.
- A deposed model has trust class `AIModelProbe`. Its testimony is admissible and intentionally outranked by curated sources such as dictionaries, standards, and academic resources.
- Per-witness magnitude is consumed during ingest/adjudication. Persisted `laplace.attestations` stores provenance, outcome class, timestamps, and counts; `laplace.consensus` stores Glicko-2 state — no raw values, no reconstructable per-witness scores.

## What export means here (the foundry)

- `laplace synthesize substrate` pours consensus into a mold. The mold (architecture recipe) is user-authored or discovered from a deposed model (`--recipe-from`; recipes round-trip via `laplace.model_recipes()` — the recipe entity id is Blake3 of the canonical config JSON, registered verbatim in canonical_names).
- Nothing in the foundry reads model weights, original norms, or any witness file beyond the mold's recipe/tokenizer. There is no inverse score law, no fidelity-to-original metric, no per-layer reconstruction target. The original witness's floats are not a referent: consensus differs from every witness and owes none of them its handwriting.
- The basis is GENERATED: Laplacian eigenmaps over the union of consensus planes and content-trajectory pairs (`content_trajectory_pairs` over `content_index` — the "followed by N% of the time" distributions read from the usage observations), Gram-Schmidt orthonormalized, Procrustes-anchored to token content coordinates, deterministic capacity fill (seeded from the recipe, never the clock), plus a bias channel that keeps the SwiGLU gate a stable scalar. Interior tensors are truncated-SVD factorizations of the consensus operators projected through that basis (`compute_substrate_gram` → `tensor_svd_truncate`), distributed across layers by uniform residual split.
- Any mold tensor the foundry does not define is a hard error — never a zero-fill, never a copy from someone else's tensors.
- Acceptance = conventional function: the cast GGUF loads and runs as a normal, non-faulty model via conventional means, CPU-only (`scripts/model-forward-oracle.py forward-gguf` is the instrument), and its behavior tracks the substrate's own Glicko-ordered walks. Numeric agreement with any ingested model is not a goal and not a metric.

## Geometry and math boundaries

- Truth lives in relational consensus. Geometry serves two roles: per-witness specimens (audit instrument: S3 morph placements in `physicalities`) and the foundry's rendering instruments (LE/GSO/Procrustes generate export spaces). It is never the truth engine.
- C# orchestrates and marshals. Core math belongs in engine C/C++ kernels or existing native interop; do not inline new math truth in C# when an engine kernel exists or should exist.

## Implementation posture

- Follow the existing shape in `SafetensorsContainerParser`, `WeightTensorETL`, `ModelTableETL` (deposition) and `FoundryExport` + `SynthesizeFromSubstrateAsync` (export) before designing new abstractions.
- Prefer deterministic, content-addressed, idempotent deposition. Re-ingest should be a no-op or a guarded refusal, not double-counted evidence. The foundry is deterministic per machine: same consensus + same mold ⇒ same cast.
- Entity-id derivation is perf-cache territory (`CodepointPerfcache` + `HashComposer`) — O(tier), zero DB round trips; the DB is read set-based, never per token.
- Adding a modality or architecture means adding `PathSpec` entries to `ArchitectureProfile` — tokens for an LLM just happen to be text; the reduction to token→token is the same for any modality.
- When uncertain, read `docs/EPISTEMOLOGY.md`, `docs/INGESTION.md`, `docs/ARCHITECTURE.md`, `docs/SYNTHESIS.md`, and `docs/OPEN-PROBLEMS.md` before changing behavior.
