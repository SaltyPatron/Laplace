# Synthesis

The export side: pouring adjudicated consensus into conventional-AI molds. A model is something you CAST. Instrument-tier by ruling (2026-06-10): the substrate's own inference walks consensus directly; a cast artifact exists for the legacy ecosystem.

## The inversion

Conventional flow: corpus → months of gradient descent → frozen weights → (opaque) inference.
Laplace flow: witnesses → adjudicated consensus (minutes, attributed) → `synthesize substrate <recipe> <out>` → a build artifact any GGUF runtime executes, with every weight traceable to testimony.

The model file is demoted to a **render target** — rebuildable on demand, diffable build-to-build (deltas name their witnesses), exportable at any dimension (the mold picks the shapes), disposable. The substrate stays alive; the artifact is a cache for the legacy ecosystem.

The original witness's floats are never a referent. Consensus differs from every ingested witness and owes none of them its handwriting — export renders consensus, it does not invert an ingest. There is no inverse score law, no calibrated inverse, no per-layer reconstruction target. (The 2026-06-11 ruling killed the cell archive that made inversion thinkable; see `model-deposition.instructions.md`.)

## The foundry (as built)

```
mold     recipe.json (dims/layers/heads — user-authored, or discovered from any deposed
         model via laplace.model_recipes() + synthesize substrate --recipe-from)
  → planes      one set-based read per token→token consensus plane (SIMILAR_TO, ATTENDS,
                OV_RELATES, COMPLETES_TO, PRECEDES, CO_OCCURS_WITH; eff-μ = rating − 2·rd)
                PLUS the usage observations themselves: content_trajectory_pairs straight
                off the witnessed trajectories (word-stride) — "followed by N% of the
                time", windowed co-occurrence
  → basis       laplacian_eigenmaps_from_sparse_graph over the union graph (degree-capped)
                → gram_schmidt → procrustes anchored to token content coords → deterministic
                capacity fill (seeded from the recipe, never the clock) → bias channel
                (keeps the SwiGLU gate a stable scalar; softmax-invariant at the lm_head)
  → operators   compute_substrate_gram projects each operator through the basis (Eᵀ·A·E);
                attention = ATTENDS ∪ co-occurrence ∪ windowed trajectory pairs,
                completion = COMPLETES_TO ∪ PRECEDES ∪ trajectory continuation
  → factors     tensor_svd_truncate at the mold's ranks (kv_dim for attention, interm for
                FFN); uniform residual split across layers ((1/L)^½ per factor); γ = ones
  → gguf_writer llama.cpp-conformant GGUF (KV metadata + aligned tensor blobs)
CLI: Laplace.Cli synthesize substrate <config.json> <out.gguf>
     gates: 'basis generated', 'synthesis complete'. Any mold tensor the foundry does not
     define is a hard error — never a zero-fill, never a copy from witness tensors.
```

Models, corpora, and the witnessed sequences all pour into the same mold: a cast model contains book testimony, dictionary testimony, and model testimony at whatever ranks the adjudication gave them (AIModelProbe is intentionally outranked by curated sources).

## Model deposition (the other direction, summarized)

`ModelTableETL`: token→token behavioral planes only (SIMILAR_TO / ATTENDS / OV_RELATES / COMPLETES_TO), intermediate spaces contracted inside native kernels, layer as `context_id` provenance (cross-layer agreement folds — it is the strongest testimony), claim-shaped record counts. Plus tokenizer testimony (MERGES_WITH, TOKEN_MAPS_TO), recipe scalars (HAS_*), and the S3 morph (per-witness Projection physicalities). See `model-deposition.instructions.md` for the full law.

## Validation: behavioral, not bitwise

Fidelity criterion = conventional function: the cast GGUF loads and runs as a normal, non-faulty model via conventional means, CPU-only, with no degradation vs a structurally comparable model — and its behavior tracks the substrate's own Glicko-ordered walks. Numeric agreement with any ingested model is not a goal and not a metric; an original witness is at most a baseline to beat on the same harness (same mold + better data ⇒ the comparison is apples-to-apples).

- Instrument: `scripts/model-forward-oracle.py` — exact f64 llama-family forward pass with numpy, deliberately ZERO Laplace code in the path. Modes: forward (next-token from safetensors), embed, forward-gguf (same pass reading the CAST GGUF — the acceptance probe).
- Harness: llama_behavioral — N prompts, greedy CPU decode, JSON of continuations + tok/s (Windows binary home: `D:\LlamaCPP\llama-completion.exe`; prompts: scripts/prompts_smoke.txt).
- Substrate cross-check: `walk-verdict.cmd` / `scripts/sql/model-planes-audit.sql` — the cast model's top tokens should overlap the walk's COMPLETES_TO/ATTENDS neighbors.

## Artifact classes

1. **Same-mold recast**: depose snapshot A → pour A's discovered mold from consensus → behavioral comparison vs A on the same harness. Same shapes, same runtime cost; the only variable is the data. Disagreement in the direction of higher-ranked witnesses is the product working.
2. **Clean-room model**: cast from curated-witness consensus with zero model ancestry — every weight traceable to enumerated, licensed sources. A certifiable artifact class nothing trained can match.
3. **No-ancestor compile ("a model trained by reading")**: literature → sequence consensus + content trajectories → cast tensors. The text→tensor bridge this required (formerly the load-bearing open lemma) is the foundry basis/operator path above; what remains open is generative QUALITY at scale, measured on the behavioral harness.
4. **Resized casts**: same substrate, any mold — edge-sized builds on demand (distillation without a teacher-student loop or its license entanglement). Lottery-ticket sparsity is inherent: consensus only holds above-noise claims, and SVD truncation prunes further (`LAPLACE_FOUNDRY_REL_ERR_TOL`).

## Strategic consequences (kept honest)

- Backwards compatibility with the entire deployed ecosystem (llama.cpp never learns nobody trained the file).
- Model versioning becomes meaningful: build deltas ↔ consensus deltas ↔ named witnesses.
- "Training data extraction" inverts from attack to FEATURE with a warranty (Merkle reconstruction).
- The unproven frontier stays labeled: generative quality of cast artifacts (esp. class 3) is the bet; the harness exists precisely because the claim is falsifiable.
