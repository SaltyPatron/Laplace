#include "bilinear_edges_internal.hpp"
#include "laplace/dynamics/model_math.h"
#include "laplace/core/score.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <utility>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

namespace {

using laplace::dynamics::detail::kProjectionTileRows;
using laplace::dynamics::detail::score_value;

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

} // namespace

extern "C"
int bilinear_contraction_entity_salience(
    const bilinear_contraction_context_t* context,
    const hash128_t* entity_ids, std::size_t entity_count,
    int64_t* out_scores_fp1e9, int32_t* out_order)
{
    if (!context || !entity_ids || !out_scores_fp1e9 || !out_order
        || entity_count == 0 || entity_count != context->entity_count
        || context->rank == 0)
        return -1;

    const std::vector<double>& left = context->left;
    const std::vector<double>& right =
        context->shared_factors ? context->left : context->right;
    if (left.size() != entity_count * context->rank
        || right.size() != entity_count * context->rank)
        return -1;

    std::vector<double> magnitudes(entity_count, 0.0);
    long double squared_sum = 0.0L;
    for (std::size_t entity = 0; entity < entity_count; ++entity) {
        const double* l = left.data() + entity * context->rank;
        const double* r = right.data() + entity * context->rank;
        long double magnitude_squared = 0.0L;
        for (std::size_t k = 0; k < context->rank; ++k) {
            if (!std::isfinite(l[k]) || !std::isfinite(r[k])) return -2;
            magnitude_squared += (long double)l[k] * l[k];
            if (!context->shared_factors)
                magnitude_squared += (long double)r[k] * r[k];
        }
        double magnitude = std::sqrt((double)magnitude_squared);
        if (!std::isfinite(magnitude)) return -2;
        magnitudes[entity] = magnitude;
        squared_sum += magnitude_squared;
    }

    const double salience_arena =
        std::sqrt((double)(squared_sum / (long double)entity_count));
    if (!std::isfinite(salience_arena)) return -2;
    for (std::size_t entity = 0; entity < entity_count; ++entity) {
        out_scores_fp1e9[entity] = salience_arena == 0.0
            ? 500000000LL
            : laplace_score_fp(magnitudes[entity], salience_arena);
        out_order[entity] = (int32_t)entity;
    }

    std::sort(out_order, out_order + entity_count,
        [&](int32_t a, int32_t b) {
            const int64_t as = out_scores_fp1e9[(std::size_t)a];
            const int64_t bs = out_scores_fp1e9[(std::size_t)b];
            if (as != bs) return as > bs;
            return std::memcmp(entity_ids + a, entity_ids + b, sizeof(hash128_t)) < 0;
        });
    return 0;
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
int ffn_contraction_create(const float* embedding_rows,
    std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    const float* up, const float* up_bias, const float* gate, const float* gate_bias,
    const float* down, const float* down_bias, std::size_t intermediate, int activation,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, std::size_t* out_resident_bytes)
{
    return ffn_contraction_create_output(embedding_rows, embedding_rows, vocabulary_rows, dimension, token_rows, entity_indexes, token_count, entity_count, up, up_bias, gate, gate_bias, down, down_bias, intermediate, activation, out_context, out_arena_rms, out_resident_bytes);
}

extern "C"
int ffn_contraction_create_output(const float* embedding_rows, const float* output_rows,
    std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    const float* up, const float* up_bias, const float* gate, const float* gate_bias,
    const float* down, const float* down_bias, std::size_t intermediate, int activation,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, std::size_t* out_resident_bytes)
{
    if (!out_context || !out_arena_rms || !out_resident_bytes) return -1;
    *out_context = nullptr;
    *out_arena_rms = 0.0;
    *out_resident_bytes = 0;
    if (!embedding_rows || !output_rows || !up || !down || !token_rows || !entity_indexes ||
        dimension == 0 || intermediate == 0 || token_count == 0 || entity_count == 0 ||
        dimension > (std::size_t)INT32_MAX || entity_count > (std::size_t)INT32_MAX ||
        intermediate > SIZE_MAX / dimension || dimension > SIZE_MAX / dimension ||
        entity_count > SIZE_MAX / dimension ||
        vocabulary_rows > SIZE_MAX / dimension || activation < 0 || activation > 5 ||
        (activation == 0 && !gate) || (!gate && gate_bias)) return -1;
    auto finite = [](const float* values, std::size_t n) {
        if (values)
            for (std::size_t i = 0; i < n; ++i)
                if (!std::isfinite(values[i])) return false;
        return true;
    };
    const std::size_t weights = intermediate * dimension;
    if (!finite(up, weights) || !finite(gate, weights) || !finite(down, weights) ||
        !finite(up_bias, intermediate) || !finite(gate_bias, intermediate) ||
        !finite(down_bias, dimension)) return -2;
    try {
        auto context = std::make_unique<bilinear_contraction_context_t>();
        context->entity_count = entity_count;
        context->rank = dimension;
        int rc = aggregate_canonical_rows(output_rows, vocabulary_rows, dimension,
            token_rows, entity_indexes, token_count, entity_count, false, context->right);
        if (rc != 0) return rc;
        context->left.assign(entity_count * dimension, 0.0);
        std::vector<std::size_t> counts(entity_count, 0);
        const std::size_t tile = std::min(token_count, kProjectionTileRows);
        std::vector<double> inputs(tile * dimension), outputs(tile * dimension);
        for (std::size_t begin = 0; begin < token_count; begin += tile) {
            const std::size_t rows = std::min(tile, token_count - begin);
            for (std::size_t r = 0; r < rows; ++r) {
                const float* src = embedding_rows + (std::size_t)token_rows[begin + r] * dimension;
                for (std::size_t d = 0; d < dimension; ++d) inputs[r * dimension + d] = src[d];
            }
            rc = ffn_write_vectors_ex_d(inputs.data(), rows, dimension,
                up, up_bias, gate, gate_bias, intermediate, down, down_bias,
                dimension, activation, outputs.data());
            if (rc != 0) return rc;
            // Nonlinear activation does not commute with averaging aliases.
            for (std::size_t r = 0; r < rows; ++r) {
                const std::size_t entity = (std::size_t)entity_indexes[begin + r];
                double* dst = context->left.data() + entity * dimension;
                for (std::size_t d = 0; d < dimension; ++d) {
                    const double value = outputs[r * dimension + d];
                    if (!std::isfinite(value)) return -2;
                    dst[d] += value;
                }
                ++counts[entity];
            }
        }
        for (std::size_t entity = 0; entity < entity_count; ++entity) {
            if (counts[entity] == 0) return -1;
            for (std::size_t d = 0; d < dimension; ++d)
                context->left[entity * dimension + d] /= (double)counts[entity];
        }
        rc = factor_arena(context->left, context->right, entity_count, dimension, &context->arena);
        if (rc != 0) return rc;
        *out_arena_rms = context->arena;
        *out_resident_bytes = sizeof(*context) +
            (context->left.capacity() + context->right.capacity()) * sizeof(double);
        *out_context = context.release();
        return 0;
    } catch (const std::bad_alloc&) {
        return -3;
    } catch (const std::exception&) {
        return -1;
    }
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
    return bilinear_projected_contraction_create_output(embedding_rows, embedding_rows, vocabulary_rows, dimension, token_rows, entity_indexes, token_count, entity_count, left_weight, left_bias, right_weight, right_bias, rank, out_context, out_arena_rms, out_resident_bytes);
}

extern "C"
int bilinear_projected_contraction_create_output(
    const float* embedding_rows, const float* output_rows,
    std::size_t vocabulary_rows, std::size_t dimension,
    const int* token_rows, const int* entity_indexes,
    std::size_t token_count, std::size_t entity_count,
    const float* left_weight, const float* left_bias,
    const float* right_weight, const float* right_bias,
    std::size_t rank,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, std::size_t* out_resident_bytes)
{
    if (!embedding_rows || !output_rows || !left_weight || !right_weight || !out_context
        || !out_arena_rms || !out_resident_bytes || rank == 0)
        return -1;
    *out_context = nullptr;

    std::vector<double> x;
    int rc = aggregate_canonical_rows(
        embedding_rows, vocabulary_rows, dimension, token_rows, entity_indexes,
        token_count, entity_count, true, x);
    if (rc != 0) return rc;
    std::vector<double> output;
    if (output_rows != embedding_rows) {
        rc = aggregate_canonical_rows(output_rows, vocabulary_rows, dimension,
            token_rows, entity_indexes, token_count, entity_count, true, output);
        if (rc != 0) return rc;
    }
    const double* y = output_rows == embedding_rows ? x.data() : output.data();
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
                1.0, y + begin * augmented, (MKL_INT)augmented,
                bw.data(), (MKL_INT)augmented, 0.0,
                context->right.data() + begin * rank, (MKL_INT)rank);
        }
#else
        for (std::size_t row = 0; row < entity_count; ++row) {
            for (std::size_t k = 0; k < rank; ++k) {
                double l = 0.0, r = 0.0;
                for (std::size_t j = 0; j < augmented; ++j) {
                    l += x[row * augmented + j] * aw[k * augmented + j];
                    r += y[row * augmented + j] * bw[k * augmented + j];
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
                1.0, x.data() + begin * augmented, (MKL_INT)augmented,
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
                    v += x[row * augmented + b] * kernel[b * augmented + a];
                context->left[row * augmented + a] = v;
            }
#endif
        context->right = output_rows == embedding_rows ? std::move(x) : std::move(output);
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
