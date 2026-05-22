#include "laplace/dynamics/init.h"

/* Real implementation lands Epic D Story D.4 — mkl_set_threading_layer(TBB)
 * + mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE). Stub satisfies linkage. */

extern "C" int laplace_dynamics_init(void) {
#ifdef LAPLACE_HAS_MKL
    /* TODO Epic D Story D.4 — real init:
     * mkl_set_threading_layer(MKL_THREADING_TBB);
     * return mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE) == MKL_CBWR_SUCCESS ? 0 : -1;
     */
    return 0;
#else
    /* Eigen-only fallback: nothing to init. */
    return 0;
#endif
}

extern "C" const char* laplace_dynamics_version(void) {
    return "0.1.0";
}
