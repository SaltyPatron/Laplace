#include "laplace/core/trajectory.h"

/* Real implementations land Chunk 2+ alongside mantissa_pack/unpack.
 * Stubs satisfy linkage. */

int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm) {
    (void)entity_hashes; (void)n; (void)out_xyzm;
    return -1;
}

int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap) {
    (void)trajectory_xyzm; (void)n_points; (void)out_hashes; (void)out_cap;
    return -1;
}
