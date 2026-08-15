#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"

/* VERTEX POSITION IS THE ORDINAL. The packed `ordinal` field duplicates the
 * vertex's index in the LINESTRING, so it cannot disagree with it: measured
 * live 2026-08-15 over 291,412 physicalities / 3,519,140 constituents, zero
 * rows where the packed value differed from the position, and zero rows built
 * by the one producer that could make them differ (trajectory_build_rle, which
 * has no caller outside its own tests). Readers already take position --
 * trajectory_unpacked_points states it as the contract.
 *
 * The field is 16 bits, so it stops being able to carry the position at 65,535.
 * That limit belongs to the redundant copy, never to the composition: a bound
 * on how wide a thing may be must come from ownership or lifetime, not from a
 * spare field's width. Past the representable range the copy is written as 0 --
 * "not representable, use the position" -- rather than wrapped to a value that
 * would be wrong. At or below 65,535 every byte is identical to before, so
 * stored rows and their ids are untouched. */
#define TRAJECTORY_ORDINAL_MAX 0xFFFFu

int trajectory_build_flagged(const hash128_t* entity_hashes,
                             const uint64_t*  flags,
                             size_t           n,
                             double*          out_xyzm) {
    if (out_xyzm == NULL) return -1;
    if (entity_hashes == NULL && n > 0) return -1;

    for (size_t i = 0; i < n; ++i) {
        mantissa_payload_t p;
        p.entity_id  = entity_hashes[i];
        p.ordinal    = (i + 1 <= TRAJECTORY_ORDINAL_MAX) ? (uint16_t)(i + 1) : (uint16_t)0;
        p.run_length = 1;
        p.flags      = flags ? flags[i] : 0;
        mantissa_pack(&out_xyzm[i * 4], &p);
    }
    return 0;
}

int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm) {
    return trajectory_build_flagged(entity_hashes, NULL, n, out_xyzm);
}

int trajectory_build_rle(const hash128_t* constituents,
                         size_t           n,
                         double*          out_xyzm,
                         size_t*          out_vertex_count) {
    if (out_xyzm == NULL || out_vertex_count == NULL) return -1;
    if (constituents == NULL && n > 0) return -1;

    size_t v = 0;
    size_t i = 0;
    while (i < n) {
        size_t run = 1;
        while (i + run < n &&
               constituents[i + run].hi == constituents[i].hi &&
               constituents[i + run].lo == constituents[i].lo) {
            ++run;
        }
        /* Runs collapse, so position does NOT track the source ordinal here --
         * the prefix sum of the run_lengths this vertex and its predecessors
         * carry does, which is why the field is kept on this path. Both fields
         * are 16 bits: the ordinal above 65,535 is written 0 ("use the prefix
         * sum"), while a run longer than 65,535 is EMITTED AS SEVERAL VERTICES
         * rather than saturated -- run_length is what readback expands, so a
         * clamped value would silently reconstruct fewer constituents than went
         * in, and this path's whole contract is that it round-trips exactly. */
        size_t emitted = 0;
        while (emitted < run) {
            size_t chunk = run - emitted;
            if (chunk > TRAJECTORY_ORDINAL_MAX) chunk = TRAJECTORY_ORDINAL_MAX;
            size_t ord = i + emitted + 1;
            mantissa_payload_t p;
            p.entity_id  = constituents[i];
            p.ordinal    = (ord <= TRAJECTORY_ORDINAL_MAX) ? (uint16_t)ord : (uint16_t)0;
            p.run_length = (uint16_t)chunk;
            p.flags      = 0;
            mantissa_pack(&out_xyzm[v * 4], &p);
            ++v;
            emitted += chunk;
        }
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

    for (size_t i = 0; i < n_points; ++i) {
        mantissa_payload_t p;
        mantissa_unpack(&trajectory_xyzm[i * 4], &p);
        out_hashes[i] = p.entity_id;
    }
    return (int)n_points;
}
