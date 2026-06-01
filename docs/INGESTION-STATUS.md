# Model-ingestion status — what's fixed, what's still broken

**Status of record for AI-model ingestion.** Read with `docs/SUBSTRATE-FOUNDATION.md` (the ratified
lens). Updated 2026-06-01. The point of this file: stop re-breaking things that were already corrected.

## The law (never violate)

- **Content is identity. Source / model / layer / head / position / magnitude / time are WITNESSES,
  never identity.** Same content → same hash, regardless of which model/neuron/source produced it.
  Putting *anything* source-derived into an entity id (e.g. ordering an n-gram's constituents by weight
  magnitude) breaks dedup and is the cardinal sin.
- **Ingestion is O(params) streaming, never a recompute / GEMM / vocab² bilinear.**
- **No top-k / lottery-ticket truncation.** Noise floor only (drop genuine ~0 jitter); magnitude lives
  in the Glicko-2 μ, so selectivity is `ORDER BY μ` at query time, never discarded at ingest.
- **Evidence keeps provenance (source, layer, head, time); consensus drops it** (one row per
  (subject, kind, object), Glicko-2-accumulated over all witnesses).

## Working + verified

- **Generic shape-based circuit detection** — `ModelGeometry.Detect` finds QK/OV/FFN from tensor SHAPE +
  universal HF config dims, NO per-family code. Cross-model test passes on TinyLlama (GQA) and Phi-2
  (MHA, `dense`/`fc1`/`fc2`). `ModelGeometryTests`.
- **O(params) address-book ingest** — `WeightTensorETL.EmitCircuitMemoriesAsync`: build `addr[m]`
  (dim→token) once from the embedding, then stream each weight CELL and resolve via `addr`. NO E·W vocab
  projection (that was O(vocab·params)), no vocab² pairs. Verified: TinyLlama ~31s, Phi-2 ~90s; unicode +
  both models ~5 min total; ~1.9 GB DB at the (now-removed) 0.05% floor.
- **Cross-dtype decoder** — `LoadRawBF16AsF32`: F32/F64, F16, BF16, F8_E4M3, F8_E5M2, I8/16/32/64, U8,
  BOOL; fail-loud on unknown (GGUF block-quant is a separate container — not handled).
- **Sharded safetensors** — `SafetensorsContainerParser.ParseModel` unions all `*.safetensors`; each
  tensor carries `FilePath`. Single-file + sharded uniform.
- **Per-model source identity** — `ModelDecomposer.SourceForModel(modelDir)` (HF `models--ORG--NAME` →
  `ORG/NAME`). No hardcoded TinyLlama. Guard/synth/stats use it; stats counts model kinds globally.
- **Recipe null-safe** — `GetDoubleOr`/`GetInt` tolerate JSON null (Phi `rms_norm_eps:null` →
  `layer_norm_eps`).
- **Deterministic QK projection** — `qk_project_cached.cpp` reverted from 8-lane plain to serial Neumaier;
  bit-identical to the all-pairs reference; parity test un-skipped, 6/6.
- **All circuits emitted** — QK→ATTENDS, OV→OV_RELATES, FFN→COMPLETES_TO, each with its (layer,head)
  Witness as `attestations.context_id` (evidence provenance).
- **Re-ingest guard** keys on COMPLETES_TO (idempotent; no second DB hammer).
- **Top-k floor removed** — absolute noise floor `LAPLACE_CIRCUIT_FLOOR`, default 0 (keep all real cells).

## Broken / being unfucked (in this pass)

- **[CARDINAL] n-gram id was source-tainted** — constituents were ordered by weight |magnitude|
  (source/position) → same token-set → different Merkle id per witness → NO dedup. FIX: order by content
  (`Hash128.CompareToBytewise`), magnitude only in μ. (`WeightTensorETL.EmitUnit`.)
- **Consensus kept context** — `rebuild_consensus` GROUP BY included `context_id`, and layer/head is in
  context → witnesses never collapsed (consensus rows == evidence rows; zero dedup). FIX: key consensus on
  (subject, kind, object) only; drop source AND context. (`13_consensus.sql.in`.)
- **Queries were ad-hoc** — ranked-μ / completions / dedup-stats were hand-run each session. FIX: permanent
  substrate functions (`top_relations`, `completions`, `consensus_stats`) in the extension SQL.

## Still open (next)

- **Morph (LE→GSO→Procrustes)** gated off (`LAPLACE_SKIP_MORPH`): dense eigenmaps is O(n²·d). It gives the
  spatial/locality axis (GIST `physicalities_coord_gist` + Hilbert) and the export placements, but n-dim→4D
  is lossy. Either drop it (relations carry the content) or make its affinity the streamed sparse graph.
- **Dedup magnitude vs granularity** — per-neuron n-grams may be too specific to recur; token→token recurs
  most. Measure `consensus_stats` after the cardinal fix; decide granularity.
- **Semantic validation** — decode relations to surfaces; verify a known fact (e.g. capital-of-France).
- **Synthesis / GGUF re-export** reads the OLD Q_PROJECTS (`Program.cs` SynthesizeAsync) → broken; needs
  rework to read the new records + placements.
- **Breadth** — GGUF block-quant (Flux, `gguf/`), MoE (Qwen3/DeepSeek; detector groups by expert but
  untested), non-text modalities (the n-gram trajectory entity is already the modality-blind unit; only the
  front-end atom extractor is modality-specific).
- **Granular test suite** — have ModelGeometry cross-model + QK parity; owe NgramTrajectory (content-id
  stability/dedup), Math4d.Centroid, address-book, run-to-run determinism, recipe, tokenizer.
