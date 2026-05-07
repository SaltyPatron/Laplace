/*
 * hilbert.h — HilbertCurveService public API.
 *
 * Phase 2 / Track B / Service B9.
 *
 * 4D Hilbert space-filling curve — Skilling 2003, "Programming the Hilbert
 * Curve". Maps quantized 4D lattice coordinates to a 1D Hilbert index that
 * preserves locality (nearby Hilbert indices ↔ nearby 4D points). Used by
 * Gist4DService (B10) for the linearization key on POINT4D and as a
 * secondary B-tree index alongside the GiST + SP-GiST indexes.
 *
 * Substrate use: S³ points have x,y,z,w in [-1, +1]. Quantize to 16 bits
 * per axis ⇒ 64-bit Hilbert index. Fits in a bigint column.
 */

#ifndef LAPLACE_HILBERT_H
#define LAPLACE_HILBERT_H

#include <stdint.h>

#include "laplace_pg/geometry4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Quantize an S³ POINT4D (each component in [-1, +1]) to 16-bit-per-axis
 * lattice coordinates and emit a 64-bit Hilbert index. Out-of-range values
 * are clamped. */
uint64_t laplace_hilbert_point4d_to_index(const laplace_point4d_t *p);

/* Inverse: 64-bit Hilbert index → quantized POINT4D (loses precision below
 * 2^-15; useful for index → grid-cell decoding only). */
void laplace_hilbert_index_to_point4d(uint64_t h, laplace_point4d_t *out);

/* Direct integer interface (used internally and by tests). */
uint64_t laplace_hilbert_xyzw_to_index(uint16_t x, uint16_t y, uint16_t z, uint16_t w);
void     laplace_hilbert_index_to_xyzw(uint64_t h,
                                       uint16_t *x, uint16_t *y,
                                       uint16_t *z, uint16_t *w);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_HILBERT_H */
