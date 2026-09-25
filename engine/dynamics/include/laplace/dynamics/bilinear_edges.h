#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

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
 * bounded row tiles. For a wide linear circuit it first contracts the
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

/* Explicit output vocabulary; tied callers delegate with output_rows=embedding_rows. */
int bilinear_projected_contraction_create_output(
    const float* embedding_rows, const float* output_rows,
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

/* Rank every canonical entity by how strongly this circuit touches it. This is
 * a decomposition primitive, not a truth judgment or model forward pass. The
 * opaque contraction already contains canonicalized per-entity left/right
 * factors; this operation reduces those transient factors to one normalized
 * salience score per entity and returns a deterministic score-descending order.
 * entity_ids are used only for stable bytewise tie-breaking and are not copied
 * into the native context. */
int bilinear_contraction_entity_salience(
    const bilinear_contraction_context_t* context,
    const hash128_t* entity_ids, size_t entity_count,
    int64_t* out_scores_fp1e9, int32_t* out_order);

/* Per-token nonlinear FFN probe, reduced to canonical identities only AFTER
 * activation. Retains [mean(FFN(E_alias)), mean(E_alias)] factors of width d;
 * candidate calibration and arena reduction use the shared context operations. */
int ffn_contraction_create(const float* embedding_rows,
    size_t vocabulary_rows, size_t dimension,
    const int* token_rows, const int* entity_indexes,
    size_t token_count, size_t entity_count,
    const float* up, const float* up_bias, const float* gate, const float* gate_bias,
    const float* down, const float* down_bias, size_t intermediate, int activation,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, size_t* out_resident_bytes);

/* Explicit output vocabulary; tied callers delegate with output_rows=embedding_rows. */
int ffn_contraction_create_output(const float* embedding_rows, const float* output_rows,
    size_t vocabulary_rows, size_t dimension,
    const int* token_rows, const int* entity_indexes,
    size_t token_count, size_t entity_count,
    const float* up, const float* up_bias, const float* gate, const float* gate_bias,
    const float* down, const float* down_bias, size_t intermediate, int activation,
    bilinear_contraction_context_t** out_context,
    double* out_arena_rms, size_t* out_resident_bytes);

/* Declared significance contract for one circuit's pair evidence.
 *
 * Family: every ordered pair (i, j), i != j, of the context's canonical
 * entities; N = n(n-1).  Null: each subject i is compared with the circuit's
 * own score distribution for that subject, s_ij = l_i . r_j over every other
 * object j != i, whose exact mean and standard deviation follow from the factor
 * mean and covariance (less the subject's own term) without the n-by-n
 * product.  Test: z_ij = (s_ij - mean_i) / sd_i.
 * A pair is significant iff z_ij >= tau = sqrt(2 ln N), the universal
 * threshold: under a Gaussian null with those moments the expected number of
 * false pairs in the whole circuit is below one.  Only the upper tail is
 * evidence; a low or negative score is absence of evidence, never refutation.
 * Subjects with zero variance contribute nothing.
 *
 * With symmetric != 0 (a symmetric relation over shared factors, where
 * s_ij = s_ji) the family is the N = n(n-1)/2 unordered pairs; each is written
 * once, from its lower subject, when either endpoint's null rejects it
 * (z = max(z_ij, z_ji)).  Symmetric over unshared factors is refused.
 *
 * The grade is 0.5 * (1 + tanh(z / tau)): every emitted pair confirms, and a
 * stronger departure from the circuit's own null grades higher.
 *
 * Pages whole subjects starting at row_begin; capacity must be at least n - 1.
 * *out_row_end is the next subject to request (n when the circuit is done).
 * out_z and out_threshold are optional. */
int bilinear_contraction_significant_pairs(
    bilinear_contraction_context_t* context,
    size_t row_begin, int symmetric,
    int32_t* out_rows, int32_t* out_cols,
    int64_t* out_scores_fp1e9, double* out_z,
    size_t capacity,
    size_t* out_count, size_t* out_row_end,
    double* out_threshold);

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
