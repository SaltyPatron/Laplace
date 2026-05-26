#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Trajectory primitives — a trajectory is a mantissa-packed LINESTRING
 * whose vertices reference entities (each playing a constituent role at
 * its vertex position). Each vertex's XYZ encodes the referenced entity's
 * full BLAKE3-128 hash; M encodes per-vertex metadata (ordinal, run_length,
 * flags). Per ADR 0012.
 *
 * No custom geometry struct: per RULES.md R22, the geometry is an
 * `LWLINE` (from liblwgeom) at the PG-wrapper layer. Engine kernels
 * operate on raw XYZM-packed double buffers (matches POINT4D layout).
 *
 * Read patterns:
 *   - At ingest: pack N entity references (with ordinal/run_length/flags)
 *     into N mantissa-packed XYZM points (one point per vertex).
 *   - At cascade read: stream the LINESTRING's POINT4Ds, unpack each
 *     vertex's mantissa to recover the referenced entity hash and the
 *     per-vertex metadata.
 *
 * The PG wrapper handles GSERIALIZED ↔ POINT4D buffer marshalling via
 * lwgeom_from_gserialized + getPoint4d(); the engine sees the raw
 * `double*` buffer with n_points*4 doubles. */

/* Pack N entity-hash references into a mantissa-packed XYZM buffer.
 * Caller-side ordinal/run_length/flags threading lands with the real impl;
 * this stub signature is hash-only and will widen when implemented. */
int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm);

/* RLE variant: collapses consecutive identical hashes into a single vertex
 * with run_length > 1 in the M channel (per ADR 0012). out_xyzm must have
 * capacity for 4*n doubles (worst-case no compression). *out_vertex_count
 * receives the actual number of vertices emitted (≤ n). */
int trajectory_build_rle(const hash128_t* constituents,
                         size_t           n,
                         double*          out_xyzm,
                         size_t*          out_vertex_count);

/* Unpack a mantissa-packed XYZM buffer back to entity-hash references.
 * `trajectory_xyzm` is `n_points * 4` doubles. */
int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap);

#ifdef __cplusplus
}
#endif
