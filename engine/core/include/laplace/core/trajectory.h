#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Trajectory primitives — a trajectory is a mantissa-packed LINESTRING
 * whose vertex coords carry constituent-entity hash bits in their FP64
 * mantissas (per ADR 0012).
 *
 * No custom geometry struct: per RULES.md R22, the geometry is an
 * `LWLINE` (from liblwgeom) at the PG-wrapper layer. Engine kernels
 * operate on raw XYZM-packed double buffers (matches POINT4D layout).
 *
 * Read patterns:
 *   - At ingest: pack N constituent hashes into N mantissa-packed XYZM
 *     points (one point per constituent).
 *   - At cascade read: stream the LINESTRING's POINT4Ds, unpack each
 *     vertex's mantissa to recover the constituent hash and position.
 *
 * The PG wrapper handles GSERIALIZED ↔ POINT4D buffer marshalling via
 * lwgeom_from_gserialized + getPoint4d(); the engine sees the raw
 * `double*` buffer with n_points*4 doubles. */

/* Pack N constituent hashes into a mantissa-packed XYZM buffer.
 * `out_xyzm` must have capacity for `n * 4` doubles. */
int trajectory_build(const hash128_t* constituent_hashes,
                     size_t           n,
                     double*          out_xyzm);

/* Unpack a mantissa-packed XYZM buffer back to constituent hashes.
 * `trajectory_xyzm` is `n_points * 4` doubles. */
int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap);

#ifdef __cplusplus
}
#endif
