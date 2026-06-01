#include "laplace/synthesis/qk_project_cached.h"

#include <algorithm>
#include <cmath>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* Project-once + score-from-cache decomposition of the pruned QK kernel.
 *
 * Both the projection helpers and the scoring algorithm below are copied VERBATIM
 * from qk_pairs_threshold_pruned.cpp (neumaier_add / project_token / proj_l2 /
 * score_qk_cached / KeyNorm / candidate_prefix_len / two-pass count+write). The
 * ONLY change in scoring is the projection SOURCE: instead of projecting E per
 * head, we read the pre-projected q_cache[token][head] and k_cache[token][kv_head]
 * vectors that project_qk_layer wrote in a single pass over E. The arithmetic and
 * the emission contract are therefore bit-identical to the pruned kernel. */

namespace {

/* One Neumaier compensated-summation step — IDENTICAL to qk_pairs_threshold.cpp /
 * qk_pairs_threshold_pruned.cpp so projected/score bits match exactly. */
inline void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

/* Project token `row` through head weight W [head_dim x d_model] into
 * proj[d] = Σ_m E[m]*W[d,m], compensated, fixed order over m. IDENTICAL to the
 * pruned kernel's project_token. */
inline void project_token(const float* E_row, const float* W, size_t d_model,
                          size_t head_dim, double* proj /*[head_dim]*/) {
    for (size_t d = 0; d < head_dim; ++d) {
        const float* w = W + d * d_model;
        /* Serial Neumaier compensated sum over m, fixed order — BIT-IDENTICAL to
         * qk_pairs_threshold.cpp's project_token (the all-pairs reference) and to the
         * C# Neumaier reference. The substrate is exact and deterministic (no ANN, no
         * approximation): the project-once cache and the all-pairs kernel MUST agree
         * bit-for-bit, so both use the same compensated chain. (A prior 8-lane plain
         * reduction here vectorized faster but dropped compensation, diverging by 1 ULP
         * — that broke determinism parity and was less exact; reverted.) Determinism is
         * structural: each (token,head,dim) record is computed wholly by one thread in
         * this fixed order, so the cache is identical run-to-run regardless of threads. */
        double sum = 0.0, c = 0.0;
        for (size_t m = 0; m < d_model; ++m)
            neumaier_add(sum, c, (double)E_row[m] * (double)w[m]);
        proj[d] = sum + c;
    }
}

/* L2 norm of a projected vector, compensated f64 over the squares (fixed order).
 * IDENTICAL to the pruned kernel. Pruning only; never the emitted score. */
inline double proj_l2(const double* v, size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, v[d] * v[d]);
    const double ss = sum + c;
    return ss > 0.0 ? std::sqrt(ss) : 0.0;
}

/* score(t,s) = Σ_d q_t[d]*k_s[d], compensated, fixed order over d, over the
 * PRE-PROJECTED vectors. IDENTICAL to the pruned kernel's score_qk_cached. */
inline double score_qk_cached(const double* q_t, const double* k_s,
                              size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, q_t[d] * k_s[d]);
    return sum + c;
}

/* (norm, key_index) for the norm-descending key table. IDENTICAL to the sibling. */
struct KeyNorm {
    double   norm;
    uint32_t key;
};

} /* namespace */

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

    /* ETL transform: each (token, head, dim) projection is an independent output
     * record. Parallelize across tokens; each record is computed wholly by one
     * thread via project_token's fixed-order reduction. Determinism is structural —
     * thread count distributes records, never splits a record's math — so the cache
     * is bit-identical run-to-run regardless of thread count, with no library
     * reproducibility flag. */
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
    oneapi::tbb::parallel_for(
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

    /* k_s for key s is k_cache[(s*n_kv + kv_head)*head_dim ..]. */
    auto k_vec = [&](size_t s) -> const double* {
        return k_cache + (s * n_kv + kv_head) * head_dim;
    };
    /* q_t for query token t is q_cache[(t*n_heads + head)*head_dim ..]. */
    auto q_vec = [&](size_t t) -> const double* {
        return q_cache + (t * n_heads + head) * head_dim;
    };

    /* ── Key norm table for this kv_head (computed ONCE, fixed key order). The
     * projected keys already live in k_cache; we just read their cached vectors and
     * compute the pruning norm (same proj_l2 as the pruned kernel). ───────────── */
    std::vector<KeyNorm> keys_desc(vocab);
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, vocab, 256),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t s = rng.begin(); s != rng.end(); ++s)
                keys_desc[s] = {proj_l2(k_vec(s), head_dim), (uint32_t)s};
        });
#else
    for (size_t s = 0; s < vocab; ++s)
        keys_desc[s] = {proj_l2(k_vec(s), head_dim), (uint32_t)s};
#endif
    /* Descending by norm; ties broken by key index so the prefix boundary is
     * fully deterministic. (Order within a query is fixed later regardless.) */
    std::sort(keys_desc.begin(), keys_desc.end(),
              [](const KeyNorm& a, const KeyNorm& b) {
                  if (a.norm != b.norm) return a.norm > b.norm;
                  return a.key < b.key;
              });

    /* Per-query candidate-key cutoff — IDENTICAL to the pruned kernel. */
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

    /* Per-query L2 norms (read from the cached q vectors; same proj_l2). */
    std::vector<double> q_norms(n_rows);
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri)
                q_norms[ri] = proj_l2(q_vec(q0 + ri), head_dim);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri)
        q_norms[ri] = proj_l2(q_vec(q0 + ri), head_dim);
#endif

    /* ── Single pass: each row scores its candidate prefix ONCE into a
     * thread-local survivor vector. The prior kernel scored every candidate
     * TWICE — once in a count pass, once in a write pass — purely to compute
     * stable parallel output offsets; that doubled the dominant compensated-dot
     * cost (the inner score_qk_cached over head_dim). Output here is
     * BIT-IDENTICAL: same survivor set (same |sc|>floor test on the same cached
     * dot), same per-row ascending-key order, same whole-leading-rows out_cap
     * overflow policy. Only the redundant second scoring is removed. ───────── */
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
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<size_t>(0, n_rows, 64),
        [&](const oneapi::tbb::blocked_range<size_t>& rng) {
            for (size_t ri = rng.begin(); ri != rng.end(); ++ri) score_row(ri);
        });
#else
    for (size_t ri = 0; ri < n_rows; ++ri) score_row(ri);
#endif

    /* Concatenate whole leading rows that fit out_cap, in row order — same
     * deterministic layout + overflow semantics as the prior two-pass write. */
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
