# Per-site cancellation analysis — the six DGEMM sites that lost their verdict

**Date:** 2026-08-12
**Context:** GH #1024. `FLOAT_PRECISION_AUDIT_2026-08-12.md` marked `ffn_edges` ×4
and `compute_substrate_gram` ×2 "fp32 safe on information grounds", then withdrew
that verdict — provenance bounds what accumulation *starts* with and says nothing
about what survives it. This is the per-site analysis the withdrawal asked for.

## The distinction that decides it

Composed placements reach `min ‖coord‖ = 0.003148` against unit-norm tier-0 —
~2.5 decimal digits of cancellation — and **those values are kept**. The small
ones *are* the coordinate. Nothing discards them, so fp32's remaining ~4.5 digits
have to carry them.

`ffn_edges` does the opposite. Its output passes `if (fabs(v) > theta)` before
anything downstream sees it. **The threshold discards precisely the values where
cancellation has destroyed the precision.** What survives is the large-magnitude
tail, which is the part with the least relative error.

That is the whole answer for four of the six sites. Cancellation only matters
where cancelled values are retained.

## `ffn_edges.cpp` — 4 sites

| line | operation | contracts over | verdict |
|---|---|---|---|
| 47 | `G = E · gateᵀ` | `d` (embed dim) | **fp32 safe** |
| 53 | `U = E · upᵀ` | `d` | **fp32 safe** |
| 65 | `O = A · downᵀ` | `interm` | **fp32 safe, with a caveat** |
| 81 | `S = O · unembᵀ` | `d` | **fp32 safe** |

**47, 53.** Inputs are model weights of fp16/bf16 provenance. Accumulation depth
is `d`; at d≈4096 the fp32 error is ~√4096 · 1.2e-7 ≈ 7.7e-6 relative. Output
feeds `silu(G)·U`, a smooth bounded nonlinearity — no amplification.

**65.** Deepest of the four, contracting over `interm` (≈11008): ~1.3e-5
relative. **The caveat is the normalisation immediately after it:**

```c
double ss = 0.0;
for (c) ss += Orow[c] * Orow[c];
const double inv = ss > 0.0 ? 1.0 / std::sqrt(ss) : 0.0;
for (c) Orow[c] *= inv;
```

If `O` has cancelled heavily, `ss` is small, `inv` is large, and the *relative*
error is preserved through the normalisation rather than reduced. This is the one
place in `ffn_edges` structurally similar to the placement path. It is still safe
because its output is consumed through the threshold at line 88 — but if a future
caller reads normalised `O` directly, without a threshold, this site changes
class. Worth a comment at the call site rather than a silent assumption.

**81.** `O` is unit-norm by construction, `unemb` is model weights, accumulation
over `d`. Output is thresholded on `theta`, then quantised through
`laplace_score_fp` to an int64 on a 1e9 scale. A 1e-5 relative error is ~1e4
units out of 1e9 — invisible to ranking and to the draw threshold.

## `arch_template.cpp` — `compute_substrate_gram`, 2 sites

| line | operation | contracts over | verdict |
|---|---|---|---|
| 383 | `unary_gram = E_scaledᵀ · E_scaled` | `V` (vocab) | **fp64 required** |
| 402 | `binary_gram = token_basisᵀ · SB` | `V` | **fp64 required** |

Three reasons, none of which the threshold argument reaches — Gram matrices have
no threshold, every entry is retained.

1. **Accumulation depth is the vocabulary.** V ≈ 32k–128k, an order of magnitude
   past anything in `ffn_edges`. At V=128k, fp32 gives ~√128000 · 1.2e-7 ≈ 4.3e-5
   relative *before* conditioning.
2. **Gram matrices are ill-conditioned by construction.** `EᵀE` squares the
   condition number of `E`. Any downstream solve or decomposition multiplies the
   input error by κ, and 4.3e-5 · κ is unbounded in a way 1e-5 through a
   threshold is not.
3. **`E_scaled` widens the dynamic range deliberately:**
   `E_scaled[t] = token_basis[t] · sqrt(per_token[t])`. If `per_token` spans
   orders of magnitude across the vocabulary — which is what a token-frequency
   weighting does — the scaled rows span half that range in magnitude, and fp32
   has 8 exponent bits against fp64's 11 to hold it.

`binary_gram` additionally accumulates `SB` in an **unordered sparse loop**:

```c
for (e < nnz) { Br[d] += w * Ec[d]; }
```

That is order-dependent as well as cancellation-prone — the same non-associativity
that `math4d_centroid` was just fixed for, in a hot loop over `nnz` edges. It is
not currently canonicalised. Independent of the fp32 question, **`SB` is not
reproducible under edge reordering**, and it feeds a Gram matrix that feeds a
decomposition.

## Summary

- **4 of 6 sites are fp32-safe**, because `ffn_edges` thresholds its output and
  the threshold discards exactly the cancelled values.
- **2 of 6 require fp64** — the Gram sites, where accumulation runs over the
  vocabulary, every entry is retained, conditioning squares, and the scaling
  widens dynamic range.
- **One new finding, unrelated to precision:** `SB`'s sparse accumulation at
  `arch_template.cpp:393` is order-dependent. Same defect class as the one just
  fixed in `math4d_centroid`, still live here.
- The `O` normalisation at `ffn_edges.cpp:72` is safe *only* because of its
  consumer. That coupling should be written down where a future caller will see
  it.

Scope unchanged: GPU fp32 for contraction magnitudes feeding Glicko, never for
identity, placement, or ordering. All four fp32-safe sites are on the permitted
side; both fp64 sites are decomposition inputs, which is the other side.
