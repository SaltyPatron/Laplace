#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Faithful contracted-operator edge extraction — the model-evidence core.
 *
 * A model's interior tensor is a token×token bilinear through the embedding:
 * QK = E·Wq·Wkᵀ·Eᵀ, OV = E·Wv·Wo·E_Uᵀ, FFN = (E·Wup)·(E_U·Wdown)ᵀ. In every
 * case the operator factors as  M = Left · Rightᵀ  where Left = E·Wenc and
 * Right = E_U·Wdec are the projected embeddings (cache them once per circuit).
 * This kernel materializes M tile-by-tile and emits EVERY (i,j) whose signed
 * value exceeds the coherence threshold `theta`.
 *
 * INVARIANTS (ARCHITECTURE.md §model-ingest):
 *   - The ONLY cut is `theta` — the coherence floor a global fidelity budget β
 *     derives from the data per circuit. NEVER argmax (one token per dim — the
 *     lottery-ticket rape), NEVER top-k / per-row top-k, NEVER an a-priori
 *     energy %. You fix the fidelity kept; the data fixes the threshold.
 *   - SIGNED values are emitted verbatim (attract > 0 / repel < 0) — the signed
 *     magnitude feeds the Glicko win/loss outcome; never |value|, never reduced.
 *   - f64 throughout; the contraction is an exact MKL dgemm (CBWR-locked at
 *     laplace_dynamics_init ⇒ deterministic), and the emit order is a fixed
 *     row-major scan ⇒ bit-reproducible output.
 *   - M is NEVER materialized dense over the full vocab: the caller tiles row
 *     ranges so peak memory is O(tile_rows × n_right), not O(n_left × n_right).
 *
 * Processes Left rows [row_begin, row_end) against all of Right; emits GLOBAL
 * row indices (row_begin + local). Caller loops tiles, draining `out_*` each
 * call. On buffer overflow sets *overflow=1 and stops (caller retries with a
 * larger cap or a smaller tile).
 *
 *   left      : [n_left  × r] row-major f64 (projected, e.g. E·Wq).
 *   right     : [n_right × r] row-major f64 (projected, e.g. E_U·Wk).
 *   r         : inner contraction dim (head dim / intermediate rank).
 *   theta     : coherence threshold; |M[i,j]| > theta is kept.
 *   out_rows/out_cols : caller buffers length >= cap; global (i, j) of each edge.
 *   out_vals  : caller buffer length >= cap; the SIGNED M[i,j].
 *   cap       : capacity of the out_* buffers.
 *   out_count : receives the number of edges written.
 *   overflow  : set to 1 iff cap was hit before the tile finished.
 *
 * Returns 0 on success; -1 on bad args; -2 if MKL is unavailable. */
int bilinear_edges_tile(
    const double* left,  size_t row_begin, size_t row_end,
    const double* right, size_t n_right,
    size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals,
    size_t cap, size_t* out_count, int* overflow);

/* Project an embedding through a circuit weight: out [n × r] = pts [n × d] · Wᵀ,
 * where W [r × d] row-major (safetensors out_features × in_features). This forms
 * the Left/Right operands the bilinear contraction consumes (E·Wq, E·Wv, E·Wup,
 * E_U·Wdownᵀ, …). f32 in → exact f64 dgemm → f64 out (the projection is cached
 * once per circuit, then reused across row tiles). Returns 0, -1 bad args, -2 no MKL. */
int project_embedding(const float* pts, size_t n, size_t d,
                      const float* W, size_t r, double* out);

#ifdef __cplusplus
}
#endif
