#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif




int bilinear_edges_tile(
    const double* left,  size_t row_begin, size_t row_end,
    const double* right, size_t n_right,
    size_t r, double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    size_t cap, size_t* out_count, int* overflow);

/* Evaluate an already-admitted set of bilinear pairs.  Unlike
 * bilinear_edges_tile this never enumerates or thresholds the n×n product:
 * `rows`/`cols` are the complete externally-admitted candidate page.  The
 * arena is the exact RMS of L·R^T, computed without materialising that matrix;
 * each returned score is a transient calibration input and must not be stored
 * in evidence.  `out_outcomes` is the durable three-valued receipt candidate.
 */
int bilinear_candidates_calibrate(
    const double* left, size_t n_left,
    const double* right, size_t n_right, size_t r,
    const int* rows, const int* cols, size_t pair_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes,
    double* out_arena_rms);

/* Compute one circuit arena, then reuse it while streaming all admitted
 * keyset pages. */
int bilinear_arena_rms(
    const double* left, size_t n_left,
    const double* right, size_t n_right, size_t r,
    double* out_arena_rms);

int bilinear_candidates_calibrate_at_arena(
    const double* left, size_t n_left,
    const double* right, size_t n_right, size_t r,
    const int* rows, const int* cols, size_t pair_count,
    double arena_rms,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes);

/* Opaque, page-reusable contraction arena. Token rows that resolve to the same
 * canonical entity are averaged as one set before any circuit projection; the
 * caller supplies every tokenizer row and its canonical entity index. Numeric
 * factors live only in this context and are released after the candidate pages
 * have been consumed. */
typedef struct bilinear_contraction_context bilinear_contraction_context_t;

int bilinear_direct_contraction_create(
    const float* left_rows, const float* right_rows,
    size_t vocabulary_rows, size_t dimension,
    const int* token_rows, const int* entity_indexes,
    size_t token_count, size_t entity_count,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, size_t* out_resident_bytes);

/* left/right weights are rank-by-dimension row-major matrices. Biases are
 * optional rank-vectors. For narrow circuits the implementation projects in
 * bounded row tiles. For a wide circuit (notably FFN) it first contracts the
 * two weights to a dimension-by-dimension kernel, preventing V*rank factors
 * and rank-squared Gram matrices from existing at all. */
int bilinear_projected_contraction_create(
    const float* embedding_rows,
    size_t vocabulary_rows, size_t dimension,
    const int* token_rows, const int* entity_indexes,
    size_t token_count, size_t entity_count,
    const float* left_weight, const float* left_bias,
    const float* right_weight, const float* right_bias,
    size_t rank,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, size_t* out_resident_bytes);

int bilinear_contraction_candidates_calibrate(
    const bilinear_contraction_context_t* context,
    const int* rows, const int* cols, size_t pair_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes);

void bilinear_contraction_free(bilinear_contraction_context_t* context);

/* Fold each candidate's circuit-score column through the canonical Glicko-2
 * rating-period calculus, then return and classify its native expected score.
 * Scores are circuit-major: score[circuit * candidate_count + candidate]. */
int model_circuit_calibrate_glicko(
    const int64_t* scores_fp1e9,
    const int64_t* opponent_ratings_fp1e9,
    const int64_t* opponent_rds_fp1e9,
    size_t circuit_count, size_t candidate_count,
    int64_t* out_scores_fp1e9, int16_t* out_outcomes);

int project_embedding(const float* pts, size_t n, size_t d,
                      const float* W, size_t r, double* out);



int project_embedding_d(const double* pts, size_t n, size_t d,
                        const float* W, size_t r, double* out);

int norm_rows_d(double* data, size_t n, size_t dim);

int expand_kv_heads_d(const double* kv, size_t n, size_t n_heads, size_t n_kv,
                      size_t head_dim, double* out);

/* Transpose one contiguous column block from a row-major float matrix.  Model
 * contraction uses this to turn output-projection/down-projection columns into
 * right-hand token factors without managed tensor math. */
int transpose_column_block_f(
    const float* matrix, size_t rows, size_t cols,
    size_t column_begin, size_t column_count, float* out);

#ifdef __cplusplus
}
#endif
