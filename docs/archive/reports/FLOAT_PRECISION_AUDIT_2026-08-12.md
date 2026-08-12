# Float precision audit — is FP64 load-bearing?

**Date:** 2026-08-12
**Host:** hart-server (i7-6850K 6c/12t, 125 GB, GTX 1080 Ti 11 GB, MKL/TBB 2026.1)
**Question:** the engine calls `cblas_dgemm` in 11 places. Is double precision
required by the invention, or inherited by default? And what does the answer
unlock?

## Measured, this host

4096×4096 GEMM, torch 2.6.0+cu124 (GPU) and MKL 6 threads (CPU):

| | time | throughput |
|---|---|---|
| GPU FP32 | 14.9 ms | **9.21 TFLOP/s** |
| GPU FP64 | 339.1 ms | 0.41 TFLOP/s |
| CPU FP32 | 316.8 ms | 0.434 TFLOP/s |
| CPU FP64 | 546.0 ms | 0.252 TFLOP/s ← current |

- Keep FP64 and add the GPU: **1.6×**. Not worth wiring.
- Move to FP32 and add the GPU: **36×** over today.
- Move to FP32 and stay on Intel: **1.7×**, from AVX2 lane width alone.

The 22.5× GPU FP32:FP64 gap is Pascal's 1/32 FP64 rate — a market segmentation
decision by NVIDIA, not a property of the workload. Any consumer GPU, ARM core,
or embedded target has the same shape: FP32 at full rate, FP64 crippled or
absent. FP32-only is what makes the engine portable to hardware that has no
FP64 units at all.

Incidental finding: `torch.cuda.get_arch_list()` on the 2.6.0+cu124 wheel is
`[sm_50, sm_60, sm_70, sm_75, sm_80, sm_86, sm_90]` — **`sm_61` is absent and
the 1080 Ti (cc 6.1) runs it anyway at 9.21 TFLOP/s.** CUDA is binary
compatible within a major compute capability, so the sm_60 cubins load on a 6.1
device. Do not gate a Pascal decision on `sm_61` appearing in that list.

## Determinism does not require FP64

IEEE-754 binary32 is exactly as reproducible as binary64: same inputs, same
operation order, bit-identical output. What buys determinism here is
`-fno-fast-math -ffp-contract=off` plus fixed reduction order, and neither cares
about mantissa width. Content addressing over FP32 hashes as stably as over
FP64.

What FP64 buys is dynamic range and accumulated-error headroom, which matters
for long reduction chains and ill-conditioned decomposition — not for
reproducibility.

## The correct precision placement is already in the codebase

`engine/core/src/score.c`:

```c
int64_t laplace_score_fp(double v, double m) {
    double s = 0.5 * (1.0 + v / (m + fabs(v)));   // squash to [0,1]
    return (int64_t)llround(s * 1e9);              // ~2^30 levels
}

void laplace_score_batch_fp(const float* w, size_t n, double m, int64_t* out) {
    double v = (double)w[i];                       // widen per element, HERE
    ...
}
```

The batch scorer takes **fp32 in**, widens per element for the squash, and emits
`int64`. That is precision spent where it is nearly free (O(n) squash) and saved
where it dominates (O(n³) GEMM). The rest of the engine should follow the
pattern this function already sets.

## Per call site

| site | inputs | verdict |
|---|---|---|
| `bilinear_edges.cpp:75` `project_embedding` | `const float*` ×2 | **Pure waste.** Allocates two `vector<double>`, widens every element, then DGEMMs. The inputs are already fp32 — there is no information to preserve. |
| `bilinear_edges.cpp:96` `project_embedding_d` | `double* pts`, `float* W` | Half waste — widens `W` for no reason. |
| `bilinear_edges.cpp:31` `bilinear_edges_tile` | `double*` | Output is thresholded on `theta`, then quantized through `laplace_score_fp` to `int64`. FP32 safe. |
| `ffn_edges.cpp` ×4 | `double*` (emb, unemb, gate, up, down) | Source is fp16/bf16 safetensors widened upstream. FP32 safe on information grounds. |
| `arch_template.cpp:383,402` `compute_substrate_gram` | `double*` | Same provenance. Gram matrices are the one place to watch accumulation depth. |
| `model_math.cpp:286,291` | `double*` | Same provenance. |
| `tensor_decompose.cpp:30` `LAPACKE_sgesdd` | `float` | Already single. Correct, and the best GPU candidate in the tree — SVD is iterative and stays resident. |

## The risk, which is not precision

FP32 GEMM changes the *exact* `int64` scores. Relative error ~1e-7 propagates
through the squash to roughly **±100 units out of 1e9**. For ranking,
thresholding, and retrieval that is invisible.

If the attestation hash covers the score, every previously computed attestation
changes identity. That makes the migration a **re-ingest**, not a bug — and the
database was recreated on 2026-08-11, so it is the cheapest it will ever be.

**Open question:** does the attestation content address include the score?

## Recommended order

1. `project_embedding` → `cblas_sgemm` on the original `float*`. Strict win,
   zero semantic change: deletes two allocations and two conversion passes over
   `n*d + r*d` elements. Measure alone.
2. Determine whether scores are hashed.
3. If not hashed: convert the dynamics/synthesis GEMM path to FP32 and the
   engine becomes GPU-eligible at 9.21 TFLOP/s for the model ingest/export lane.
4. If hashed: same conversion, scheduled with a re-ingest.

## Scope note

GPU is under evaluation for **model ingestion and export only**. The Laplace
substrate and inference path remain CPU-only by operator mandate — that lane's
claim is that inference is attestation lookup, not GEMM, and accelerating a GEMM
there would contradict the premise rather than support it. Model ingest/export
is format conversion and tensor decomposition, where a GPU is only a faster way
to do arithmetic nobody claims is inference.
