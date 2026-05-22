#include "laplace/dynamics/sparsity.h"

#include <cstddef>

/* Real implementations land Chunk 6 Stories 6.10/6.11/6.12 — lottery-
 * ticket-aware multi-pass filter per RULES.md R3 + ADR 0007. Stubs
 * satisfy linkage. */

extern "C"
int sparsity_per_tensor_topk(const double*            weights,
                             size_t                   n,
                             const sparsity_params_t* params,
                             uint8_t*                 out_mask) {
    (void)weights; (void)n; (void)params; (void)out_mask;
    return -1;
}

extern "C"
int sparsity_per_row_topk(const double*            weights,
                          size_t                   rows,
                          size_t                   cols,
                          const sparsity_params_t* params,
                          uint8_t*                 inout_mask) {
    (void)weights; (void)rows; (void)cols; (void)params; (void)inout_mask;
    return -1;
}

extern "C"
int sparsity_probe_validate(const double*            weights,
                            size_t                   n,
                            const sparsity_params_t* params,
                            uint8_t*                 inout_mask) {
    (void)weights; (void)n; (void)params; (void)inout_mask;
    return -1;
}
