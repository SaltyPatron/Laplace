#include "laplace/core/hilbert4d.h"

#include <math.h>
#include <stdint.h>
#include <string.h>

#define LAPLACE_HILBERT_DIMS 4
#define LAPLACE_HILBERT_BITS 32

static uint32_t laplace_quantize_coord(double c) {
    if (c <= -1.0) return 0u;
    if (c >=  1.0) return 0xFFFFFFFFu;
    const double scaled = (c + 1.0) * 2147483648.0;
    if (scaled >= 4294967295.0) return 0xFFFFFFFFu;
    return (uint32_t)scaled;
}

static double laplace_dequantize_coord(uint32_t u) {
    return ((double)u + 0.5) / 2147483648.0 - 1.0;
}

static void laplace_axes_to_transpose(uint32_t X[LAPLACE_HILBERT_DIMS]) {
    const uint32_t M = 1u << (LAPLACE_HILBERT_BITS - 1);
    uint32_t P, Q, t;
    int i;

    for (Q = M; Q > 1; Q >>= 1) {
        P = Q - 1;
        for (i = 0; i < LAPLACE_HILBERT_DIMS; i++) {
            if (X[i] & Q) {
                X[0] ^= P;
            } else {
                t = (X[0] ^ X[i]) & P;
                X[0] ^= t;
                X[i] ^= t;
            }
        }
    }

    for (i = 1; i < LAPLACE_HILBERT_DIMS; i++) {
        X[i] ^= X[i - 1];
    }
    t = 0;
    for (Q = M; Q > 1; Q >>= 1) {
        if (X[LAPLACE_HILBERT_DIMS - 1] & Q) t ^= Q - 1;
    }
    for (i = 0; i < LAPLACE_HILBERT_DIMS; i++) {
        X[i] ^= t;
    }
}

static void laplace_transpose_to_axes(uint32_t X[LAPLACE_HILBERT_DIMS]) {
    const uint32_t N = 2u << (LAPLACE_HILBERT_BITS - 1);
    uint32_t P, Q, t;
    int i;

    t = X[LAPLACE_HILBERT_DIMS - 1] >> 1;
    for (i = LAPLACE_HILBERT_DIMS - 1; i > 0; i--) {
        X[i] ^= X[i - 1];
    }
    X[0] ^= t;

    for (Q = 2; Q != N; Q <<= 1) {
        P = Q - 1;
        for (i = LAPLACE_HILBERT_DIMS - 1; i >= 0; i--) {
            if (X[i] & Q) {
                X[0] ^= P;
            } else {
                t = (X[0] ^ X[i]) & P;
                X[0] ^= t;
                X[i] ^= t;
            }
        }
    }
}

static void laplace_pack_transpose_to_bytes(const uint32_t X[LAPLACE_HILBERT_DIMS], uint8_t out[16]) {
    memset(out, 0, 16);
    for (int j = 0; j < LAPLACE_HILBERT_BITS; ++j) {
        for (int i = 0; i < LAPLACE_HILBERT_DIMS; ++i) {
            const int bit_pos_lsb = j * LAPLACE_HILBERT_DIMS + (LAPLACE_HILBERT_DIMS - 1 - i);
            const int bit_pos_msb = (LAPLACE_HILBERT_DIMS * LAPLACE_HILBERT_BITS - 1) - bit_pos_lsb;
            const uint32_t b = (X[i] >> j) & 1u;
            out[bit_pos_msb >> 3] |= (uint8_t)(b << (7 - (bit_pos_msb & 7)));
        }
    }
}

static void laplace_unpack_bytes_to_transpose(const uint8_t in[16], uint32_t X[LAPLACE_HILBERT_DIMS]) {
    memset(X, 0, sizeof(uint32_t) * LAPLACE_HILBERT_DIMS);
    for (int j = 0; j < LAPLACE_HILBERT_BITS; ++j) {
        for (int i = 0; i < LAPLACE_HILBERT_DIMS; ++i) {
            const int bit_pos_lsb = j * LAPLACE_HILBERT_DIMS + (LAPLACE_HILBERT_DIMS - 1 - i);
            const int bit_pos_msb = (LAPLACE_HILBERT_DIMS * LAPLACE_HILBERT_BITS - 1) - bit_pos_lsb;
            const uint32_t b = (in[bit_pos_msb >> 3] >> (7 - (bit_pos_msb & 7))) & 1u;
            X[i] |= (b << j);
        }
    }
}

void hilbert4d_encode(const double p[4], hilbert128_t* out) {
    uint32_t X[LAPLACE_HILBERT_DIMS] = {
        laplace_quantize_coord(p[0]),
        laplace_quantize_coord(p[1]),
        laplace_quantize_coord(p[2]),
        laplace_quantize_coord(p[3]),
    };
    laplace_axes_to_transpose(X);
    laplace_pack_transpose_to_bytes(X, out->bytes);
}

void hilbert4d_decode(const hilbert128_t* h, double out[4]) {
    uint32_t X[LAPLACE_HILBERT_DIMS];
    laplace_unpack_bytes_to_transpose(h->bytes, X);
    laplace_transpose_to_axes(X);
    out[0] = laplace_dequantize_coord(X[0]);
    out[1] = laplace_dequantize_coord(X[1]);
    out[2] = laplace_dequantize_coord(X[2]);
    out[3] = laplace_dequantize_coord(X[3]);
}

int hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b) {
    return memcmp(a->bytes, b->bytes, sizeof(a->bytes));
}
