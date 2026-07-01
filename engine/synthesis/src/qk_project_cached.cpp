#include "laplace/synthesis/qk_project_cached.h"

#include <algorithm>
#include <cmath>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include "laplace/dynamics/tbb_parallel.h"
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
int project_qk_layer(
    const float* E_f32, size_t vocab, size_t d_model,
    const float* Wq, size_t n_heads,
    const float* Wk, size_t n_kv,
    size_t head_dim,
    double* q_cache_out, double* k_cache_out)
{
    if (!E_f32 || !Wq || !Wk || !q_cache_out || !k_cache_out) return -1;
    if (vocab == 0 || d_model == 0 || n_heads == 0 || n_kv == 0 || head_dim == 0)
        return -1;

    auto project_row = [&](size_t t) {
        const float* E_row = E_f32 + t * d_model;
        double* q_tok = q_cache_out + t * n_heads * head_dim;
        for (size_t h = 0; h < n_heads; ++h)
            project_token(E_row, Wq + h * head_dim * d_model, d_model, head_dim,
                          q_tok + h * head_dim);
        double* k_tok = k_cache_out + t * n_kv * head_dim;
        for (size_t kh = 0; kh < n_kv; ++kh)
            project_token(E_row, Wk + kh * head_dim * d_model, d_model, head_dim,
                          k_tok + kh * head_dim);
    };

#ifdef LAPLACE_HAS_MKL
laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t t = rng.begin(); t != rng.end(); ++t) project_row(t);
        });
#else
    for (size_t t = 0; t < vocab; ++t) project_row(t);
#endif
    return 0;
}

extern "C"
long score_qk_head_cached(
    const double* q_cache, size_t n_heads,
    const double* k_cache, size_t n_kv,
    size_t vocab, size_t head_dim,
    size_t head, size_t kv_head,
    double floor, size_t q0, size_t q1,
    qk_pair_f64_t* out, size_t out_cap, int* overflow)
{
    if (!q_cache || !k_cache || !out || !overflow) return -1;
    if (vocab == 0 || head_dim == 0 || n_heads == 0 || n_kv == 0) return -1;
    if (head >= n_heads || kv_head >= n_kv) return -1;
    if (q0 > q1 || q1 > vocab) return -1;
    if (std::isnan(floor) || floor < 0.0) return -1;
    const double noise_floor = floor;

    *overflow = 0;
    const size_t n_rows = q1 - q0;
    if (n_rows == 0) return 0;

    auto k_vec = [&](size_t s) -> const double* {
        return k_cache + (s * n_kv + kv_head) * head_dim;
    };
    auto q_vec = [&](size_t t) -> const double* {
        return q_cache + (t * n_heads + head) * head_dim;
    };

    std::vector<KeyNorm> keys_desc(vocab);
#ifdef LAPLACE_HAS_MKL
laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t s = rng.begin(); s != rng.end(); ++s)
                keys_desc[s] = {proj_l2(k_vec(s), head_dim), (uint32_t)s};
        });
#else
    for (size_t s = 0; s < vocab; ++s)
        keys_desc[s] = {proj_l2(k_vec(s), head_dim), (uint32_t)s};
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

    std::vector<double> q_norms(n_rows);
#ifdef LAPLACE_HAS_MKL
laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri)
                q_norms[ri] = proj_l2(q_vec(q0 + ri), head_dim);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri)
        q_norms[ri] = proj_l2(q_vec(q0 + ri), head_dim);
#endif

    std::vector<std::vector<qk_pair_f64_t>> row_out(n_rows);
    auto score_row = [&](size_t ri) {
        const double* q_t = q_vec(q0 + ri);
        const size_t n_cand = candidate_prefix_len(q_norms[ri]);
        auto& sv = row_out[ri];
        for (size_t i = 0; i < n_cand; ++i) {
            const uint32_t s = keys_desc[i].key;
            const double sc = score_qk_cached(q_t, k_vec(s), head_dim);
            if (std::fabs(sc) > noise_floor)
                sv.push_back({(uint32_t)(q0 + ri), s, sc});
        }
        std::sort(sv.begin(), sv.end(),
                  [](const qk_pair_f64_t& a, const qk_pair_f64_t& b) {
                      return a.key_idx < b.key_idx;
                  });
    };
#ifdef LAPLACE_HAS_MKL
laplace::tbb_ops::parallel_for_size(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri) score_row(ri);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri) score_row(ri);
#endif

    size_t acc = 0;
    size_t rows_fit = n_rows;
    for (size_t ri = 0; ri < n_rows; ++ri) {
        if (acc + row_out[ri].size() > out_cap) {
            rows_fit = ri;
            *overflow = 1;
            break;
        }
        acc += row_out[ri].size();
    }
    size_t w = 0;
    for (size_t ri = 0; ri < rows_fit; ++ri)
        for (const auto& p : row_out[ri]) out[w++] = p;

    return (long)w;
}
