/*
 * knn_exact_cosine.c — MKL-tiled brute-force exact cosine KNN.
 *
 * Algorithm:
 *   1. L2-normalize a scratch copy of dictionary (and queries, when distinct).
 *   2. Compute similarities = Queries · Dictionary^T via cblas_dgemm.
 *      Dimensions: (n_queries × dim) × (dim × n_dict) → (n_queries × n_dict).
 *      Since both are L2-normalized, dot product == cosine similarity.
 *   3. For each query row, extract top-k via a min-heap (size k); finalize
 *      to descending order via in-place sort of the heap result.
 *
 * For very large dictionaries (n_dict × n_queries > 1B doubles) the GEMM
 * should be tiled along the n_dict dimension to fit cache; this baseline
 * implementation calls cblas_dgemm once for the whole matrix and is
 * adequate for vocabulary-scale (V ≤ 200K) inputs. Tiling is added in a
 * follow-up commit that benchmarks against the substrate's largest models.
 */

#include "laplace_pg/knn_exact.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#include <mkl.h>

/* ------------------------------------------------------------------ */
/* Min-heap over (similarity, index) pairs — used per-query to keep   */
/* the running top-k as we scan the n_dict similarity row.            */
/* ------------------------------------------------------------------ */

typedef struct {
    double sim;
    int    idx;
} sim_pair_t;

static inline void heap_swap(sim_pair_t *a, sim_pair_t *b) {
    sim_pair_t t = *a; *a = *b; *b = t;
}

static void heap_sift_down(sim_pair_t *heap, int n, int i) {
    /* Min-heap: smallest sim at the root. */
    for (;;) {
        const int l = 2 * i + 1;
        const int r = 2 * i + 2;
        int smallest = i;
        if (l < n && heap[l].sim < heap[smallest].sim) { smallest = l; }
        if (r < n && heap[r].sim < heap[smallest].sim) { smallest = r; }
        if (smallest == i) { return; }
        heap_swap(&heap[i], &heap[smallest]);
        i = smallest;
    }
}

static void heap_build(sim_pair_t *heap, int n) {
    for (int i = n / 2 - 1; i >= 0; --i) {
        heap_sift_down(heap, n, i);
    }
}

/* In-place sort of a populated heap (descending by sim).
 * After this, heap[0] holds the LARGEST sim (best match). */
static void heap_to_descending(sim_pair_t *heap, int n) {
    /* Heap-sort: repeatedly pop the smallest off the back, leaving
     * the array sorted ascending. Then reverse to get descending. */
    for (int end = n - 1; end > 0; --end) {
        heap_swap(&heap[0], &heap[end]);
        heap_sift_down(heap, end, 0);
    }
    /* The above produces descending order naturally because we built a
     * MIN-heap and popped smallest-first to the END — so heap[0..n-1]
     * after the loop is sorted DESCENDING by sim. No reverse needed. */
}

/* ------------------------------------------------------------------ */
/* L2-normalize each row of an n × dim matrix in place.               */
/* ------------------------------------------------------------------ */

static void l2_normalize_rows(double *m, int n_rows, int dim) {
    for (int r = 0; r < n_rows; ++r) {
        double *row = m + (size_t)r * (size_t)dim;
        double sum = 0.0;
        for (int c = 0; c < dim; ++c) { sum += row[c] * row[c]; }
        if (sum > 0.0 && isfinite(sum)) {
            const double inv = 1.0 / sqrt(sum);
            for (int c = 0; c < dim; ++c) { row[c] *= inv; }
        }
    }
}

/* ------------------------------------------------------------------ */
/* Top-k extraction from one row of the (n_queries × n_dict) similarity
 * matrix, with optional self-exclusion (skip column == query_index).
 * ------------------------------------------------------------------ */

static void top_k_from_row(
    const double *sim_row,
    int           n_dict,
    int           k,
    int           skip_index,    /* -1 if no self-exclusion */
    int          *out_idx,
    double       *out_sim)
{
    sim_pair_t *heap = (sim_pair_t *)malloc((size_t)k * sizeof(sim_pair_t));
    /* Caller guaranteed k > 0; failure to allocate would be catastrophic
     * but malloc returning NULL for a small k is unrealistic on any
     * sane platform. Defensive zeroing in case of wraparound. */
    if (!heap) {
        for (int j = 0; j < k; ++j) { out_idx[j] = -1; out_sim[j] = 0.0; }
        return;
    }

    /* Phase 1: fill heap with first k valid entries. */
    int filled = 0;
    int scan = 0;
    while (filled < k && scan < n_dict) {
        if (scan != skip_index) {
            heap[filled].sim = sim_row[scan];
            heap[filled].idx = scan;
            ++filled;
        }
        ++scan;
    }
    /* If n_dict < k, pad with -INFINITY entries so the heap is well-formed. */
    while (filled < k) {
        heap[filled].sim = -INFINITY;
        heap[filled].idx = -1;
        ++filled;
    }
    heap_build(heap, k);

    /* Phase 2: scan the rest, replacing the heap root if a larger sim arrives. */
    for (int j = scan; j < n_dict; ++j) {
        if (j == skip_index) { continue; }
        const double s = sim_row[j];
        if (s > heap[0].sim) {
            heap[0].sim = s;
            heap[0].idx = j;
            heap_sift_down(heap, k, 0);
        }
    }

    /* Phase 3: sort heap to descending by sim. */
    heap_to_descending(heap, k);

    for (int j = 0; j < k; ++j) {
        out_idx[j] = heap[j].idx;
        out_sim[j] = heap[j].sim;
    }
    free(heap);
}

/* ------------------------------------------------------------------ */
/* Public API                                                          */
/* ------------------------------------------------------------------ */

static int knn_cosine_internal(
    const double *queries_in,
    int           n_queries,
    const double *dictionary_in,
    int           n_dict,
    int           dim,
    int           k,
    int           self_exclude,   /* nonzero → exclude col == row */
    int          *out_indices,
    double       *out_similarities)
{
    if (queries_in == NULL || dictionary_in == NULL ||
        out_indices == NULL || out_similarities == NULL) { return 1; }
    if (n_queries <= 0 || n_dict <= 0 || dim <= 0) { return 1; }
    if (k <= 0 || k > n_dict) { return 1; }

    const size_t qsize = (size_t)n_queries * (size_t)dim;
    const size_t dsize = (size_t)n_dict    * (size_t)dim;
    const size_t ssize = (size_t)n_queries * (size_t)n_dict;

    double *q_norm = (double *)mkl_malloc(qsize * sizeof(double), 64);
    double *d_norm = (double *)mkl_malloc(dsize * sizeof(double), 64);
    double *sim    = (double *)mkl_malloc(ssize * sizeof(double), 64);
    if (!q_norm || !d_norm || !sim) {
        if (q_norm) mkl_free(q_norm);
        if (d_norm) mkl_free(d_norm);
        if (sim)    mkl_free(sim);
        return 2;
    }
    memcpy(q_norm, queries_in,    qsize * sizeof(double));
    memcpy(d_norm, dictionary_in, dsize * sizeof(double));
    l2_normalize_rows(q_norm, n_queries, dim);
    l2_normalize_rows(d_norm, n_dict,    dim);

    /* sim = q_norm · d_norm^T  (n_queries × n_dict). */
    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        n_queries, n_dict, dim,
        1.0,
        q_norm, dim,
        d_norm, dim,
        0.0,
        sim,    n_dict);

    for (int q = 0; q < n_queries; ++q) {
        const int skip = self_exclude ? q : -1;
        top_k_from_row(
            sim + (size_t)q * (size_t)n_dict,
            n_dict,
            k,
            skip,
            out_indices       + (size_t)q * (size_t)k,
            out_similarities  + (size_t)q * (size_t)k);
    }

    mkl_free(q_norm);
    mkl_free(d_norm);
    mkl_free(sim);
    return 0;
}

int laplace_knn_exact_cosine_d(
    const double *queries,
    int           n_queries,
    const double *dictionary,
    int           n_dict,
    int           dim,
    int           k,
    int          *out_indices,
    double       *out_similarities)
{
    return knn_cosine_internal(
        queries, n_queries, dictionary, n_dict, dim, k,
        /* self_exclude = */ 0,
        out_indices, out_similarities);
}

int laplace_knn_self_cosine_d(
    const double *dictionary,
    int           n_dict,
    int           dim,
    int           k,
    int          *out_indices,
    double       *out_similarities)
{
    return knn_cosine_internal(
        dictionary, n_dict, dictionary, n_dict, dim, k,
        /* self_exclude = */ 1,
        out_indices, out_similarities);
}
