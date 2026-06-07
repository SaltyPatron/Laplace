#include "laplace/core/trajectory.h"
#include "laplace/core/mantissa.h"

int trajectory_build_flagged(const hash128_t* entity_hashes,
                             const uint64_t*  flags,
                             size_t           n,
                             double*          out_xyzm) {
    if (out_xyzm == NULL) return -1;
    if (entity_hashes == NULL && n > 0) return -1;
    if (n > 0xFFFFu) return -1;

    for (size_t i = 0; i < n; ++i) {
        mantissa_payload_t p;
        p.entity_id  = entity_hashes[i];
        p.ordinal    = (uint16_t)(i + 1);
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

    for (size_t i = 0; i < n_points; ++i) {
        mantissa_payload_t p;
        mantissa_unpack(&trajectory_xyzm[i * 4], &p);
        out_hashes[i] = p.entity_id;
    }
    return (int)n_points;
}
