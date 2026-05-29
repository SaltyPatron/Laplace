#include "laplace/synthesis/qk_pairs_threshold_pruned.h"

#include <algorithm>
#include <cmath>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* Exact, deterministic, SUB-QUADRATIC threshold-based QK token-relation scorer.
 *
 * Bit-identical to compute_qk_pairs_above_threshold (qk_pairs_threshold.cpp) but
 * avoids scoring the vast majority of query×key pairs by Cauchy–Schwarz norm
 * pruning: |q_t·k_s| <= ‖q_t‖·‖k_s‖, so a pair can satisfy |score| > noise_floor
 * only if ‖q_t‖·‖k_s‖ > noise_floor. We compute all key norms once, sort keys by
 * norm descending, and per query score only the keys whose norm clears the
 * per-query cutoff (a prefix of the sorted list found by binary search). Keys
 * below the cutoff provably cannot exceed the floor and are skipped — so the
 * emitted set is unchanged. Scored candidates use the IDENTICAL compensated-f64
 * arithmetic and fixed summation order as the all-pairs kernel, so every emitted
 * score matches bit-for-bit.
 *
 * Determinism mirrors the sibling exactly:
 *   - All f64; q/k dot products compensated in fixed order m=0..d_model-1; score
 *     compensated in fixed order d=0..head_dim-1 (same helpers as the sibling).
 *   - Key norms computed in fixed key order s=0..vocab-1 (the only place norms
 *     enter arithmetic that affects pruning, never the emitted score).
 *   - Survivors within a query row are RE-SORTED to ascending key index before
 *     the threshold test, so the emitted order is independent of the norm-sorted
 *     scan order (and of any tie order in the norm sort).
 *   - Two passes with a fixed-order prefix-sum of per-row survivor counts assign
 *     stable output offsets, so the emitted set + order are bit-identical across
 *     thread counts and across any [q0,q1) window split. TBB parallelizes only
 *     across query rows.
 *
 * Overflow / bounded memory: same contract as the sibling — keep the largest
 * whole-row leading prefix that fits, set *overflow, return that prefix's count.
 * Extra memory beyond the caller buffer: O(vocab) shared key-norm/index table +
 * O(vocab·head_dim) shared cache of the projected keys (both built once) and
 * O((q1-q0)·head_dim) cache of the projected queries (built once), plus O(q1-q0)
 * row metadata. Each token is PROJECTED EXACTLY ONCE per head — the projections
 * are cached and scoring is a pure cached dot product (no re-projection). */

namespace {

/* One Neumaier compensated-summation step: fold `term` into (sum, c).
 * IDENTICAL to qk_pairs_threshold.cpp so candidate scores match bitwise. */
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
 * sibling's project_token. */
inline void project_token(const float* E_row, const float* W, size_t d_model,
                          size_t head_dim, double* proj /*[head_dim]*/) {
    for (size_t d = 0; d < head_dim; ++d) {
        const float* w = W + d * d_model;
        double sum = 0.0, c = 0.0;
        for (size_t m = 0; m < d_model; ++m)
            neumaier_add(sum, c, (double)E_row[m] * (double)w[m]);
        proj[d] = sum + c;
    }
}

/* L2 norm of a projected vector, compensated f64 over the squares (fixed order).
 * Used only for Cauchy–Schwarz pruning, never for the emitted score. */
inline double proj_l2(const double* v, size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, v[d] * v[d]);
    const double ss = sum + c;
    return ss > 0.0 ? std::sqrt(ss) : 0.0;
}

/* score(t,s) = Σ_d q_t[d]*k_s[d], compensated, fixed order over d, over the
 * PRE-PROJECTED q_t and k_s vectors. The projections are computed exactly once
 * (per head) and cached; this is a pure dot product over the cached vectors in
 * the SAME fixed order d=0..head_dim-1 as the sibling, so the emitted score is
 * bit-identical to qk_pairs_threshold.cpp's score_qk. */
inline double score_qk_cached(const double* q_t, const double* k_s,
                              size_t head_dim) {
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, q_t[d] * k_s[d]);
    return sum + c;
}

/* (norm, key_index) for the norm-descending key table. */
struct KeyNorm {
    double   norm;
    uint32_t key;
};

} /* namespace */

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

    /* ── Key projections + norm table (computed ONCE, fixed key order). ─────────
     * k_cache[s*head_dim .. ] holds the projected f64 vector for key s — the SAME
     * compensated projection the sibling recomputes per pair, kept so scoring is a
     * pure cached dot product. keys_desc[i] = (‖k_s‖, s), sorted by norm desc, for
     * Cauchy–Schwarz pruning only. Each key is projected EXACTLY ONCE here. */
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
    /* Descending by norm; ties broken by key index so the prefix boundary is
     * fully deterministic. (Order within a query is fixed later regardless.) */
    std::sort(keys_desc.begin(), keys_desc.end(),
              [](const KeyNorm& a, const KeyNorm& b) {
                  if (a.norm != b.norm) return a.norm > b.norm;
                  return a.key < b.key;
              });

    /* Per-query candidate-key cutoff: a key s can exceed the floor only if
     * ‖q_t‖·‖k_s‖ > noise_floor. We keep, conservatively, every key with
     * ‖q_t‖·‖k_s‖ >= noise_floor (>= rather than > so a candidate is never
     * wrongly dropped at the boundary). Since keys_desc is norm-descending, the
     * kept candidates form a prefix [0, n_cand). With qnorm > 0, the condition
     * ‖k_s‖ >= noise_floor/qnorm is monotone in ‖k_s‖, so the prefix length is
     * found by binary search on the norm-desc array. */
    auto candidate_prefix_len = [&](double qnorm) -> size_t {
        if (qnorm <= 0.0) return 0;          /* zero query: no pair can survive */
        if (noise_floor <= 0.0) return vocab; /* floor 0 admits any nonzero pair */
        const double cutoff = noise_floor / qnorm;
        /* Largest prefix whose norms are all >= cutoff. keys_desc is descending,
         * so find the first element with norm < cutoff. */
        size_t lo = 0, hi = vocab;
        while (lo < hi) {
            const size_t mid = lo + (hi - lo) / 2;
            if (keys_desc[mid].norm >= cutoff) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    };

    /* Per-row survivor counts (window-bounded metadata, O(n_rows)). */
    std::vector<size_t> counts(n_rows, 0);

    /* ── Project every query in the window EXACTLY ONCE into q_cache, reused
     * across pass 1 (count) and pass 2 (write). q_cache[ri*head_dim ..] holds the
     * projected f64 vector for query row q0+ri; q_norms[ri] its L2 norm. Same
     * compensated projection the sibling uses, just computed once. */
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

    /* ── Pass 1: per query row, use cached q_t, find candidate prefix, score each
     * candidate as a cached dot product (identical arithmetic), count
     * |score| > floor. ─────────────────────────────────────────────────────── */
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

    /* ── Prefix-sum -> stable per-row output offsets (fixed row order). Keep
     * only whole leading rows that fit in out_cap; the rest overflow. ───────── */
    std::vector<size_t> offsets(n_rows);
    size_t acc = 0;
    size_t rows_fit = n_rows;
    for (size_t ri = 0; ri < n_rows; ++ri) {
        if (acc + counts[ri] > out_cap) {  /* this row would not fully fit */
            rows_fit = ri;
            *overflow = 1;
            break;
        }
        offsets[ri] = acc;
        acc += counts[ri];
    }
    const size_t total_written = acc;  /* pairs in the kept whole-row prefix */

    /* ── Pass 2: write each kept row's survivors at its stable offset, emitted
     * in ASCENDING key index so the set + order match the all-pairs kernel
     * regardless of the norm-sorted scan order. ─────────────────────────────── */
    auto write_row = [&](size_t ri) {
        if (counts[ri] == 0) return;
        const size_t t = q0 + ri;
        const double* q_t = q_cache.data() + ri * head_dim;
        const size_t n_cand = candidate_prefix_len(q_norms[ri]);
        /* Collect survivors, then sort by key index ascending. counts[ri] is the
         * exact survivor count from pass 1, so this vector is small. */
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
