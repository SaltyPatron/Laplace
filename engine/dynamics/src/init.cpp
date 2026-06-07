#include "laplace/dynamics/init.h"

#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#ifndef LAPLACE_MKL_CBWR_MODE
#define LAPLACE_MKL_CBWR_MODE MKL_CBWR_AUTO
#endif
#endif

extern "C" int laplace_dynamics_init(void) {
#ifdef LAPLACE_HAS_MKL
    const int rc = mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE);
    return (rc == MKL_CBWR_SUCCESS) ? 0 : -1;
#else
    return 0;
#endif
}

extern "C" const char* laplace_dynamics_version(void) {
    return "0.1.0";
}
