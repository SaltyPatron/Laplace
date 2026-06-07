# Synthesis

The export side: pouring adjudicated substrate into conventional-AI artifacts. A model is something you COMPILE.

## The inversion

Conventional flow: corpus → months of gradient descent → frozen weights → (opaque) inference.
Laplace flow: witnesses → adjudicated arenas (minutes, attributed) → `synthesize substrate <recipe> <out>` → a build artifact any GGUF runtime executes, with every weight traceable to testimony.

The model file is demoted to a **render target** — like a language is a render choice for realize(): rebuildable on demand, diffable build-to-build (deltas name their witnesses), exportable at any dimension (recipe-agnostic target_dim), disposable. The substrate stays alive; the artifact is a cache for the legacy ecosystem.

## Pipeline (as built)

```
recipe.json (the mold: dims/layers/heads — an architecture config, NOT a checkpoint)
  → arch_template       tensor inventory for the target architecture
  → arena pours         consensus → tensors:
      EMBEDS            token×channel arena → embedding matrix
      Q/K/V/O_PROJECTS  qk_pairs_threshold(_pruned), qk_project_cached kernels
      GATES/UP/DOWN     FFN arenas
      NORM_SCALES       norm vectors
      OUTPUT_PROJECTS   unembedding
  → tensor_decompose    export-only SVD factoring where the mold demands low-rank shapes
  → gguf_writer         llama.cpp-conformant GGUF (KV metadata + aligned tensor blobs)
  → format_writer       alternative serializations (safetensors-style)
CLI: Laplace.Cli synthesize substrate <config.json> <out.gguf>   (gates: 'synthesis complete',
     'consensus arenas poured' — pours read consensus directly; no spectral re-derivation)
```

Inverse-encode law: the exporter must invert the ingest encode (s = ½(1+tanh(w/M)) → Glicko μ) via the CALIBRATED inverse — a forward table built by running the substrate's own accumulate kernel over a w-grid, then monotone interpolation (validate-arena-reconstruction.py measures both naive and calibrated reconstruction; tanh saturation bounds recoverable magnitude — by design, lottery-ticket salience over magnitude fidelity).

## Model deposition (the other direction, summarized)

Cell ETL: every non-zero weight cell = one adjudicated match under its tensor-role kind; positions aggregate as witnesses; context_id = layer/head circuit entity; M = pooled tensor RMS; lottery-ticket sparsity (flat noise thresholds are banned as a class). Re-ingest refused (double-counting). Geometry side: LE+GSO+PA fireflies per circuit (GEOMETRY.md).

## Validation: behavioral, not bitwise

Fidelity criterion = behavior: same prompts, same harness, diff continuations. Bit-identity of blobs is explicitly NOT the bar (build determinism is a separate, internal law).

- Ground truth: `scripts/model-forward-oracle.py` — exact f64 llama-family forward pass straight from safetensors with numpy, deliberately ZERO Laplace code in the path (a passing export cannot be an artifact of shared code). Modes: forward (next-token), embed (E·E_Uᵀ row), forward-gguf (same pass reading the EXPORTED GGUF).
- Harness: llama_behavioral — N prompts, greedy CPU decode, JSON of continuations + tok/s (Windows binary home: `D:\LlamaCPP\llama-completion.exe`; prompts: scripts/prompts_smoke.txt).
- Reconstruction meter: validate-arena-reconstruction.py — Pearson/Spearman/sign-agreement/relative-L2 of ŵ vs w per channel, calibrated inverse, saturation census.

## Artifact classes

1. **Round-trip model**: depose model A → re-export at A's recipe → behavioral diff vs A. The compatibility/salvage proof; the open keystone experiment (TinyLlama + Phi-2 staged in D:\Models\hub).
2. **Clean-room model**: export from curated-witness consensus with zero model ancestry — every weight traceable to enumerated, licensed sources. A certifiable artifact class nothing trained can match. Depends on (3) for generative quality.
3. **No-ancestor compile ("a model trained by reading")**: literature → sequence arenas → tensor pours. BLOCKED on the text→tensor bridge (OPEN-PROBLEMS §5): a defined estimator from PRECEDES/CO_OCCURS/COMPLETES_TO statistics into Q/K-affinity and projection pours. The load-bearing open lemma of the whole thesis.
4. **Resized exports**: same substrate, any target_dim — edge-sized builds on demand (distillation without a teacher-student loop or its license entanglement).

## Strategic consequences (kept honest)

- Backwards compatibility with the entire deployed ecosystem (llama.cpp never learns nobody trained the file).
- Model versioning becomes meaningful: build deltas ↔ consensus deltas ↔ named witnesses.
- "Training data extraction" inverts from attack to FEATURE with a warranty (Merkle reconstruction).
- The unproven frontier stays labeled: behavioral parity of pours (esp. class 3) is the bet; the harness exists precisely because the claim is falsifiable.
