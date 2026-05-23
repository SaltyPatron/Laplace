#include "laplace/core/super_fibonacci.h"

#include <math.h>

/* See header for the canonical reference (Alexa CVPR 2022).
 *
 * The constants φ and ψ below are the exact magic values from the paper's
 * supplementary material. ψ is a numerical solution to the optimization
 * problem of minimizing pair-wise discrepancy on S^3; the value is stable
 * across reproductions of the paper's experiments. φ = √2 is exact. */

#define LAPLACE_SUPER_FIB_PHI 1.4142135623730951454746218587388284504413604736328125  /* √2 */
#define LAPLACE_SUPER_FIB_PSI 1.5337511687552042888118041448362171649932861328125     /* Alexa 2022 */
#define LAPLACE_SUPER_FIB_TWO_PI 6.2831853071795864769252867665590057683943387987502

void super_fibonacci(size_t n, double* out) {
    if (n == 0 || out == NULL) return;
    const double inv_phi = LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PHI;
    const double inv_psi = LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PSI;
    const double inv_n = 1.0 / (double)n;
    for (size_t i = 0; i < n; ++i) {
        const double s = (double)i + 0.5;
        const double s_over_n = s * inv_n;
        const double r = sqrt(s_over_n);
        const double R = sqrt(1.0 - s_over_n);
        const double alpha = s * inv_phi;
        const double beta  = s * inv_psi;
        const size_t base = i << 2;  /* 4*i */
        out[base + 0] = r * sin(alpha);
        out[base + 1] = r * cos(alpha);
        out[base + 2] = R * sin(beta);
        out[base + 3] = R * cos(beta);
    }
}
