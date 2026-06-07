#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

int trajectory_build_flagged(const hash128_t* entity_hashes,
                             const uint64_t*  flags,
                             size_t           n,
                             double*          out_xyzm);

int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm);

int trajectory_build_rle(const hash128_t* constituents,
                         size_t           n,
                         double*          out_xyzm,
                         size_t*          out_vertex_count);

int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap);

#ifdef __cplusplus
}
#endif
