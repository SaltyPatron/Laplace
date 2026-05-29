#include "laplace/synthesis/per_token_magnitude.h"

#include <cmath>
#include <cstring>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* Per-token L2 magnitude of a BF16 [rows x cols] row-major tensor.
 *
 * Exact + deterministic engine kernel replacing the scalar C#
 * WeightTensorETL.ReducePerCellMagnitude. The only rounding step is the inner
 * summation, which uses Neumaier compensated summation in a FIXED column order.
 * Each row's reduction is wholly self-contained, so parallelizing ACROSS rows
 * (TBB) leaves every row's arithmetic — and therefore the output bits —
 * identical regardless of thread count. */

namespace {

/* Exact BF16 → f32: upper 16 bits of an IEEE-754 float32. Matches the C#
 * decode `(uint)bits << 16` reinterpreted as float. */
inline float bf16_to_f32(uint16_t bits) {
    const uint32_t u = (uint32_t)bits << 16;
    float f;
    std::memcpy(&f, &u, sizeof(f));
    return f;
}

/* L2 norm of one row, Neumaier-compensated f64 sum in fixed column order. */
inline double row_l2(const uint16_t* row, size_t cols) {
    double sum = 0.0;   /* running sum */
    double c   = 0.0;   /* compensation for lost low-order bits */
    for (size_t j = 0; j < cols; ++j) {
        const double v = (double)bf16_to_f32(row[j]);
        const double term = v * v;                 /* square in f64 */
        const double t = sum + term;
        /* Neumaier: capture the rounding error whichever operand is larger. */
        if (std::fabs(sum) >= std::fabs(term))
            c += (sum - t) + term;
        else
            c += (term - t) + sum;
        sum = t;
    }
    return std::sqrt(sum + c);
}

} /* namespace */

extern "C"
int compute_per_token_l2_magnitude(const uint16_t* tensor_bf16,
                                   size_t rows, size_t cols,
                                   double* out /*[rows]*/) {
    if (!tensor_bf16 || !out) return -1;
    if (rows == 0 || cols == 0) return -1;

#ifdef LAPLACE_HAS_MKL
    /* Parallelize across rows only — each row's fixed-order reduction is
     * independent, so thread count cannot change any output bit. */
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, rows, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t r = rng.begin(); r != rng.end(); ++r)
                out[r] = row_l2(tensor_bf16 + r * cols, cols);
        });
#else
    for (size_t r = 0; r < rows; ++r)
        out[r] = row_l2(tensor_bf16 + r * cols, cols);
#endif

    return 0;
}
