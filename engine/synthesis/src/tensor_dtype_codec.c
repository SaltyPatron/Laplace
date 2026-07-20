#include "laplace/synthesis/tensor_dtype_codec.h"

#include <math.h>
#include <stdint.h>
#include <string.h>

#if defined(__x86_64__)
#  include <immintrin.h>
#  if defined(__AVX2__)
#    define LAPLACE_HAS_AVX2 1
#  endif
#  if defined(__F16C__)
#    define LAPLACE_HAS_F16C 1
#  endif
#endif

int laplace_tensor_dtype_from_name(const char* name) {
    if (!name) return LAPLACE_TENSOR_DTYPE_UNKNOWN;
    if (strcmp(name, "F64")     == 0) return LAPLACE_TENSOR_DTYPE_F64;
    if (strcmp(name, "F32")     == 0) return LAPLACE_TENSOR_DTYPE_F32;
    if (strcmp(name, "F16")     == 0) return LAPLACE_TENSOR_DTYPE_F16;
    if (strcmp(name, "BF16")    == 0) return LAPLACE_TENSOR_DTYPE_BF16;
    if (strcmp(name, "F8_E5M2") == 0) return LAPLACE_TENSOR_DTYPE_F8_E5M2;
    if (strcmp(name, "F8_E4M3") == 0) return LAPLACE_TENSOR_DTYPE_F8_E4M3;
    if (strcmp(name, "I64")     == 0) return LAPLACE_TENSOR_DTYPE_I64;
    if (strcmp(name, "I32")     == 0) return LAPLACE_TENSOR_DTYPE_I32;
    if (strcmp(name, "I16")     == 0) return LAPLACE_TENSOR_DTYPE_I16;
    if (strcmp(name, "I8")      == 0) return LAPLACE_TENSOR_DTYPE_I8;
    if (strcmp(name, "U8")      == 0) return LAPLACE_TENSOR_DTYPE_U8;
    if (strcmp(name, "BOOL")    == 0) return LAPLACE_TENSOR_DTYPE_BOOL;
    return LAPLACE_TENSOR_DTYPE_UNKNOWN;
}

size_t laplace_tensor_dtype_size(int dtype) {
    switch (dtype) {
        case LAPLACE_TENSOR_DTYPE_F64:
        case LAPLACE_TENSOR_DTYPE_I64:     return 8;
        case LAPLACE_TENSOR_DTYPE_F32:
        case LAPLACE_TENSOR_DTYPE_I32:     return 4;
        case LAPLACE_TENSOR_DTYPE_F16:
        case LAPLACE_TENSOR_DTYPE_BF16:
        case LAPLACE_TENSOR_DTYPE_I16:     return 2;
        case LAPLACE_TENSOR_DTYPE_F8_E5M2:
        case LAPLACE_TENSOR_DTYPE_F8_E4M3:
        case LAPLACE_TENSOR_DTYPE_I8:
        case LAPLACE_TENSOR_DTYPE_U8:
        case LAPLACE_TENSOR_DTYPE_BOOL:    return 1;
        default:                           return 0;
    }
}

/* IEEE half -> float, exact. Scalar reference and the tail of the F16C path. */
static float half_to_float(uint16_t h) {
    const uint32_t sign = (uint32_t)(h & 0x8000u) << 16;
    const uint32_t exp  = (uint32_t)(h >> 10) & 0x1Fu;
    const uint32_t mant = (uint32_t)(h & 0x03FFu);
    uint32_t bits;

    if (exp == 0) {
        if (mant == 0) {
            bits = sign;                       /* +/-0 */
        } else {
            /* subnormal half: normalize into a float exponent */
            uint32_t m = mant, e = 0;
            while ((m & 0x0400u) == 0) { m <<= 1; ++e; }
            m &= 0x03FFu;
            bits = sign | ((127u - 15u - e + 1u) << 23) | (m << 13);
        }
    } else if (exp == 0x1Fu) {
        bits = sign | 0x7F800000u | (mant << 13); /* Inf / NaN, payload preserved */
    } else {
        bits = sign | ((exp + (127u - 15u)) << 23) | (mant << 13);
    }

    float f;
    memcpy(&f, &bits, sizeof f);
    return f;
}

/* bfloat16 -> float: the stored 16 bits ARE the high half of the float. */
static float bf16_to_float(uint16_t b) {
    uint32_t bits = (uint32_t)b << 16;
    float f;
    memcpy(&f, &bits, sizeof f);
    return f;
}

/* FP8 E5M2: sign|5 exp|2 mant re-seated into an IEEE half, then widened. */
static float f8_e5m2_to_float(uint8_t b) {
    const int sign = (b >> 7) & 1;
    const int exp  = (b >> 2) & 0x1F;
    const int mant = b & 0x3;
    const uint16_t half = (uint16_t)((sign << 15) | (exp << 10) | (mant << 8));
    return half_to_float(half);
}

/* FP8 E4M3 (OCP form): no infinities; exp==15 && mant==7 is the NaN slot. */
static float f8_e4m3_to_float(uint8_t b) {
    const int sign = (b >> 7) & 1;
    const int exp  = (b >> 3) & 0xF;
    const int mant = b & 0x7;
    float v;

    if (exp == 0) {
        v = (float)mant * 0.001953125f;             /* mant * 2^-9 */
    } else if (exp == 15 && mant == 7) {
        uint32_t nan_bits = 0x7FC00000u;
        memcpy(&v, &nan_bits, sizeof v);
        return sign ? -v : v;
    } else {
        const float scale = ldexpf(1.0f, exp - 7);
        v = (1.0f + (float)mant * 0.125f) * scale;
    }
    return sign ? -v : v;
}

int laplace_tensor_decode_f32(const void* raw_bytes,
                              size_t n_elements,
                              int dtype,
                              float* out) {
    if (!raw_bytes || !out) return -1;
    if (laplace_tensor_dtype_size(dtype) == 0) return -2;
    if (n_elements == 0) return 0;

    size_t i = 0;

    switch (dtype) {
        case LAPLACE_TENSOR_DTYPE_F32:
            memcpy(out, raw_bytes, n_elements * sizeof(float));
            return 0;

        case LAPLACE_TENSOR_DTYPE_F64: {
            const double* s = (const double*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_F16: {
            const uint16_t* s = (const uint16_t*)raw_bytes;
#ifdef LAPLACE_HAS_F16C
            for (; i + 8 <= n_elements; i += 8) {
                __m128i h = _mm_loadu_si128((const __m128i*)(s + i));
                _mm256_storeu_ps(out + i, _mm256_cvtph_ps(h));
            }
#endif
            for (; i < n_elements; ++i) out[i] = half_to_float(s[i]);
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_BF16: {
            const uint16_t* s = (const uint16_t*)raw_bytes;
#ifdef LAPLACE_HAS_AVX2
            for (; i + 8 <= n_elements; i += 8) {
                __m128i v16 = _mm_loadu_si128((const __m128i*)(s + i));
                __m256i v32 = _mm256_slli_epi32(_mm256_cvtepu16_epi32(v16), 16);
                _mm256_storeu_ps(out + i, _mm256_castsi256_ps(v32));
            }
#endif
            for (; i < n_elements; ++i) out[i] = bf16_to_float(s[i]);
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_F8_E5M2: {
            const uint8_t* s = (const uint8_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = f8_e5m2_to_float(s[i]);
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_F8_E4M3: {
            const uint8_t* s = (const uint8_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = f8_e4m3_to_float(s[i]);
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_I64: {
            const int64_t* s = (const int64_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_I32: {
            const int32_t* s = (const int32_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_I16: {
            const int16_t* s = (const int16_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_I8: {
            const int8_t* s = (const int8_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_U8: {
            const uint8_t* s = (const uint8_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = (float)s[i];
            return 0;
        }

        case LAPLACE_TENSOR_DTYPE_BOOL: {
            const uint8_t* s = (const uint8_t*)raw_bytes;
            for (; i < n_elements; ++i) out[i] = s[i] != 0 ? 1.0f : 0.0f;
            return 0;
        }

        default:
            return -2;
    }
}
