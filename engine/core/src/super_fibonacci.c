#include "laplace/core/super_fibonacci.h"

#include <math.h>
#include <stdint.h>

#define LAPLACE_SUPER_FIB_PHI 1.4142135623730951454746218587388284504413604736328125
#define LAPLACE_SUPER_FIB_PSI 1.5337511687552042888118041448362171649932861328125
#define LAPLACE_SUPER_FIB_TWO_PI 6.2831853071795864769252867665590057683943387987502

void super_fibonacci_point(size_t n, size_t i, double out[4]) {
    if (n == 0 || out == NULL || i >= n) return;
    const double s = (double)i + 0.5;
    const double s_over_n = s * (1.0 / (double)n);
    const double r = sqrt(s_over_n);
    const double R = sqrt(1.0 - s_over_n);
    const double alpha = s * (LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PHI);
    const double beta  = s * (LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PSI);
    out[0] = r * sin(alpha);
    out[1] = r * cos(alpha);
    out[2] = R * sin(beta);
    out[3] = R * cos(beta);
}

/* Base-2 radical inverse: bit-reverse i into the fraction. Injective on the low
 * 53 bits, which is what makes the placement below injective. Integer-only --
 * no transcendental, so this half carries no libm dependence. */
static double laplace_radical_inverse_base2(uint64_t i) {
    i = (i << 32) | (i >> 32);
    i = ((i & 0x0000ffff0000ffffULL) << 16) | ((i & 0xffff0000ffff0000ULL) >> 16);
    i = ((i & 0x00ff00ff00ff00ffULL) << 8)  | ((i & 0xff00ff00ff00ff00ULL) >> 8);
    i = ((i & 0x0f0f0f0f0f0f0f0fULL) << 4)  | ((i & 0xf0f0f0f0f0f0f0f0ULL) >> 4);
    i = ((i & 0x3333333333333333ULL) << 2)  | ((i & 0xccccccccccccccccULL) >> 2);
    i = ((i & 0x5555555555555555ULL) << 1)  | ((i & 0xaaaaaaaaaaaaaaaaULL) >> 1);
    return (double)(i >> 11) * 0x1.0p-53;
}

void super_fibonacci_point_open(size_t i, double out[4]) {
    if (out == NULL) return;
    const double s = (double)i + 0.5;
    const double t = laplace_radical_inverse_base2((uint64_t)i);
    const double r = sqrt(t);
    const double R = sqrt(1.0 - t);
    const double alpha = s * (LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PHI);
    const double beta  = s * (LAPLACE_SUPER_FIB_TWO_PI / LAPLACE_SUPER_FIB_PSI);
    out[0] = r * sin(alpha);
    out[1] = r * cos(alpha);
    out[2] = R * sin(beta);
    out[3] = R * cos(beta);
}


static uint64_t laplace_reverse_low_bits(uint64_t value, unsigned bits) {
    uint64_t out = 0;
    for (unsigned i = 0; i < bits; ++i) {
        out = (out << 1) | (value & 1ULL);
        value >>= 1;
    }
    return out;
}

int super_fibonacci_open_index(const double point[4], uint64_t max_count,
                               uint64_t* out_index) {
    if (point == NULL || out_index == NULL || max_count == 0
        || max_count > (1ULL << 53))
        return -1;

    unsigned bits = 0;
    uint64_t ceiling = max_count - 1;
    while (ceiling != 0) {
        ++bits;
        ceiling >>= 1;
    }
    if (bits == 0) bits = 1;

    const double t = point[0] * point[0] + point[1] * point[1];
    if (!isfinite(t) || t < -1e-12 || t > 1.0 + 1e-12)
        return -2;

    const double scaled = ldexp(t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t), (int)bits);
    uint64_t reversed = (uint64_t) llround(scaled);
    const uint64_t domain = 1ULL << bits;
    if (reversed >= domain)
        reversed = domain - 1;

    const uint64_t index = laplace_reverse_low_bits(reversed, bits);
    if (index >= max_count)
        return -3;

    *out_index = index;
    return 0;
}

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
        const size_t base = i << 2;
        out[base + 0] = r * sin(alpha);
        out[base + 1] = r * cos(alpha);
        out[base + 2] = R * sin(beta);
        out[base + 3] = R * cos(beta);
    }
}
