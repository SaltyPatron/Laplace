#include "laplace/dynamics/init.h"

/* Process-startup init for liblaplace_dynamics. + 
 * (substrate determinism): lock MKL Conditional Bitwise Reproducibility so every
 * MKL computational routine (Eigen's MKL-dispatched dense ops via EIGEN_USE_MKL_ALL,
 * LAPACKE SVD, CBLAS GEMM) produces bit-identical results regardless of thread
 * count or dynamic dispatch. Must run BEFORE the first MKL call — it does, via the
 * C# binding's static constructor and the extension's _PG_init.
 *
 * The threading layer is already TBB by linkage (MKL::MKL is built with
 * MKL_THREADING=tbb_thread); we do NOT call mkl_set_threading_layer here — the
 * CMake compile-definition `MKL_THREADING_TBB` collides with the mkl.h enum of the
 * same name, and runtime selection is only needed for the single-dynamic (mkl_rt)
 * link model, which we don't use. CBWR is the determinism lever. */

#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
/* MKL_CBWR_AUTO: bit-reproducible on a given CPU across any thread count (the
 * determinism the parallel write path needs). Override via -DLAPLACE_MKL_CBWR_MODE=
 * MKL_CBWR_AVX2 (etc.) for cross-machine reproducibility on a pinned ISA. */
#ifndef LAPLACE_MKL_CBWR_MODE
#define LAPLACE_MKL_CBWR_MODE MKL_CBWR_AUTO
#endif
#endif

extern "C" int laplace_dynamics_init(void) {
#ifdef LAPLACE_HAS_MKL
    const int rc = mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE);
    /* MKL_CBWR_SUCCESS (0) on success. A failure means MKL already ran a kernel
     * before this lock (mode change refused) — surface it, never silently. */
    return (rc == MKL_CBWR_SUCCESS) ? 0 : -1;
#else
    /* Eigen-only fallback: Eigen's own dense/sparse paths are single-threaded
     * deterministic; nothing to lock. */
    return 0;
#endif
}

extern "C" const char* laplace_dynamics_version(void) {
    return "0.1.0";
}
