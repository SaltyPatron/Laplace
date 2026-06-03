#include "laplace/core/hilbert4d.h"

#include <math.h>
#include <stdint.h>
#include <string.h>

/* Skilling (2004) 4D Hilbert curve over the [-1, 1]^4 bounding hyperbox.
 *
 *: 32 bits per dimension, 4 dimensions → 128-bit index.
 * Pure integer bit-twiddling once coordinates are quantized — no FP in the
 * algorithm proper.
 *
 *   double[4] in [-1, 1]
 *     → quantize to uint32_t[4] (one cell per 2^-32 unit)
 *     → AxestoTranspose (Skilling) — transforms axes to Hilbert transpose
 *     → interleave the 4 × 32 transpose bits into 128 contiguous index bits
 *
 * Decode reverses every step.
 *
 * Reference: John Skilling, "Programming the Hilbert curve", AIP Conf. Proc.
 * 707, 381 (2004), doi:10.1063/1.1751381. The AxestoTranspose / TransposetoAxes
 * routines below are a direct transcription of his pseudocode for n=4, b=32. */

#define LAPLACE_HILBERT_DIMS 4
#define LAPLACE_HILBERT_BITS 32

static uint32_t laplace_quantize_coord(double c) {
    /* Map [-1, 1] → [0, 2^32). Clamp out-of-range inputs to the edges so callers
     * don't silently wrap., ~20% of the hyperbox is the corners
     * outside the unit 4-ball — those are legitimate query points (interior
     * centroids can be slightly inside, but never outside [-1,1] by construction). */
    if (c <= -1.0) return 0u;
    if (c >=  1.0) return 0xFFFFFFFFu;
    /* Centered: cell k spans [k/2^31 - 1, (k+1)/2^31 - 1). */
    const double scaled = (c + 1.0) * 2147483648.0;  /* 2^31 */
    if (scaled >= 4294967295.0) return 0xFFFFFFFFu;
    return (uint32_t)scaled;
}

static double laplace_dequantize_coord(uint32_t u) {
    /* Inverse of laplace_quantize_coord: return the cell center. */
    return ((double)u + 0.5) / 2147483648.0 - 1.0;
}

static void laplace_axes_to_transpose(uint32_t X[LAPLACE_HILBERT_DIMS]) {
    /* Skilling 2004 AxestoTranspose, fixed to n=4 dims and b=32 bits. */
    const uint32_t M = 1u << (LAPLACE_HILBERT_BITS - 1);  /* 0x80000000 */
    uint32_t P, Q, t;
    int i;

    /* Inverse undo */
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

    /* Gray encode */
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
    /* Skilling 2004 TransposetoAxes, fixed to n=4 dims and b=32 bits. */
    const uint32_t N = 2u << (LAPLACE_HILBERT_BITS - 1);  /* 2^32 logically; use shift fully */
    uint32_t P, Q, t;
    int i;

    /* Gray decode by H ^ (H/2) */
    t = X[LAPLACE_HILBERT_DIMS - 1] >> 1;
    for (i = LAPLACE_HILBERT_DIMS - 1; i > 0; i--) {
        X[i] ^= X[i - 1];
    }
    X[0] ^= t;

    /* Undo excess work */
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
    /* Interleave Skilling-transpose bits into a single 128-bit index, big-
     * endian byte layout (memcmp on out[] = numeric order of the index).
     *
     * Convention (matching the canonical galtay/hilbertcurve reference,
     * verified against its byte-for-byte output for 4D curves at p=2..32):
     *   bit (j*n + (n-1-i)) of the index (LSB=0)  =  bit j of X[i]
     * Equivalently, MSB-first across the index:
     *   index bit 0 (MSB)   = bit (b-1) of X[0]
     *   index bit 1         = bit (b-1) of X[1]
     *   index bit n-1       = bit (b-1) of X[n-1]
     *   index bit n         = bit (b-2) of X[0]
     *   ...
     *   index bit n*b-1     = bit 0 of X[n-1]
     *
     * The dim ordering matters: Skilling's AxestoTranspose uses X[0] as a
     * pivot, and the algorithm only produces a valid Hilbert curve when X[0]
     * is interleaved into the HIGH bits of the index. Using X[n-1] at high
     * bits gives a self-consistent but non-Hilbert curve. */
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
    /* Big-endian bytes (per laplace_pack_transpose_to_bytes) — memcmp gives
     * numerical order of the 128-bit Hilbert index, identical to PG btree
     * on the bytea(16) representation. */
    return memcmp(a->bytes, b->bytes, sizeof(a->bytes));
}
