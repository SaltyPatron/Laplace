#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Circuit extraction — the O(params) address-book read of a model's weight matrix, moved
 * off the managed scalar path into the engine (TBB-parallel, exact). This is the hot loop
 * of AI-model ingestion: for each hidden unit (row), find the weight cells above the noise
 * floor and resolve each to the token its dimension points to (the embedding address book).
 *
 * `addr[m]` = the token index dimension m points to (argmax_t |E[t,m]|), or -1 if none —
 * see `build_address_book`. The caller passes row-major weights (decode tensors are
 * transposed to [n_units × d_model] first so the scan is always contiguous).
 *
 * Output: one record per surviving (unit, dimension) — (unit, token, signed value). The
 * token may repeat within a unit (several dims point to it); dedup/compose is the caller's
 * cheap per-unit step. Records are ordered (unit asc, column asc) → deterministic regardless
 * of thread count. The magnitude is the SIGNED weight (sign carries confirm/refute for the
 * signed Glicko-2 vote); the floor compares |value|. */

typedef struct {
    uint32_t unit;    /* hidden-unit (row) index */
    int32_t  token;   /* resolved token index (addr[column]) */
    float    value;   /* signed weight cell value */
} circuit_cell_t;

/* MODEL INSPECTOR — detect a viable noise floor for ANY weight tensor, model-agnostically.
 * No hardcoded value, no per-family table: the floor is chosen from THIS tensor's own
 * magnitude distribution so the KEPT cells (|w| > floor) retain `target_energy` (e.g. 0.99)
 * of its Frobenius energy (Σ w²). The high-energy subnetwork is the lottery ticket — the part
 * that carries the layer's behavior — so raising the floor to it stays coherent while dropping
 * the noise tail. Histogrammed (TBB), O(n) — works on billions of cells. `target_energy` ∈
 * (0,1]; 1.0 ⇒ floor 0 (keep all). Returns the floor (≥ 0); negative on bad args (NaN-coded). */
double detect_energy_floor(const float* W, size_t n, double target_energy);

/* Build the address book: addr_out[m] = argmax_{t : valid[t]} |E[t,m]| over the vocab, for
 * each of d_model dimensions; -1 where no valid token resolves it. E is [vocab × d_model]
 * row-major f32. `valid` (length vocab) may be NULL (all valid). TBB-parallel over dims.
 * Returns 0 on success, -1 on null/zero args. */
int build_address_book(const float* E, size_t vocab, size_t d_model,
                       const uint8_t* valid, int32_t* addr_out);

/* Resolve weight rows [u0, u1) of a row-major [n_units × d_model] matrix against the address
 * book, emitting every cell with |value| > floor (and addr[col] >= 0) into `out`. TBB-parallel
 * over the row window; two-pass (count → prefix-sum → fill) so output is dense + deterministic.
 * If the window's survivor count exceeds `cap`, sets *overflow=1, *out_count=0 and emits
 * nothing (caller shrinks the window and retries, like the QK windowing). Otherwise
 * *overflow=0, *out_count = records written. Returns 0 on success, negative on bad args. */
int resolve_matrix(const float* W, size_t n_units, size_t d_model,
                   const int32_t* addr, double floor,
                   size_t u0, size_t u1,
                   circuit_cell_t* out, size_t cap,
                   size_t* out_count, int* overflow);

#ifdef __cplusplus
}
#endif
