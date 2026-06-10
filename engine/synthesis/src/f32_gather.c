#include "laplace/synthesis/f32_gather.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#if defined(__AVX2__) && defined(__x86_64__)
#  include <immintrin.h>
#  define LAPLACE_F32_GATHER_AVX2 1
#endif

int laplace_f32_gather_to_f64(
    const float* src,
    const int*   row_map,
    size_t       n_rows,
    size_t       d,
    double*      out) {
    if (!src || !row_map || !out) return -1;
    if (n_rows == 0 || d == 0) return 0;

    for (size_t r = 0; r < n_rows; ++r) {
        int idx = row_map[r];
        if (idx < 0) continue;
        const float* row = src + (size_t)idx * d;
        double* dst = out + r * d;
        size_t c = 0;
#ifdef LAPLACE_F32_GATHER_AVX2
        for (; c + 4 <= d; c += 4) {
            __m128  vf  = _mm_loadu_ps(row + c);
            __m256d vd0 = _mm256_cvtps_pd(vf);
            _mm256_storeu_pd(dst + c, vd0);
        }
#endif
        for (; c < d; ++c)
            dst[c] = (double)row[c];
    }
    return 0;
}
