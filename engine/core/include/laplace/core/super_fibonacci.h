#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

void super_fibonacci(size_t n, double* out);

/*
 * The i-th point of the same n-point set, without materialising the other
 * n-1. super_fibonacci is closed form per index, so the loop body is liftable.
 * Bit-identical to super_fibonacci(n, .)[i] — pinned by test_super_fibonacci.
 */
void super_fibonacci_point(size_t n, size_t i, double out[4]);

/*
 * Placement with no declared population. super_fibonacci_point takes its radius
 * from (i+0.5)/n, so a coordinate is a property of (index, population): raising
 * n moves every point already placed, and an address is only stable while the
 * space never grows. The radius here is the base-2 radical inverse of i, which
 * is prefix-stable -- point i is the same point whether the space holds a
 * thousand entities or 2^53. The angular terms are byte-identical to the
 * bounded form; they never referenced n.
 *
 * Injective for the same reason the bounded form is: out[0]^2+out[1]^2 recovers
 * the radial parameter, which recovers i. Exact while i < 2^53.
 */
void super_fibonacci_point_open(size_t i, double out[4]);

#ifdef __cplusplus
}
#endif
