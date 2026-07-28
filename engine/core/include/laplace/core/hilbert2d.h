#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Hilbert 2D <-> 1D, the image ladder's POSITION primitive.
 *
 * docs/invention/modality-ladder-law.md, image section:
 *   "Position = trajectory (Hilbert 2D->1D already positional-encoding primitive)"
 *
 * NOT a replacement for hilbert4d, and not the same job. hilbert4d gives
 * locality preservation over the 4-BALL. Only TIER-0 atoms are projected onto
 * the glome — the S3 surface — placed there by their canonical order
 * (super_fibonacci, unit norm). Everything composed takes the CENTROID of its
 * constituents (hash_composer.c -> math4d_centroid) and therefore falls strictly
 * INSIDE, toward the origin: the more composition, the deeper in. The surface is
 * the alphabet boundary; the interior is everything built out of it.
 *
 * This module answers an orthogonal question: in what ORDER do a frame's pixels
 * enter its trajectory.
 *
 * That order is NOT merely a compression choice — it is part of the image's
 * IDENTITY. Above tier 0 the coordinate cannot carry identity, because centroids
 * commute (Rule #1, content_witness_batch.c: "centroids collide, e.g. cat/act");
 * identity is the ordered constituent sequence, i.e. the trajectory. So a frame
 * scanned row-major and the same frame scanned along this curve are DIFFERENT
 * entities with different ids. Whichever order ships is therefore rock-lock
 * class, exactly like the packed-RGB colour order, and belongs in
 * modality-ladder-law.md with the same "operator may override before the first
 * image seed only" clause. Nothing here decides it; this supplies the curve.
 *
 * WHY HILBERT AND NOT ROW-MAJOR. The image ladder stores a frame as a
 * physicality trajectory, and the trajectory format carries run_length as a
 * first-class field (laplace_mantissa_pack, laplace_trajectory_constituents).
 * A run only compresses if spatially adjacent pixels are adjacent in the
 * sequence. Row-major breaks every run at the right edge — a flat 64x64 block
 * costs 64 runs. The Hilbert curve keeps 2D locality in 1D: the same block is
 * one contiguous stretch, so it costs one. Same reason the law reaches for it
 * rather than a scanline, and the reason it compounds with content-address
 * dedup rather than fighting it.
 *
 * Coordinates are unsigned and confined to an order-N square (side 2^order),
 * so the index fits 2*order bits. order <= 31 keeps the index inside uint64.
 */

/* Side length of an order-N square. */
static inline uint64_t hilbert2d_side(uint32_t order) { return 1ull << order; }

/* Number of cells on the curve for an order-N square. */
static inline uint64_t hilbert2d_cells(uint32_t order) { return 1ull << (2u * order); }

/*
 * (x, y) -> distance along the order-N Hilbert curve.
 * Returns 0 on success, -1 if order is out of range or a coordinate is outside
 * the square. Deterministic and total on its domain — no state, no allocation.
 */
int hilbert2d_encode(uint32_t order, uint32_t x, uint32_t y, uint64_t* out_d);

/* Exact inverse of hilbert2d_encode. */
int hilbert2d_decode(uint32_t order, uint64_t d, uint32_t* out_x, uint32_t* out_y);

#ifdef __cplusplus
}
#endif
