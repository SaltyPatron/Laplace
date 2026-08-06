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

#ifdef __cplusplus
}
#endif
