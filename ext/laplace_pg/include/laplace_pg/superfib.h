/*
 * superfib.h — SuperFibonacciService public API.
 *
 * Phase 2 / Track B / Service B4.
 *
 * Marc Alexa, "Super-Fibonacci Spirals: Fast, Low-Discrepancy Sampling of
 * SO(3)", CVPR 2022. Quasi-uniform low-discrepancy sampling of S³ (the unit
 * 3-sphere parameterized as unit quaternions in 4D). Used to place every
 * Unicode codepoint atom in the substrate's tier-0 pool — all 1,114,112
 * codepoints across the full 17 planes, ordered by (script, general_category,
 * UCA primary collation weight, Unihan radical for CJK, codepoint integer).
 *
 * Constants (Alexa CVPR 2022 §3): the positive real roots of
 *   ψ⁴ = ψ + 4    →  PSI ≈ 1.5343237490380328129
 *   φ²  = ?       →  PHI = golden ratio, ≈ 1.6180339887498948482
 *   (these names match the Hartonomous-002 SuperFibonacci.cs port)
 */

#ifndef LAPLACE_SUPERFIB_H
#define LAPLACE_SUPERFIB_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Place sample i of total on S³.
 * out_xyzw is a 4-double output array filled with the unit quaternion.
 */
void laplace_super_fibonacci_4d(int i, int total, double out_xyzw[4]);

/*
 * Batch placement: write samples [start_inclusive, end_exclusive) of total
 * into out_array (out_array must have at least 4 * (end - start) doubles).
 */
void laplace_super_fibonacci_4d_range(int start_inclusive,
                                      int end_exclusive,
                                      int total,
                                      double *out_array);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_SUPERFIB_H */
