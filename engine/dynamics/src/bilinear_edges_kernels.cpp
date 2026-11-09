#include "bilinear_edges_internal.hpp"
#include "laplace/core/glicko2.h"
#include "laplace/core/score.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <utility>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#  include "laplace/dynamics/tbb_parallel.h"
#endif

namespace {

using laplace::dynamics::detail::kProjectionTileRows;

// Exact per-subject moments of s_ij = l_i . r_j over every object j:
//   mean_i = l_i . rbar,  var_i = l_i^T C l_i,  C = (1/n) R^T R - rbar rbar^T.
// Neither needs the n-by-n product; C is rank-by-rank.
int prepare_subject_null(bilinear_contraction_context_t* context)
{
    if (context->null_ready) return 0;
    const std::size_t n = context->entity_count;
    const std::size_t r = context->rank;
    if (n < 2 || r == 0) return -1;
    const std::vector<double>& L = context->left;
    const std::vector<double>& R = context->shared_factors ? context->left : context->right;
    if (L.size() != n * r || R.size() != n * r) return -1;

    std::vector<double> mean(r, 0.0);
    for (std::size_t j = 0; j < n; ++j) {
        const double* row = R.data() + j * r;
        for (std::size_t k = 0; k < r; ++k) mean[k] += row[k];
    }
    for (std::size_t k = 0; k < r; ++k) mean[k] /= (double)n;

    std::vector<double> cov(r * r, 0.0);
#ifdef LAPLACE_HAS_MKL
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
        (MKL_INT)r, (MKL_INT)r, (MKL_INT)n,
        1.0 / (double)n, R.data(), (MKL_INT)r, R.data(), (MKL_INT)r,
        0.0, cov.data(), (MKL_INT)r);
#else
    for (std::size_t j = 0; j < n; ++j) {
        const double* row = R.data() + j * r;
        for (std::size_t a = 0; a < r; ++a)
            for (std::size_t b = 0; b < r; ++b) cov[a * r + b] += row[a] * row[b];
    }
    for (double& v : cov) v /= (double)n;
#endif
    for (std::size_t a = 0; a < r; ++a)
        for (std::size_t b = 0; b < r; ++b) cov[a * r + b] -= mean[a] * mean[b];

    context->null_mean.assign(n, 0.0);
    context->null_sd.assign(n, 0.0);
    const std::size_t tile = std::min(n, kProjectionTileRows);
    std::vector<double> projected(tile * r);
    for (std::size_t begin = 0; begin < n; begin += tile) {
        const std::size_t rows = std::min(tile, n - begin);
#ifdef LAPLACE_HAS_MKL
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasNoTrans,
            (MKL_INT)rows, (MKL_INT)r, (MKL_INT)r,
            1.0, L.data() + begin * r, (MKL_INT)r, cov.data(), (MKL_INT)r,
            0.0, projected.data(), (MKL_INT)r);
#else
        for (std::size_t a = 0; a < rows; ++a)
            for (std::size_t k = 0; k < r; ++k) {
                double v = 0.0;
                for (std::size_t m = 0; m < r; ++m)
                    v += L[(begin + a) * r + m] * cov[m * r + k];
                projected[a * r + k] = v;
            }
#endif
        for (std::size_t a = 0; a < rows; ++a) {
            const std::size_t subject = begin + a;
            const double* l = L.data() + subject * r;
            const double* p = projected.data() + a * r;
            const double* self = R.data() + subject * r;
            double mu = 0.0, var = 0.0, diagonal = 0.0;
            for (std::size_t k = 0; k < r; ++k) {
                mu += l[k] * mean[k];
                var += l[k] * p[k];
                diagonal += l[k] * self[k];
            }
            // The family excludes the subject itself: remove s_ii from both moments.
            const double nn = (double)n;
            const double second = var + mu * mu;
            const double mu_other = (nn * mu - diagonal) / (nn - 1.0);
            const double var_other =
                (nn * second - diagonal * diagonal) / (nn - 1.0) - mu_other * mu_other;
            if (!std::isfinite(mu_other) || !std::isfinite(var_other)) return -2;
            context->null_mean[subject] = mu_other;
            // Roundoff can leave a degenerate subject slightly negative.
            context->null_sd[subject] = var_other > 0.0 ? std::sqrt(var_other) : 0.0;
        }
    }
    context->null_ready = true;
    return 0;
}

int calibrate_model_circuits(
    bool affirmative_only,
    const int64_t* scores_fp1e9,
    const int64_t* opponent_ratings_fp1e9,
    const int64_t* opponent_rds_fp1e9,
    std::size_t circuit_count, std::size_t candidate_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes)
{
    if (!scores_fp1e9 || !opponent_ratings_fp1e9 || !opponent_rds_fp1e9
        || !out_scores_fp1e9 || !out_outcomes
        || circuit_count == 0 || candidate_count == 0)
        return -1;
    std::vector<glicko2_observation_t> observations(circuit_count);
    for (std::size_t candidate = 0; candidate < candidate_count; ++candidate) {
        std::size_t used = 0;
        for (std::size_t circuit = 0; circuit < circuit_count; ++circuit) {
            const int64_t score = scores_fp1e9[circuit * candidate_count + candidate];
            if (score < 0 || score > 1000000000LL
                || opponent_rds_fp1e9[circuit] < 0)
                return -1;
            if (affirmative_only && score < 500000000LL) return -1;
            if (affirmative_only && score == 500000000LL) continue;
            observations[used++] = {
                opponent_ratings_fp1e9[circuit],
                opponent_rds_fp1e9[circuit],
                score,
            };
        }
        if (used == 0) {
            out_scores_fp1e9[candidate] = 500000000LL;
            out_outcomes[candidate] = LAPLACE_ATTESTATION_OUTCOME_DRAW;
            continue;
        }
        glicko2_state_t state;
        glicko2_init(&state, LAPLACE_GLICKO2_NEUTRAL_MU_FP,
            350000000000LL, 60000000LL);
        glicko2_update_period(&state, observations.data(), used,
            LAPLACE_GLICKO2_DEFAULT_TAU, 0);
        const int64_t folded_score = laplace_glicko2_expected_score_fp(state.rating, state.rd);
        out_scores_fp1e9[candidate] = folded_score;
        // Presence of affirmative circuit evidence is a typed outcome; a rating
        // or absence of evidence must not be reinterpreted as semantic refutation.
        if (affirmative_only) out_outcomes[candidate] = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
        else if (laplace_attestation_outcome_from_score_fp(
                     folded_score, out_outcomes + candidate) != 0) return -2;
    }
    return 0;
}

} // namespace

extern "C"
int bilinear_edges_tile(
    const double* left,  std::size_t row_begin, std::size_t row_end,
    const double* right, std::size_t n_right,
    std::size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    std::size_t cap, std::size_t* out_count, int* overflow)
{
    if (!left || !right || !out_rows || !out_cols || !out_vals || !out_count || !overflow)
        return -1;
    if (row_end <= row_begin || n_right == 0 || r == 0) return -1;

    *out_count = 0;
    *overflow  = 0;
    const std::size_t t = row_end - row_begin;

#ifdef LAPLACE_HAS_MKL
    std::vector<double> M(t * n_right);
    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)t, (MKL_INT)n_right, (MKL_INT)r,
        1.0,
        left + row_begin * r, (MKL_INT)r,
        right,                (MKL_INT)r,
        0.0,
        M.data(),             (MKL_INT)n_right);

    std::size_t cnt = 0;
    for (std::size_t a = 0; a < t; ++a) {
        const double* Mrow = M.data() + a * n_right;
        const int gi = (int)(row_begin + a);
        for (std::size_t b = 0; b < n_right; ++b) {
            const double v = Mrow[b];
            if (std::fabs(v) > theta) {
                if (cnt >= cap) { *overflow = 1; *out_count = cnt; return 0; }
                out_rows[cnt] = gi;
                out_cols[cnt] = (int)b;
                out_vals[cnt] = v;
                if (out_scores) out_scores[cnt] = (long long)laplace_score_fp(v, 1.0);
                ++cnt;
            }
        }
    }
    *out_count = cnt;
    return 0;
#else
    (void)left; (void)right; (void)r; (void)theta;
    (void)out_rows; (void)out_cols; (void)out_vals; (void)out_scores; (void)cap; (void)t;
    return -2;
#endif
}

extern "C"
int bilinear_arena_rms(
    const double* left, std::size_t n_left,
    const double* right, std::size_t n_right, std::size_t r,
    double* out_arena_rms)
{
    if (!left || !right || !out_arena_rms || n_left == 0 || n_right == 0 || r == 0)
        return -1;
    if (n_left > std::numeric_limits<std::size_t>::max() / r
        || n_right > std::numeric_limits<std::size_t>::max() / r)
        return -1;

    // ||L R^T||²_F = tr((L^T L)(R^T R)).  This is the whole circuit's
    // calibration arena without a vocabulary-square product or selection pass.
    if (r > std::numeric_limits<std::size_t>::max() / r) return -1;
    for (std::size_t i = 0; i < n_left * r; ++i)
        if (!std::isfinite(left[i])) return -2;
    for (std::size_t i = 0; i < n_right * r; ++i)
        if (!std::isfinite(right[i])) return -2;
    std::vector<double> left_gram(r * r, 0.0);
    std::vector<double> right_gram(r * r, 0.0);
#ifdef LAPLACE_HAS_MKL
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
        (MKL_INT)r, (MKL_INT)r, (MKL_INT)n_left,
        1.0, left, (MKL_INT)r, left, (MKL_INT)r,
        0.0, left_gram.data(), (MKL_INT)r);
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
        (MKL_INT)r, (MKL_INT)r, (MKL_INT)n_right,
        1.0, right, (MKL_INT)r, right, (MKL_INT)r,
        0.0, right_gram.data(), (MKL_INT)r);
#else
    for (std::size_t row = 0; row < n_left; ++row) {
        const double* v = left + row * r;
        for (std::size_t a = 0; a < r; ++a) {
            for (std::size_t b = 0; b < r; ++b) left_gram[a * r + b] += v[a] * v[b];
        }
    }
    for (std::size_t row = 0; row < n_right; ++row) {
        const double* v = right + row * r;
        for (std::size_t a = 0; a < r; ++a) {
            for (std::size_t b = 0; b < r; ++b) right_gram[a * r + b] += v[a] * v[b];
        }
    }
#endif

    double squared = 0.0;
    for (std::size_t i = 0; i < r * r; ++i) squared += left_gram[i] * right_gram[i];
    // Roundoff can make an all-zero circuit infinitesimally negative.
    if (squared < 0.0L && squared > -1e-18L) squared = 0.0L;
    if (!(squared >= 0.0L) || !std::isfinite((double)squared)) return -2;
    double denom = (double)n_left * (double)n_right;
    double arena = std::sqrt(squared / denom);
    if (!std::isfinite(arena)) return -2;
    *out_arena_rms = arena;

    return 0;
}

extern "C"
int bilinear_candidates_calibrate_at_arena(
    const double* left, std::size_t n_left,
    const double* right, std::size_t n_right, std::size_t r,
    const int* rows, const int* cols, std::size_t pair_count,
    double arena,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes)
{
    if (!left || !right || n_left == 0 || n_right == 0 || r == 0
        || !std::isfinite(arena) || arena < 0.0)
        return -1;
    if (pair_count != 0 && (!rows || !cols || !out_scores_fp1e9 || !out_outcomes))
        return -1;

    for (std::size_t i = 0; i < pair_count; ++i) {
        if (rows[i] < 0 || cols[i] < 0 || (std::size_t)rows[i] >= n_left || (std::size_t)cols[i] >= n_right)
            return -1;
        const double* l = left + (std::size_t)rows[i] * r;
        const double* rr = right + (std::size_t)cols[i] * r;
#ifdef LAPLACE_HAS_MKL
        double value = cblas_ddot((MKL_INT)r, l, 1, rr, 1);
#else
        double value = 0.0;
        for (std::size_t k = 0; k < r; ++k) value += l[k] * rr[k];
#endif
        if (!std::isfinite(value)) return -2;
        // A zero-energy circuit makes every relation unknown.  Do not feed 0/0
        // to score.c; use a neutral draw score.
        int64_t score = arena == 0.0
            ? 500000000LL
            : (int64_t)std::llround(0.5 * (1.0 + std::tanh(value / arena)) * 1e9);
        int16_t outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
        if (laplace_attestation_outcome_from_score_fp(score, &outcome) != 0) return -2;
        out_scores_fp1e9[i] = score;
        out_outcomes[i] = outcome;
    }
    return 0;
}

extern "C"
int bilinear_candidates_calibrate(
    const double* left, std::size_t n_left,
    const double* right, std::size_t n_right, std::size_t r,
    const int* rows, const int* cols, std::size_t pair_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes,
    double* out_arena_rms)
{
    int rc = bilinear_arena_rms(left, n_left, right, n_right, r, out_arena_rms);
    if (rc != 0) return rc;
    return bilinear_candidates_calibrate_at_arena(
        left, n_left, right, n_right, r,
        rows, cols, pair_count, *out_arena_rms,
        out_scores_fp1e9, out_outcomes);
}

extern "C"
int bilinear_contraction_significant_candidates(
    bilinear_contraction_context_t* context,
    const int* rows, const int* cols, std::size_t pair_count, int symmetric,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes)
{
    if (!context || context->entity_count < 2 || context->rank == 0
        || (symmetric && !context->shared_factors)) return -1;
    if (pair_count == 0) return 0;
    if (!rows || !cols || !out_scores_fp1e9 || !out_outcomes) return -1;
    const std::size_t n = context->entity_count;
    for (std::size_t i = 0; i < pair_count; ++i)
        if (rows[i] < 0 || cols[i] < 0
            || (std::size_t)rows[i] >= n || (std::size_t)cols[i] >= n) return -1;
    int rc;
    try { rc = prepare_subject_null(context); }
    catch (const std::bad_alloc&) { return -3; }
    if (rc != 0) return rc;
    const double family = symmetric ? (double)n * (double)(n - 1) / 2.0
                                    : (double)n * (double)(n - 1);
    const double tau = std::sqrt(2.0 * std::log(family));
    const auto& R = context->shared_factors ? context->left : context->right;
    for (std::size_t i = 0; i < pair_count; ++i) {
        out_scores_fp1e9[i] = 500000000LL;
        out_outcomes[i] = LAPLACE_ATTESTATION_OUTCOME_DRAW;
        const std::size_t subject = (std::size_t)rows[i], object = (std::size_t)cols[i];
        if (subject == object || !(tau > 0.0)) continue;
        const double* left = context->left.data() + subject * context->rank;
        const double* right = R.data() + object * context->rank;
#ifdef LAPLACE_HAS_MKL
        const double value = cblas_ddot((MKL_INT)context->rank, left, 1, right, 1);
#else
        double value = 0.0;
        for (std::size_t k = 0; k < context->rank; ++k) value += left[k] * right[k];
#endif
        if (!std::isfinite(value)) return -2;
        double z = context->null_sd[subject] > 0.0
            ? (value - context->null_mean[subject]) / context->null_sd[subject]
            : -std::numeric_limits<double>::infinity();
        if (symmetric && context->null_sd[object] > 0.0)
            z = std::max(z, (value - context->null_mean[object]) / context->null_sd[object]);
        if (!(z >= tau)) continue;
        if (!std::isfinite(z)) return -2;
        out_scores_fp1e9[i] = (int64_t)std::llround(0.5 * (1.0 + std::tanh(z / tau)) * 1e9);
        out_outcomes[i] = LAPLACE_ATTESTATION_OUTCOME_CONFIRM;
    }
    return 0;
}

extern "C"
int bilinear_contraction_significant_pairs(
    bilinear_contraction_context_t* context,
    std::size_t row_begin, int symmetric,
    int32_t* out_rows, int32_t* out_cols,
    int64_t* out_scores_fp1e9, double* out_z,
    std::size_t capacity,
    std::size_t* out_count, std::size_t* out_row_end,
    double* out_threshold)
{
    if (!context || !out_rows || !out_cols || !out_scores_fp1e9
        || !out_count || !out_row_end)
        return -1;
    *out_count = 0;
    *out_row_end = row_begin;
    const std::size_t n = context->entity_count;
    const std::size_t r = context->rank;
    // One subject can contribute at most n-1 pairs; a page must hold one subject.
    // An unordered family needs s_ij = s_ji, which only shared factors guarantee.
    if (n < 2 || r == 0 || row_begin > n || capacity < n - 1
        || n > (std::size_t)INT32_MAX || (symmetric && !context->shared_factors))
        return -1;
    int rc = 0;
    try {
        rc = prepare_subject_null(context);
    } catch (const std::bad_alloc&) {
        return -3;
    }
    if (rc != 0) return rc;
    const double family = symmetric
        ? (double)n * (double)(n - 1) / 2.0
        : (double)n * (double)(n - 1);
    const double tau = std::sqrt(2.0 * std::log(family));
    if (out_threshold) *out_threshold = tau;
    if (row_begin == n) return 0;

    const std::vector<double>& L = context->left;
    const std::vector<double>& R = context->shared_factors ? context->left : context->right;
    constexpr std::size_t kSubjectTile = 256;
    std::vector<double> block;
    std::vector<std::vector<std::pair<int32_t, double>>> hits;
    try {
        block.resize(std::min(kSubjectTile, n) * n);
        hits.resize(std::min(kSubjectTile, n));
    } catch (const std::bad_alloc&) {
        return -3;
    }

    std::size_t count = 0;
    std::size_t row = row_begin;
    while (row < n) {
        const std::size_t rows = std::min(kSubjectTile, n - row);
#ifdef LAPLACE_HAS_MKL
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
            (MKL_INT)rows, (MKL_INT)n, (MKL_INT)r,
            1.0, L.data() + row * r, (MKL_INT)r, R.data(), (MKL_INT)r,
            0.0, block.data(), (MKL_INT)n);
#else
        for (std::size_t a = 0; a < rows; ++a)
            for (std::size_t j = 0; j < n; ++j) {
                double v = 0.0;
                for (std::size_t k = 0; k < r; ++k) v += L[(row + a) * r + k] * R[j * r + k];
                block[a * n + j] = v;
            }
#endif
        auto scan = [&](std::size_t a) {
            std::vector<std::pair<int32_t, double>>& found = hits[a];
            found.clear();
            const std::size_t subject = row + a;
            const double sd = context->null_sd[subject];
            const double mu = context->null_mean[subject];
            const double* scores = block.data() + a * n;
            if (symmetric) {
                // Unordered pair {i, j}, written once from its lower subject: it is
                // significant when either endpoint's own null rejects it.
                for (std::size_t j = subject + 1; j < n; ++j) {
                    double z = -std::numeric_limits<double>::infinity();
                    if (sd > 0.0) z = (scores[j] - mu) / sd;
                    const double other_sd = context->null_sd[j];
                    if (other_sd > 0.0)
                        z = std::max(z, (scores[j] - context->null_mean[j]) / other_sd);
                    if (z >= tau) found.emplace_back((int32_t)j, z);
                }
                return;
            }
            if (!(sd > 0.0)) return;
            for (std::size_t j = 0; j < n; ++j) {
                if (j == subject) continue;
                const double z = (scores[j] - mu) / sd;
                if (z >= tau) found.emplace_back((int32_t)j, z);
            }
        };
#ifdef LAPLACE_HAS_MKL
        laplace::tbb_ops::parallel_for(
            oneapi::tbb::blocked_range<std::size_t>(0, rows),
            [&](const oneapi::tbb::blocked_range<std::size_t>& range) {
                for (std::size_t a = range.begin(); a != range.end(); ++a) scan(a);
            });
#else
        for (std::size_t a = 0; a < rows; ++a) scan(a);
#endif
        for (std::size_t a = 0; a < rows; ++a) {
            const auto& found = hits[a];
            if (count + found.size() > capacity) {
                *out_count = count;
                *out_row_end = row + a;
                return 0;
            }
            for (const auto& [col, z] : found) {
                if (!std::isfinite(z)) return -2;
                out_rows[count] = (int32_t)(row + a);
                out_cols[count] = col;
                out_scores_fp1e9[count] =
                    (int64_t)std::llround(0.5 * (1.0 + std::tanh(z / tau)) * 1e9);
                if (out_z) out_z[count] = z;
                ++count;
            }
        }
        row += rows;
    }
    *out_count = count;
    *out_row_end = n;
    return 0;
}

extern "C"
int model_circuit_calibrate_glicko(
    const int64_t* scores, const int64_t* ratings, const int64_t* rds,
    std::size_t circuits, std::size_t candidates, int64_t* output, int16_t* outcomes)
{
    return calibrate_model_circuits(false, scores, ratings, rds, circuits, candidates, output, outcomes);
}

extern "C"
int model_circuit_corroboration_glicko(
    const int64_t* scores, const int64_t* ratings, const int64_t* rds,
    std::size_t circuits, std::size_t candidates, int64_t* output, int16_t* outcomes)
{
    return calibrate_model_circuits(true, scores, ratings, rds, circuits, candidates, output, outcomes);
}

extern "C"
int project_embedding(const float* pts, std::size_t n, std::size_t d,
                      const float* W, std::size_t r, double* out)
{
    if (!pts || !W || !out || n == 0 || d == 0 || r == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    // SGEMM on the inputs as they arrive. This widened BOTH operands into heap
    // doubles (n*d + r*d elements) and called DGEMM — manufacturing mantissa
    // bits that fp32 arguments never carried, then paying half CPU throughput
    // to multiply them (measured 2026-08-12: 0.252 vs 0.434 TFLOP/s). Only the
    // n*r result is widened now, and the operands are passed through untouched.
    // GH #1023. Output stays double* so the ABI and every LibraryImport are
    // unchanged; the wider fp64->fp32 question is GH #1024.
    std::vector<float> C((std::size_t)n * r);

    cblas_sgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)n, (MKL_INT)r, (MKL_INT)d,
        1.0f, pts, (MKL_INT)d, W, (MKL_INT)d,
        0.0f, C.data(), (MKL_INT)r);

    for (std::size_t i = 0; i < (std::size_t)n * r; ++i) out[i] = (double)C[i];
    return 0;
#else
    (void)pts; (void)W; (void)out;
    return -2;
#endif
}

extern "C"
int project_embedding_d(const double* pts, std::size_t n, std::size_t d,
                        const float* W, std::size_t r, double* out)
{
    if (!pts || !W || !out || n == 0 || d == 0 || r == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    // pts is genuinely double here, so this stays DGEMM — but W arrives as
    // float and was widened into a heap temporary for no reason other than
    // matching the other operand. MKL has no mixed-precision GEMM, so the
    // widening is unavoidable; keeping it explicit and local. GH #1023.
    std::vector<double> Wd((std::size_t)r * d);
    for (std::size_t i = 0; i < (std::size_t)r * d; ++i) Wd[i] = (double)W[i];

    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)n, (MKL_INT)r, (MKL_INT)d,
        1.0, pts, (MKL_INT)d, Wd.data(), (MKL_INT)d,
        0.0, out, (MKL_INT)r);
    return 0;
#else
    (void)pts; (void)W; (void)out;
    return -2;
#endif
}

extern "C"
int norm_rows_d(double* data, std::size_t n, std::size_t dim)
{
    if (!data || n == 0 || dim == 0) return -1;
#ifdef LAPLACE_HAS_MKL
    for (std::size_t i = 0; i < n; ++i) {
        double* row = data + i * dim;
        double ss = cblas_ddot((MKL_INT)dim, row, 1, row, 1);
        if (ss > 0.0) {
            double inv = 1.0 / std::sqrt(ss);
            cblas_dscal((MKL_INT)dim, inv, row, 1);
        }
    }
    return 0;
#else
    for (std::size_t i = 0; i < n; ++i) {
        double* row = data + i * dim;
        double ss = 0.0;
        for (std::size_t c = 0; c < dim; ++c) ss += row[c] * row[c];
        double inv = ss > 0.0 ? 1.0 / std::sqrt(ss) : 0.0;
        for (std::size_t c = 0; c < dim; ++c) row[c] *= inv;
    }
    return 0;
#endif
}

extern "C"
int expand_kv_heads_d(const double* kv, std::size_t n, std::size_t n_heads,
                      std::size_t n_kv, std::size_t head_dim, double* out)
{
    if (!kv || !out || n == 0 || n_heads == 0 || n_kv == 0 || head_dim == 0) return -1;
    const std::size_t kv_dim = n_kv * head_dim;
    const std::size_t attn_dim = n_heads * head_dim;
    if (kv_dim == attn_dim) {
        std::memcpy(out, kv, n * attn_dim * sizeof(double));
        return 0;
    }
    for (std::size_t i = 0; i < n; ++i) {
        const double* src = kv + i * kv_dim;
        double* dst = out + i * attn_dim;
        for (std::size_t h = 0; h < n_heads; ++h) {
            std::size_t kh = std::min(n_kv - 1, h * n_kv / std::max<std::size_t>(1, n_heads));
            std::memcpy(dst + h * head_dim, src + kh * head_dim, head_dim * sizeof(double));
        }
    }
    return 0;
}

extern "C"
int transpose_column_block_f(
    const float* matrix, std::size_t rows, std::size_t cols,
    std::size_t column_begin, std::size_t column_count, float* out)
{
    if (!matrix || !out || rows == 0 || cols == 0 || column_count == 0
        || column_begin > cols || column_count > cols - column_begin)
        return -1;
    for (std::size_t c = 0; c < column_count; ++c)
        for (std::size_t row = 0; row < rows; ++row)
            out[c * rows + row] = matrix[row * cols + column_begin + c];
    return 0;
}
