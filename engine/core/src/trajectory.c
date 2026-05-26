#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"

/* A trajectory is a mantissa-packed 4D LINESTRING: one vertex per constituent,
 * each vertex carrying the constituent entity's full 128-bit id + ordinal via
 * mantissa_pack (ADR 0012). build/constituents are the per-tier-node loop over
 * that lossless per-vertex primitive — the substrate's content-storage codec.
 *
 * Ordinal is uint16, so a single trajectory holds at most 65535 direct
 * constituents. That is by design: trajectories store ONE tier's direct
 * children (a word's graphemes, a sentence's words, a document's sentences),
 * never a flattened leaf list — fan-out per node stays small. A caller that
 * needs more must tier deeper, not widen a single trajectory. */

int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm) {
    if (out_xyzm == NULL) return -1;
    if (entity_hashes == NULL && n > 0) return -1;
    if (n > 0xFFFFu) return -1;   /* ordinal is uint16; tier deeper instead */

    for (size_t i = 0; i < n; ++i) {
        mantissa_payload_t p;
        p.entity_id  = entity_hashes[i];
        p.ordinal    = (uint16_t)(i + 1);   /* 1-indexed per ADR 0012 */
        p.run_length = 1;
        p.flags      = 0;
        mantissa_pack(&out_xyzm[i * 4], &p);
    }
    return 0;
}

int trajectory_build_rle(const hash128_t* constituents,
                         size_t           n,
                         double*          out_xyzm,
                         size_t*          out_vertex_count) {
    if (out_xyzm == NULL || out_vertex_count == NULL) return -1;
    if (constituents == NULL && n > 0) return -1;
    if (n > 0xFFFFu) return -1;

    size_t v = 0;
    size_t i = 0;
    while (i < n) {
        size_t run = 1;
        while (i + run < n &&
               constituents[i + run].hi == constituents[i].hi &&
               constituents[i + run].lo == constituents[i].lo) {
            ++run;
        }
        mantissa_payload_t p;
        p.entity_id  = constituents[i];
        p.ordinal    = (uint16_t)(i + 1);
        p.run_length = (uint16_t)run;
        p.flags      = 0;
        mantissa_pack(&out_xyzm[v * 4], &p);
        ++v;
        i += run;
    }
    *out_vertex_count = v;
    return 0;
}

int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap) {
    if (out_hashes == NULL) return -1;
    if (trajectory_xyzm == NULL && n_points > 0) return -1;
    if (n_points > out_cap) return -1;

    /* Recover constituents in vertex order — the order IS the sequence
     * (ordinal is a redundant cross-check stored per vertex). */
    for (size_t i = 0; i < n_points; ++i) {
        mantissa_payload_t p;
        mantissa_unpack(&trajectory_xyzm[i * 4], &p);
        out_hashes[i] = p.entity_id;
    }
    return (int)n_points;
}
