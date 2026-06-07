#include "laplace/synthesis/qk_pairs_threshold_pruned.h"

#include <algorithm>
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

inline double proj_l2(const double* v, size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, v[d] * v[d]);
    const double ss = sum + c;
    return ss > 0.0 ? std::sqrt(ss) : 0.0;
}

inline double score_qk_cached(const double* q_t, const double* k_s,
                              size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, q_t[d] * k_s[d]);
    return sum + c;
}

struct KeyNorm {
    double   norm;
    uint32_t key;
};

}

extern "C"
long compute_qk_pairs_above_threshold_pruned(
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

    std::vector<double> k_cache((size_t)vocab * head_dim);
    std::vector<KeyNorm> keys_desc(vocab);
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t s = rng.begin(); s != rng.end(); ++s) {
                double* k_proj = k_cache.data() + s * head_dim;
                project_token(E_f32 + s * d_model, Wk_head, d_model, head_dim, k_proj);
                keys_desc[s] = {proj_l2(k_proj, head_dim), (uint32_t)s};
            }
        });
#else
    for (size_t s = 0; s < vocab; ++s) {
        double* k_proj = k_cache.data() + s * head_dim;
        project_token(E_f32 + s * d_model, Wk_head, d_model, head_dim, k_proj);
        keys_desc[s] = {proj_l2(k_proj, head_dim), (uint32_t)s};
    }
#endif
    std::sort(keys_desc.begin(), keys_desc.end(),
              [](const KeyNorm& a, const KeyNorm& b) {
                  if (a.norm != b.norm) return a.norm > b.norm;
                  return a.key < b.key;
              });

    auto candidate_prefix_len = [&](double qnorm) -> size_t {
        if (qnorm <= 0.0) return 0;
        if (noise_floor <= 0.0) return vocab;
        const double cutoff = noise_floor / qnorm;
        size_t lo = 0, hi = vocab;
        while (lo < hi) {
            const size_t mid = lo + (hi - lo) / 2;
            if (keys_desc[mid].norm >= cutoff) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    };

    std::vector<size_t> counts(n_rows, 0);

    std::vector<double> q_cache((size_t)n_rows * head_dim);
    std::vector<double> q_norms(n_rows);
    auto project_query = [&](size_t ri) {
        double* q_t = q_cache.data() + ri * head_dim;
        project_token(E_f32 + (q0 + ri) * d_model, Wq_head, d_model, head_dim, q_t);
        q_norms[ri] = proj_l2(q_t, head_dim);
    };
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri) project_query(ri);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri) project_query(ri);
#endif

    auto count_row = [&](size_t ri) {
        const double* q_t = q_cache.data() + ri * head_dim;
        const size_t n_cand = candidate_prefix_len(q_norms[ri]);
        size_t n = 0;
        for (size_t i = 0; i < n_cand; ++i) {
            const uint32_t s = keys_desc[i].key;
            const double sc = score_qk_cached(q_t, k_cache.data() + (size_t)s * head_dim,
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
        const double* q_t = q_cache.data() + ri * head_dim;
        const size_t n_cand = candidate_prefix_len(q_norms[ri]);
        std::vector<qk_pair_f64_t> survivors;
        survivors.reserve(counts[ri]);
        for (size_t i = 0; i < n_cand; ++i) {
            const uint32_t s = keys_desc[i].key;
            const double sc = score_qk_cached(q_t, k_cache.data() + (size_t)s * head_dim,
                                              head_dim);
            if (std::fabs(sc) > noise_floor)
                survivors.push_back({(uint32_t)t, s, sc});
        }
        std::sort(survivors.begin(), survivors.end(),
                  [](const qk_pair_f64_t& a, const qk_pair_f64_t& b) {
                      return a.key_idx < b.key_idx;
                  });
        size_t w = offsets[ri];
        for (const auto& p : survivors) out[w++] = p;
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
