#include "laplace/synthesis/qk_pairs_threshold.h"

#include <cmath>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#endif

/* Exact, deterministic, streaming threshold-based QK token-relation scorer.
 *
 * For one attention head: project each token through the head's Q and K weights
 * (q_proj·k_proj per ADR 0056:157), score query×key by dot product, and emit
 * every pair whose |score| strictly exceeds noise_floor. No top-k, no scale, no
 * vocab×vocab / vocab×k staging buffer.
 *
 * Determinism strategy — a deterministic prefix-sum of per-row counts:
 *   Each query row t in [q0, q1) is an independent, fixed-order computation of
 *   its scores against keys s = 0..vocab-1. We run two passes:
 *     Pass 1 (TBB across rows): count, per row, how many keys give |score|>floor.
 *     Then: prefix-sum the counts in fixed row order -> each row's stable output
 *           offset. These offsets depend ONLY on the counts (which are
 *           thread-count-independent), never on scheduling.
 *     Pass 2 (TBB across rows): each row recomputes its q_t, re-scores keys in
 *           fixed order s = 0..vocab-1, and writes its emitted pairs at its
 *           stable offset.
 *   Result: the emitted set and its order (rows ascending by t, within a row
 *   keys ascending by s) are bit-identical regardless of thread count or how the
 *   caller chose the [q0, q1) window. Memory is O(head_dim) per worker plus
 *   O(q1-q0) row metadata — never O(vocab·vocab) or O(vocab·k).
 *
 * Overflow: if the window's total emitted count exceeds out_cap, we keep the
 * largest WHOLE-ROW leading prefix whose pairs all fit (never a partial row),
 * set *overflow = 1, and return that prefix's count so the caller can retry a
 * smaller [q0, q1). The kept prefix is deterministic for the same reason. */

namespace {

/* One Neumaier compensated-summation step: fold `term` into (sum, c). */
inline void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

/* Project token `row` (E_f32 + tok*d_model) through head weight W
 * [head_dim x d_model] into proj[d] = Σ_m E[m]*W[d,m], compensated, fixed order
 * over m. Independent across d. */
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

/* score(t,s) = Σ_d q_t[d]*k_s[d], compensated, fixed order over d. */
inline double score_qk(const double* q_t, const float* E_s, const float* Wk,
                       size_t d_model, size_t head_dim, double* k_scratch) {
    project_token(E_s, Wk, d_model, head_dim, k_scratch);
    double sum = 0.0, c = 0.0;
    for (size_t d = 0; d < head_dim; ++d)
        neumaier_add(sum, c, q_t[d] * k_scratch[d]);
    return sum + c;
}

} /* namespace */

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

    /* Per-row above-threshold counts (window-bounded metadata, O(n_rows)). */
    std::vector<size_t> counts(n_rows, 0);

    /* ── Pass 1: count above-threshold keys per query row, fixed key order. ── */
    auto count_row = [&](size_t ri) {
        const size_t t = q0 + ri;
        std::vector<double> q_t(head_dim);
        std::vector<double> k_scratch(head_dim);
        project_token(E_f32 + t * d_model, Wq_head, d_model, head_dim, q_t.data());
        size_t n = 0;
        for (size_t s = 0; s < vocab; ++s) {
            const double sc = score_qk(q_t.data(), E_f32 + s * d_model, Wk_head,
                                       d_model, head_dim, k_scratch.data());
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

    /* ── Prefix-sum -> stable per-row output offsets (fixed row order). ──────
     * offsets[ri] is where row ri's pairs go; depends only on counts. Determine
     * how many whole leading rows fit in out_cap; the rest overflow. */
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

    /* ── Pass 2: write each kept row's pairs at its stable offset, fixed key
     * order. Rows >= rows_fit are skipped (overflowed). ─────────────────────── */
    auto write_row = [&](size_t ri) {
        if (counts[ri] == 0) return;
        const size_t t = q0 + ri;
        std::vector<double> q_t(head_dim);
        std::vector<double> k_scratch(head_dim);
        project_token(E_f32 + t * d_model, Wq_head, d_model, head_dim, q_t.data());
        size_t w = offsets[ri];
        for (size_t s = 0; s < vocab; ++s) {
            const double sc = score_qk(q_t.data(), E_f32 + s * d_model, Wk_head,
                                       d_model, head_dim, k_scratch.data());
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
