#include "laplace/synthesis/bf16_decoder.h"

#include <stdint.h>
#include <string.h>

#if defined(__AVX2__) && defined(__x86_64__)
#  include <immintrin.h>
#  define LAPLACE_HAS_AVX2 1
#endif

int laplace_bf16_decode(const void* raw_bytes, size_t n_elements, double* out) {
    if (!raw_bytes || !out) return -1;
    if (n_elements == 0) return 0;

    const uint16_t* bf16 = (const uint16_t*)raw_bytes;
    size_t i = 0;

#ifdef LAPLACE_HAS_AVX2
    /* 8 BF16 per iteration.
     *
     * BF16 is the upper 16 bits of float32. Widening via _mm256_cvtepu16_epi32
     * zero-extends each uint16 to uint32 in a 256-bit register; shifting left
     * by 16 places the BF16 bits in the upper half of each 32-bit slot, which
     * is exactly the float32 bit pattern (sign+exponent+upper-mantissa, lower
     * mantissa bits zeroed). _mm256_castsi256_ps reinterprets without conversion.
     * The 256-bit float result then splits into two 128-bit halves for
     * double-precision widening (_mm256_cvtps_pd). */
    for (; i + 8 <= n_elements; i += 8) {
        __m128i v16 = _mm_loadu_si128((const __m128i*)(bf16 + i));
        __m256i v32 = _mm256_cvtepu16_epi32(v16);
        v32 = _mm256_slli_epi32(v32, 16);
        __m256  vf  = _mm256_castsi256_ps(v32);
        __m256d dlo = _mm256_cvtps_pd(_mm256_castps256_ps128(vf));
        __m256d dhi = _mm256_cvtps_pd(_mm256_extractf128_ps(vf, 1));
        _mm256_storeu_pd(out + i,     dlo);
        _mm256_storeu_pd(out + i + 4, dhi);
    }
#endif

    /* Scalar tail (or full path when AVX2 not available). */
    for (; i < n_elements; ++i) {
        uint32_t u32 = (uint32_t)bf16[i] << 16;
        float f32;
        memcpy(&f32, &u32, sizeof(f32));
        out[i] = (double)f32;
    }
    return 0;
}
