#include "laplace/dynamics/bilinear_edges.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/score.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <cstring>
#include <new>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

struct bilinear_contraction_context {
    std::size_t entity_count = 0;
    std::size_t rank = 0;
    double arena = 0.0;
    std::vector<double> left;
    std::vector<double> right;
    bool shared_factors = false;
};

namespace {

constexpr std::size_t kProjectionTileRows = 4096;

int aggregate_canonical_rows(
    const float* source, std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    bool append_one, std::vector<double>& out)
{
    if (!source || !token_rows || !entity_indexes || vocabulary_rows == 0
        || dimension == 0 || token_count == 0 || entity_count == 0)
        return -1;
    const std::size_t stride = dimension + (append_one ? 1u : 0u);
    if (entity_count > std::numeric_limits<std::size_t>::max() / stride) return -1;
    out.assign(entity_count * stride, 0.0);
    std::vector<std::size_t> counts(entity_count, 0);
    for (std::size_t i = 0; i < token_count; ++i) {
        const int token = token_rows[i];
        const int entity = entity_indexes[i];
        if (token < 0 || entity < 0 || (std::size_t)token >= vocabulary_rows
            || (std::size_t)entity >= entity_count)
            return -1;
        const float* src = source + (std::size_t)token * dimension;
        double* dst = out.data() + (std::size_t)entity * stride;
        for (std::size_t j = 0; j < dimension; ++j) {
            if (!std::isfinite(src[j])) return -2;
            dst[j] += (double)src[j];
        }
        ++counts[(std::size_t)entity];
    }
    for (std::size_t entity = 0; entity < entity_count; ++entity) {
        if (counts[entity] == 0) return -1;
        double* dst = out.data() + entity * stride;
        const double inv = 1.0 / (double)counts[entity];
        for (std::size_t j = 0; j < dimension; ++j) dst[j] *= inv;
        if (append_one) dst[dimension] = 1.0;
    }
    return 0;
}

int factor_arena(
    const std::vector<double>& left, const std::vector<double>& right,
    std::size_t rows, std::size_t rank, double* out_arena)
{
    if (!out_arena || rows == 0 || rank == 0
        || left.size() != rows * rank || right.size() != rows * rank)
        return -1;
    std::vector<double> lg(rank * rank, 0.0);
    std::vector<double> rg(rank * rank, 0.0);
#ifdef LAPLACE_HAS_MKL
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
        (MKL_INT)rank, (MKL_INT)rank, (MKL_INT)rows,
        1.0, left.data(), (MKL_INT)rank, left.data(), (MKL_INT)rank,
        0.0, lg.data(), (MKL_INT)rank);
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
        (MKL_INT)rank, (MKL_INT)rank, (MKL_INT)rows,
        1.0, right.data(), (MKL_INT)rank, right.data(), (MKL_INT)rank,
        0.0, rg.data(), (MKL_INT)rank);
#else
    for (std::size_t row = 0; row < rows; ++row) {
        const double* l = left.data() + row * rank;
        const double* r = right.data() + row * rank;
        for (std::size_t a = 0; a < rank; ++a) {
            for (std::size_t b = 0; b < rank; ++b) {
                lg[a * rank + b] += l[a] * l[b];
                rg[a * rank + b] += r[a] * r[b];
            }
        }
    }
#endif
    double squared = 0.0;
    for (std::size_t i = 0; i < rank * rank; ++i) squared += lg[i] * rg[i];
    if (squared < 0.0 && squared > -1e-18) squared = 0.0;
    if (!(squared >= 0.0) || !std::isfinite(squared)) return -2;
    *out_arena = std::sqrt(squared / ((double)rows * (double)rows));
    return std::isfinite(*out_arena) ? 0 : -2;
}

void widen_weight_with_bias(
    const float* weight, const float* bias,
    std::size_t rank, std::size_t dimension,
    std::vector<double>& out)
{
    const std::size_t stride = dimension + 1;
    out.resize(rank * stride);
    for (std::size_t row = 0; row < rank; ++row) {
        for (std::size_t col = 0; col < dimension; ++col)
            out[row * stride + col] = (double)weight[row * dimension + col];
        out[row * stride + dimension] = bias ? (double)bias[row] : 0.0;
    }
}

int score_value(double value, double arena, int64_t* score_out, int16_t* outcome_out)
{
    if (!std::isfinite(value) || !score_out || !outcome_out) return -2;
    const int64_t score = arena == 0.0
        ? 500000000LL
        : (int64_t)std::llround(0.5 * (1.0 + std::tanh(value / arena)) * 1e9);
    int16_t outcome = LAPLACE_ATTESTATION_OUTCOME_DRAW;
    if (laplace_attestation_outcome_from_score_fp(score, &outcome) != 0) return -2;
    *score_out = score;
    *outcome_out = outcome;
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
        // to score.c and do not pretend a tie is positive evidence.
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
int bilinear_direct_contraction_create(
    const float* left_rows, const float* right_rows,
    std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, std::size_t* out_resident_bytes)
{
    if (!left_rows || !right_rows || !out_context || !out_arena_rms
        || !out_resident_bytes)
        return -1;
    *out_context = nullptr;
    std::vector<double> left;
    int rc = aggregate_canonical_rows(
        left_rows, vocabulary_rows, dimension, token_rows, entity_indexes,
        token_count, entity_count, false, left);
    if (rc != 0) return rc;
    std::vector<double> right;
    const bool shared = left_rows == right_rows;
    if (!shared) {
        rc = aggregate_canonical_rows(
            right_rows, vocabulary_rows, dimension, token_rows, entity_indexes,
            token_count, entity_count, false, right);
        if (rc != 0) return rc;
    }

    double arena = 0.0;
    rc = factor_arena(left, shared ? left : right, entity_count, dimension, &arena);
    if (rc != 0) return rc;
    auto* context = new (std::nothrow) bilinear_contraction_context_t();
    if (!context) return -3;
    context->entity_count = entity_count;
    context->rank = dimension;
    context->arena = arena;
    context->left = std::move(left);
    context->right = std::move(right);
    context->shared_factors = shared;
    *out_arena_rms = arena;
    *out_resident_bytes = (context->left.capacity() + context->right.capacity()) * sizeof(double);
    *out_context = context;
    return 0;
}

extern "C"
int bilinear_projected_contraction_create(
    const float* embedding_rows,
    std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    const float* left_weight, const float* left_bias,
    const float* right_weight, const float* right_bias,
    std::size_t rank,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, std::size_t* out_resident_bytes)
{
    if (!embedding_rows || !left_weight || !right_weight || !out_context
        || !out_arena_rms || !out_resident_bytes || rank == 0)
        return -1;
    *out_context = nullptr;

    std::vector<double> x;
    int rc = aggregate_canonical_rows(
        embedding_rows, vocabulary_rows, dimension, token_rows, entity_indexes,
        token_count, entity_count, true, x);
    if (rc != 0) return rc;
    const std::size_t augmented = dimension + 1;
    std::vector<double> aw;
    std::vector<double> bw;
    widen_weight_with_bias(left_weight, left_bias, rank, dimension, aw);
    widen_weight_with_bias(right_weight, right_bias, rank, dimension, bw);

    auto* context = new (std::nothrow) bilinear_contraction_context_t();
    if (!context) return -3;
    context->entity_count = entity_count;

    if (rank <= augmented) {
        // A head-sized circuit retains only its bounded projected factors. The
        // source rows are consumed in tiles so no second vocabulary-sized
        // intermediate accompanies them.
        context->rank = rank;
        context->left.resize(entity_count * rank);
        context->right.resize(entity_count * rank);
#ifdef LAPLACE_HAS_MKL
        for (std::size_t begin = 0; begin < entity_count; begin += kProjectionTileRows) {
            const std::size_t rows = std::min(kProjectionTileRows, entity_count - begin);
            cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)rows, (MKL_INT)rank, (MKL_INT)augmented,
                1.0, x.data() + begin * augmented, (MKL_INT)augmented,
                aw.data(), (MKL_INT)augmented, 0.0,
                context->left.data() + begin * rank, (MKL_INT)rank);
            cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
                (MKL_INT)rows, (MKL_INT)rank, (MKL_INT)augmented,
                1.0, x.data() + begin * augmented, (MKL_INT)augmented,
                bw.data(), (MKL_INT)augmented, 0.0,
                context->right.data() + begin * rank, (MKL_INT)rank);
        }
#else
        for (std::size_t row = 0; row < entity_count; ++row) {
            for (std::size_t k = 0; k < rank; ++k) {
                double l = 0.0, r = 0.0;
                for (std::size_t j = 0; j < augmented; ++j) {
                    l += x[row * augmented + j] * aw[k * augmented + j];
                    r += x[row * augmented + j] * bw[k * augmented + j];
                }
                context->left[row * rank + k] = l;
                context->right[row * rank + k] = r;
            }
        }
#endif
        std::vector<double>().swap(x);
    } else {
        // Wide factors are algebraically contracted before vocabulary rows:
        // (X A^T)(X B^T)^T = (X (A^T B)) X^T. Resident rank is therefore
        // d+1, never the FFN width, and both Gram reductions are (d+1)^2.
        context->rank = augmented;
        context->right = std::move(x);
        std::vector<double> kernel(augmented * augmented);
#ifdef LAPLACE_HAS_MKL
        cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
            (MKL_INT)augmented, (MKL_INT)augmented, (MKL_INT)rank,
            1.0, aw.data(), (MKL_INT)augmented,
            bw.data(), (MKL_INT)augmented,
            0.0, kernel.data(), (MKL_INT)augmented);
        context->left.resize(entity_count * augmented);
        for (std::size_t begin = 0; begin < entity_count; begin += kProjectionTileRows) {
            const std::size_t rows = std::min(kProjectionTileRows, entity_count - begin);
            cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasNoTrans,
                (MKL_INT)rows, (MKL_INT)augmented, (MKL_INT)augmented,
                1.0, context->right.data() + begin * augmented, (MKL_INT)augmented,
                kernel.data(), (MKL_INT)augmented, 0.0,
                context->left.data() + begin * augmented, (MKL_INT)augmented);
        }
#else
        for (std::size_t a = 0; a < augmented; ++a)
            for (std::size_t b = 0; b < augmented; ++b) {
                double v = 0.0;
                for (std::size_t k = 0; k < rank; ++k)
                    v += aw[k * augmented + a] * bw[k * augmented + b];
                kernel[a * augmented + b] = v;
            }
        context->left.resize(entity_count * augmented);
        for (std::size_t row = 0; row < entity_count; ++row)
            for (std::size_t a = 0; a < augmented; ++a) {
                double v = 0.0;
                for (std::size_t b = 0; b < augmented; ++b)
                    v += context->right[row * augmented + b] * kernel[b * augmented + a];
                context->left[row * augmented + a] = v;
            }
#endif
    }

    double arena = 0.0;
    rc = factor_arena(context->left, context->right,
        entity_count, context->rank, &arena);
    if (rc != 0) {
        delete context;
        return rc;
    }
    context->arena = arena;
    *out_arena_rms = arena;
    *out_resident_bytes = (context->left.capacity() + context->right.capacity()) * sizeof(double);
    *out_context = context;
    return 0;
}

extern "C"
int bilinear_contraction_candidates_calibrate(
    const bilinear_contraction_context_t* context,
    const int* rows, const int* cols, std::size_t pair_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes)
{
    if (!context || context->entity_count == 0 || context->rank == 0)
        return -1;
    if (pair_count != 0 && (!rows || !cols || !out_scores_fp1e9 || !out_outcomes))
        return -1;
    for (std::size_t i = 0; i < pair_count; ++i) {
        if (rows[i] < 0 || cols[i] < 0
            || (std::size_t)rows[i] >= context->entity_count
            || (std::size_t)cols[i] >= context->entity_count)
            return -1;
        const double* left = context->left.data() + (std::size_t)rows[i] * context->rank;
        const double* right = (context->shared_factors ? context->left.data() : context->right.data())
            + (std::size_t)cols[i] * context->rank;
#ifdef LAPLACE_HAS_MKL
        const double value = cblas_ddot((MKL_INT)context->rank, left, 1, right, 1);
#else
        double value = 0.0;
        for (std::size_t k = 0; k < context->rank; ++k) value += left[k] * right[k];
#endif
        int rc = score_value(value, context->arena,
            out_scores_fp1e9 + i, out_outcomes + i);
        if (rc != 0) return rc;
    }
    return 0;
}

extern "C"
void bilinear_contraction_free(bilinear_contraction_context_t* context)
{
    delete context;
}

extern "C"
int model_circuit_calibrate_glicko(
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
        for (std::size_t circuit = 0; circuit < circuit_count; ++circuit) {
            const int64_t score = scores_fp1e9[circuit * candidate_count + candidate];
            if (score < 0 || score > 1000000000LL
                || opponent_rds_fp1e9[circuit] < 0)
                return -1;
            observations[circuit] = {
                opponent_ratings_fp1e9[circuit],
                opponent_rds_fp1e9[circuit],
                score,
            };
        }
        glicko2_state_t state;
        glicko2_init(&state, LAPLACE_GLICKO2_NEUTRAL_MU_FP,
            350000000000LL, 60000000LL);
        glicko2_update_period(&state, observations.data(), observations.size(),
            LAPLACE_GLICKO2_DEFAULT_TAU, 0);
        const int64_t folded_score = laplace_glicko2_expected_score_fp(state.rating, state.rd);
        out_scores_fp1e9[candidate] = folded_score;
        if (laplace_attestation_outcome_from_score_fp(
                folded_score, out_outcomes + candidate) != 0)
            return -2;
    }
    return 0;
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
