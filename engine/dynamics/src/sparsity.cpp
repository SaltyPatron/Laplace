#include "laplace/dynamics/sparsity.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl.h>
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#  include <oneapi/tbb/enumerable_thread_specific.h>
#endif

/* === Multi-pass filter (Chunk 6 Stories 6.10/6.11; not part of Framework
 *     Epic #232). Stubs preserved for linkage. === */

extern "C"
int sparsity_per_tensor_topk(const double*            weights,
                             size_t                   n,
                             const sparsity_params_t* params,
                             uint8_t*                 out_mask) {
    (void)weights; (void)n; (void)params; (void)out_mask;
    return -1;
}

extern "C"
int sparsity_per_row_topk(const double*            weights,
                          size_t                   rows,
                          size_t                   cols,
                          const sparsity_params_t* params,
                          uint8_t*                 inout_mask) {
    (void)weights; (void)rows; (void)cols; (void)params; (void)inout_mask;
    return -1;
}

extern "C"
int sparsity_probe_validate(const double*            weights,
                            size_t                   n,
                            const sparsity_params_t* params,
                            uint8_t*                 inout_mask) {
    (void)weights; (void)n; (void)params; (void)inout_mask;
    return -1;
}

/* === Streaming variants (Framework Epic #232 / Stories B.1 + B.2) === */

namespace {

/* Compute |x| into a destination buffer. MKL VML when available;
 * portable loop otherwise. */
void abs_into(const double* src, size_t n, double* dst) {
#ifdef LAPLACE_HAS_MKL
    vdAbs((MKL_INT)n, src, dst);
#else
    for (size_t i = 0; i < n; ++i) dst[i] = std::fabs(src[i]);
#endif
}

/* Cross-thread-count-deterministic mask fill: mask[i] = (abs[i] >= thr).
 * The masking pass itself is order-independent (each index only depends
 * on its own input), so TBB parallelism is safe. */
void mask_threshold(const double* abs_vals, size_t n, double threshold, uint8_t* out_mask) {
#ifdef LAPLACE_HAS_MKL
    constexpr size_t kSerialBelow = 65536;
    if (n >= kSerialBelow) {
        oneapi::tbb::parallel_for(
            oneapi::tbb::blocked_range<size_t>(0, n),
            [&](const oneapi::tbb::blocked_range<size_t>& r) {
                for (size_t i = r.begin(); i < r.end(); ++i) {
                    out_mask[i] = (abs_vals[i] >= threshold) ? (uint8_t)1 : (uint8_t)0;
                }
            });
        return;
    }
#endif
    for (size_t i = 0; i < n; ++i) {
        out_mask[i] = (abs_vals[i] >= threshold) ? (uint8_t)1 : (uint8_t)0;
    }
}

/* Per-row top-k inner kernel.
 *
 * scratch is a caller-supplied buffer of at least row_size doubles, reused
 * across rows by the same thread (TBB thread-local storage at the
 * parallel_for level). Eliminates per-row heap allocation — the dominant
 * cost in the naive implementation for high row counts.
 *
 * Threshold computation: compute |x| into scratch, then nth_element with
 * std::greater puts the k-th largest at position k-1; the value at that
 * slot is the threshold. We then recompute |x| in the mask pass (cheap
 * single-op fabs), avoiding a second buffer copy. */
void per_row_topk_kernel(const double* row,
                         size_t        row_size,
                         size_t        k,
                         uint8_t*      out_mask,
                         double*       scratch) {
    if (k == 0) {
        std::memset(out_mask, 0, row_size);
        return;
    }
    if (k >= row_size) {
        std::memset(out_mask, 1, row_size);
        return;
    }
    for (size_t i = 0; i < row_size; ++i) scratch[i] = std::fabs(row[i]);
    std::nth_element(scratch, scratch + (k - 1), scratch + row_size,
                     std::greater<double>());
    const double threshold = scratch[k - 1];
    for (size_t i = 0; i < row_size; ++i) {
        out_mask[i] = (std::fabs(row[i]) >= threshold) ? (uint8_t)1 : (uint8_t)0;
    }
}

} // namespace

extern "C"
int sparsity_per_tensor_topk_streaming(
    const double* values,
    size_t        n,
    double        topk_pct,
    uint8_t*      out_mask) {
    if (!values || !out_mask) return -1;
    if (n == 0) return -1;
    if (!(topk_pct > 0.0 && topk_pct <= 1.0)) return -1;

    /* Target retain count = ceil(n * topk_pct). At topk_pct == 1.0, k == n
     * (every value retained) — we still need to compute |values| and
     * threshold to handle the edge case cleanly. */
    const double k_d = std::ceil((double)n * topk_pct);
    size_t k = (size_t)k_d;
    if (k == 0) k = 1;             /* monotonic guard: top-k% always retains >=1 */
    if (k > n) k = n;

    if (k == n) {
        /* Trivial: every value retained. */
        std::memset(out_mask, 1, n);
        return 0;
    }

    /* Materialize |values| into temp buffer. */
    std::vector<double> abs_vals(n);
    abs_into(values, n, abs_vals.data());

    /* Find threshold: k-th largest |value|. nth_element with std::greater
     * places the k-th largest at position k-1 (zero-indexed). */
    std::vector<double> tmp(abs_vals);
    std::nth_element(tmp.begin(), tmp.begin() + (k - 1), tmp.end(), std::greater<double>());
    const double threshold = tmp[k - 1];

    /* Masking pass (TBB-parallel for large n; serial otherwise). */
    mask_threshold(abs_vals.data(), n, threshold, out_mask);
    return 0;
}

extern "C"
int sparsity_per_row_topk_streaming(
    const double* rows,
    size_t        row_count,
    size_t        row_size,
    size_t        k,
    uint8_t*      out_masks) {
    if (!rows || !out_masks) return -1;
    if (row_count == 0 || row_size == 0) return -1;

#ifdef LAPLACE_HAS_MKL
    constexpr size_t kSerialBelow = 4;
    if (row_count >= kSerialBelow) {
        /* Thread-local scratch buffer reused across all rows the thread
         * processes — eliminates per-row heap alloc which was the
         * dominant cost in the naive (1 alloc/row) shape. */
        oneapi::tbb::enumerable_thread_specific<std::vector<double>> scratch;
        oneapi::tbb::parallel_for(
            oneapi::tbb::blocked_range<size_t>(0, row_count),
            [&](const oneapi::tbb::blocked_range<size_t>& r) {
                auto& s = scratch.local();
                if (s.size() < row_size) s.resize(row_size);
                for (size_t row_idx = r.begin(); row_idx < r.end(); ++row_idx) {
                    per_row_topk_kernel(
                        rows + row_idx * row_size,
                        row_size,
                        k,
                        out_masks + row_idx * row_size,
                        s.data());
                }
            });
        return 0;
    }
#endif
    /* Serial fallback: one scratch buffer reused across rows. */
    std::vector<double> scratch(row_size);
    for (size_t row_idx = 0; row_idx < row_count; ++row_idx) {
        per_row_topk_kernel(
            rows + row_idx * row_size,
            row_size,
            k,
            out_masks + row_idx * row_size,
            scratch.data());
    }
    return 0;
}
