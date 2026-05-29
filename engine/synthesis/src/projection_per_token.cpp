#include "laplace/synthesis/projection_per_token.h"

#include <cmath>
#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* Per-token E·|W| projection — one weight tensor's per-token contribution.
 *
 * Exact + deterministic engine kernel replacing the scalar C#
 * WeightTensorETL.AggregateLayerThroughEmbed. The only rounding step is the
 * summation, which uses Neumaier compensated summation in a FIXED order:
 *
 *   Step 1: perInDim[i] = Σ_o |f64(W[o,i])|  (fixed order over o)
 *   Step 2: per token t, either project E @ perInDim (in_dim == d_model)
 *           in fixed order over i, or fall back to a uniform distribution of
 *           the total perInDim magnitude across the vocabulary.
 *
 * Each perInDim[i] is a self-contained reduction over o; each out[t] is a
 * self-contained reduction over i. Parallelizing ACROSS those independent
 * axes (TBB: i for step 1, t for step 2) leaves every reduction's arithmetic —
 * and therefore the output bits — identical regardless of thread count. */

namespace {

/* Exact BF16 → f32: upper 16 bits of an IEEE-754 float32. Matches the C#
 * decode `(uint)bits << 16` reinterpreted as float. */
inline float bf16_to_f32(uint16_t bits) {
    const uint32_t u = (uint32_t)bits << 16;
    float f;
    std::memcpy(&f, &u, sizeof(f));
    return f;
}

/* One Neumaier compensated-summation step: fold `term` into (sum, c). */
inline void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    /* Capture the rounding error from whichever operand is larger. */
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

/* Step 1: perInDim[i] = Σ_{o=0..out_dim-1} |f64(W[o,i])|, compensated, fixed
 * order over o. Independent across i. */
inline double per_in_dim(const uint16_t* W_bf16, size_t i,
                         size_t out_dim, size_t in_dim) {
    double sum = 0.0;   /* running sum */
    double c   = 0.0;   /* compensation for lost low-order bits */
    for (size_t o = 0; o < out_dim; ++o) {
        const double v = (double)bf16_to_f32(W_bf16[o * in_dim + i]);
        neumaier_add(sum, c, std::fabs(v));
    }
    return sum + c;
}

/* Step 2 (projection case, in_dim == d_model): out[t] = |Σ_i E[t,i]*perInDim[i]|,
 * compensated, fixed order over i. Independent across t. */
inline double project_token(const uint16_t* E_row, const double* perInDim,
                            size_t in_dim) {
    double sum = 0.0;
    double c   = 0.0;
    for (size_t i = 0; i < in_dim; ++i) {
        const double e = (double)bf16_to_f32(E_row[i]);
        neumaier_add(sum, c, e * perInDim[i]);
    }
    return std::fabs(sum + c);
}

} /* namespace */

extern "C"
int compute_projection_per_token(const uint16_t* E_bf16, size_t vocab, size_t d_model,
                                 const uint16_t* W_bf16, size_t out_dim, size_t in_dim,
                                 double* out /*[vocab]*/) {
    if (!E_bf16 || !W_bf16 || !out) return -1;
    if (vocab == 0 || d_model == 0 || out_dim == 0 || in_dim == 0) return -1;

    /* ── Step 1: perInDim[i] over the output axis. Shared, read-only by step 2.
     * Each i is an independent fixed-order reduction over o, so we may
     * parallelize across i without affecting any output bit. */
    std::vector<double> perInDim(in_dim);
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, in_dim, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t i = rng.begin(); i != rng.end(); ++i)
                perInDim[i] = per_in_dim(W_bf16, i, out_dim, in_dim);
        });
#else
    for (size_t i = 0; i < in_dim; ++i)
        perInDim[i] = per_in_dim(W_bf16, i, out_dim, in_dim);
#endif

    /* ── Step 2. */
    if (in_dim == d_model) {
        /* Project E through perInDim. Each token t is an independent
         * fixed-order reduction over i — parallelize across tokens only. */
#ifdef LAPLACE_HAS_MKL
        oneapi::tbb::parallel_for(
            oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
            [&](const oneapi::tbb::blocked_range<size_t>& rng) {
                for (size_t t = rng.begin(); t != rng.end(); ++t)
                    out[t] = project_token(E_bf16 + t * d_model, perInDim.data(),
                                           in_dim);
            });
#else
        for (size_t t = 0; t < vocab; ++t)
            out[t] = project_token(E_bf16 + t * d_model, perInDim.data(), in_dim);
#endif
    } else {
        /* Fallback (in_dim != d_model, e.g. down_proj where in_dim is the
         * intermediate dim): the documented uniform distribution. Sum the
         * total perInDim magnitude once (compensated, fixed order over i),
         * divide once, broadcast to every token. */
        double sum = 0.0;
        double c   = 0.0;
        for (size_t i = 0; i < in_dim; ++i)
            neumaier_add(sum, c, perInDim[i]);
        const double per_token = (sum + c) / (double)vocab;
        for (size_t t = 0; t < vocab; ++t)
            out[t] = per_token;
    }

    return 0;
}
