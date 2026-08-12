# GPU vs CPU — `bilinear_edges_tile`

**Date:** 2026-08-12
**Host:** hart-server — i7-6850K 6c/12t, GTX 1080 Ti (Pascal, sm_61, 11 GiB), MKL/TBB 2026.1, CUDA 12.6
**Tool:** `engine/dynamics/tools/bilinear_edges_bench/bench_bilinear_edges.cu`
**Question:** the model lane spends its time in `C = L·Rᵀ` → threshold → emit
COO + int64 score. Is a GPU worth wiring for it, and what breaks if it is?

The CPU side calls the **shipped** `bilinear_edges_tile` out of
`liblaplace_dynamics`. It is not a reimplementation — benchmarking a lookalike
would have measured the benchmark.

## Measured

4096 × 4096 over r = 64 (16,777,216 pairs). Deterministic LCG inputs in [-1,1],
so a rerun is comparable to this one.

| theta | edges | CPU fp64 | GPU fp64 | GPU fp32 | fp32 vs CPU fp64 |
|---|---|---|---|---|---|
| 12.0 | 119 | 100.29 ms | 9.02 ms (11.1×) | 1.45 ms (69×) | exact set, worst Δscore 12 |
| 10.0 | 3,014 | 89.22 ms | 8.93 ms (10.0×) | 1.54 ms (58×) | exact set, worst Δscore 20 |
| 8.0 | 46,106 | 111.89 ms | 9.42 ms (11.9×) | 2.21 ms (51×) | **+1 edge**, worst Δscore 28 |

GEMM alone: fp64 **274–279 GFLOP/s** (≈78% of Pascal's 1/32-rate fp64 ceiling),
fp32 **2,429–2,611 GFLOP/s**. GPU totals include H2D, kernel and D2H.

Scores are `laplace_score_fp(v, 1.0)` on a 1e9 scale, so Δ28 is ~3×10⁻⁸ relative.

## What the numbers say

**GPU fp64 reproduces the shipped function exactly.** Zero score deviation at
every density, identical edge sets. 10–12× for free, and nothing about identity
changes. This is the conservative option and it is not a lateral move, contrary
to the earlier estimate made from peak-rate arithmetic alone.

**GPU fp32 is 51–69×, and it changes the edge SET, not just values.** At 46,106
edges it emitted one edge the CPU did not: a pair whose fp32 dot product crossed
`theta` when its fp64 value did not. 0.002%. That is the real failure mode — not
drift in a magnitude, but a different set of survivors at the threshold boundary.

That is tolerable *here specifically*. These scores become Glicko votes, and a
boundary flip contributes a vote whose magnitude sits exactly at `theta` — the
weakest testimony available — into a fold that is smooth in it
(`witnessWeight = ½(1 + tanh(m/M))`). The same flip anywhere near a content hash,
a `coord`, a `hilbert_index` or a trajectory would be a different identity.

**The line, now measured rather than argued:**

> GPU FP32 for contraction magnitudes feeding Glicko.
> Never for identity, placement, or ordering.

## The finding that is not about the GPU

CPU time barely moves with edge count: 89.22 ms at 3,014 edges, 111.89 ms at
46,106. The emit loop is not the cost. `bilinear_edges_tile` allocates a dense
`t × n_right` fp64 buffer — 134 MB at this tile size — fills it with DGEMM, and
scans all 16.7M entries to keep 0.3% of them. Allocation plus a full
memory-bandwidth-bound scan dominates, and it is paid whether one edge survives
or a million.

That is addressable on the CPU alone, independently of any GPU decision.

## Not established

- One host, one shape (4096², r=64), synthetic inputs. Real attention tiles have
  structure this does not.
- Emit ordering on the GPU is `atomicAdd` slot allocation, so it is
  nondeterministic run to run. The benchmark sorts before comparing. Anything
  consuming this path must not depend on emission order — which is itself a
  demonstration of why it cannot feed an ordered structure.
- Not wired into CMake. Enabling the CUDA language in the shared build changes
  the toolchain for every target to benchmark one function; build instructions
  are in the tool's header.
