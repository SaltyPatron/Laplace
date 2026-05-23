#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Marc Alexa, "Super-Fibonacci Spirals: Fast, Low-Discrepancy Sampling of
 * SO(3)", IEEE/CVF CVPR 2022, doi:10.1109/CVPR52688.2022.00795.
 *
 * Generates N quasi-uniform unit quaternions on S^3 with low discrepancy.
 * Deterministic single-pass formula; distribution near-optimal.
 *
 * Algorithm (paper Equation 7):
 *   φ = √2,  ψ = 1.533751168755204288118041...
 *   for i in [0, n):
 *       s = i + 0.5
 *       r = √(s/n),  R = √(1 - s/n)
 *       α = 2π · s / φ
 *       β = 2π · s / ψ
 *       Q[i] = (r·sin α,  r·cos α,  R·sin β,  R·cos β)
 *
 * Each Q[i] is a unit quaternion by construction: r² + R² = 1.
 *
 * Output layout: `out` is `4*n` doubles, with `out[4*i + 0..3]` = the i-th
 * quaternion as (x, y, z, w) where (x, y, z) are the imaginary components
 * and w is the scalar component. The 4-tuple is also a point on S^3 in R^4.
 *
 * Substrate use (per ADR 0006): produces the substrate-canonical CONTENT
 * physicality coord for T0 Unicode codepoint entities, with `n =
 * 1,114,112` (the Unicode codepoint space) and `i` = the codepoint's UCA
 * collation order. */

void super_fibonacci(size_t n, double* out);

#ifdef __cplusplus
}
#endif
