#include "laplace/synthesis/qk_pairs_threshold.h"

#include <cmath>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

namespace {

inline void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

inline void project_token(const float* E_row, const float* W, size_t d_model,
                          size_t head_dim, double* proj) {
    for (size_t d = 0; d < head_dim; ++d) {
        const float* w = W + d * d_model;
        double sum = 0.0, c = 0.0;
        for (size_t m = 0; m < d_model; ++m)
            neumaier_add(sum, c, (double)E_row[m] * (double)w[m]);
        proj[d] = sum + c;
    }
}

inline double score_cached(const double* q_t, const double* k_s, size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, q_t[d] * k_s[d]);
    return sum + c;
}

}

extern "C"
long compute_qk_pairs_above_threshold(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq_head, const float* Wk_head, size_t head_dim,
    double noise_floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow)
{
    if (!E_f32 || !Wq_head || !Wk_head || !out || !overflow) return -1;
    if (vocab == 0 || d_model == 0 || head_dim == 0) return -1;
    if (q0 > q1 || q1 > vocab) return -1;
    if (std::isnan(noise_floor) || noise_floor < 0.0) return -1;

    *overflow = 0;
    const size_t n_rows = q1 - q0;
    if (n_rows == 0) return 0;

    std::vector<double> K_cache(vocab * head_dim);
    {
        auto project_key = [&](size_t s) {
            project_token(E_f32 + s * d_model, Wk_head, d_model, head_dim,
                          K_cache.data() + s * head_dim);
        };
#ifdef LAPLACE_HAS_MKL
        oneapi::tbb::parallel_for(
            oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
            [&](const oneapi::tbb::blocked_range<size_t>& rng) {
                for (size_t s = rng.begin(); s != rng.end(); ++s) project_key(s);
            });
#else
        for (size_t s = 0; s < vocab; ++s) project_key(s);
#endif
    }

    std::vector<size_t> counts(n_rows, 0);

    auto count_row = [&](size_t ri) {
        const size_t t = q0 + ri;
        std::vector<double> q_t(head_dim);
        project_token(E_f32 + t * d_model, Wq_head, d_model, head_dim, q_t.data());
        size_t n = 0;
        for (size_t s = 0; s < vocab; ++s) {
            const double sc = score_cached(q_t.data(), K_cache.data() + s * head_dim,
                                           head_dim);
            if (std::fabs(sc) > noise_floor) ++n;
        }
        counts[ri] = n;
    };

#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri) count_row(ri);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri) count_row(ri);
#endif

    std::vector<size_t> offsets(n_rows);
    size_t acc = 0;
    size_t rows_fit = n_rows;
    for (size_t ri = 0; ri < n_rows; ++ri) {
        if (acc + counts[ri] > out_cap) {
            rows_fit = ri;
            *overflow = 1;
            break;
        }
        offsets[ri] = acc;
        acc += counts[ri];
    }
    const size_t total_written = acc;

    auto write_row = [&](size_t ri) {
        if (counts[ri] == 0) return;
        const size_t t = q0 + ri;
        std::vector<double> q_t(head_dim);
        project_token(E_f32 + t * d_model, Wq_head, d_model, head_dim, q_t.data());
        size_t w = offsets[ri];
        for (size_t s = 0; s < vocab; ++s) {
            const double sc = score_cached(q_t.data(), K_cache.data() + s * head_dim,
                                           head_dim);
            if (std::fabs(sc) > noise_floor) {
                out[w].query_idx = (uint32_t)t;
                out[w].key_idx   = (uint32_t)s;
                out[w].score     = sc;
                ++w;
            }
        }
    };

#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, rows_fit, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri) write_row(ri);
        });
#else
    for (size_t ri = 0; ri < rows_fit; ++ri) write_row(ri);
#endif

    return (long)total_written;
}
