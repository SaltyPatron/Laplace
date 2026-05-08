/*
 * hilbert_4d.c — Skilling 2003 transpose algorithm for 4D Hilbert curve.
 *
 * Reference: John Skilling, "Programming the Hilbert Curve", AIP Conf. Proc.
 * 707 (2004) 381-387. The transpose representation works in any dimension
 * n with bit-depth p in O(n*p) time. We use n = 4, p = 16.
 */

#include "laplace_pg/hilbert.h"

#include <math.h>

#define LAPLACE_HILBERT_N 4
#define LAPLACE_HILBERT_P 16

static uint16_t clamp_to_u16(double v_unit_signed)
{
    /* v in [-1, +1] → q in [0, 65535] */
    double v = v_unit_signed;
    if (v < -1.0) { v = -1.0; }
    if (v >  1.0) { v =  1.0; }
    const double q = (v + 1.0) * 0.5 * 65535.0;
    return (uint16_t) (q + 0.5);
}

static double from_u16(uint16_t q)
{
    return ((double) q / 65535.0) * 2.0 - 1.0;
}

/* Skilling's "AxestoTranspose" — encode integer coords to Hilbert transpose. */
static void axes_to_transpose(uint16_t coord[LAPLACE_HILBERT_N])
{
    uint16_t M = (uint16_t) 1u << (LAPLACE_HILBERT_P - 1);

    /* Inverse undo */
    for (uint16_t Q = M; Q > 1; Q >>= 1) {
        const uint16_t P = (uint16_t)(Q - 1);
        for (int i = 0; i < LAPLACE_HILBERT_N; ++i) {
            if (coord[i] & Q) {
                coord[0] ^= P;
            } else {
                const uint16_t t = (uint16_t)((coord[0] ^ coord[i]) & P);
                coord[0] ^= t;
                coord[i] ^= t;
            }
        }
    }
    /* Gray encode */
    for (int i = 1; i < LAPLACE_HILBERT_N; ++i) {
        coord[i] ^= coord[i - 1];
    }
    uint16_t t = 0;
    for (uint16_t Q = M; Q > 1; Q >>= 1) {
        if (coord[LAPLACE_HILBERT_N - 1] & Q) {
            t ^= (uint16_t)(Q - 1);
        }
    }
    for (int i = 0; i < LAPLACE_HILBERT_N; ++i) {
        coord[i] ^= t;
    }
}

/* Skilling's "TransposetoAxes" — decode Hilbert transpose to integer coords.
 * M and Q are uint32_t because M = 2^P = 65536 for P=16 does not fit in
 * uint16_t. The loop body still operates on uint16_t coord values; Q < M
 * when the body executes so its low 16 bits are well-defined. */
static void transpose_to_axes(uint16_t coord[LAPLACE_HILBERT_N])
{
    const uint32_t M = 1u << LAPLACE_HILBERT_P;
    uint16_t       t = (uint16_t)(coord[LAPLACE_HILBERT_N - 1] >> 1);
    for (int i = LAPLACE_HILBERT_N - 1; i > 0; --i) {
        coord[i] ^= coord[i - 1];
    }
    coord[0] ^= t;
    for (uint32_t Q = 2; Q != M; Q <<= 1) {
        const uint16_t P = (uint16_t)(Q - 1);
        for (int i = LAPLACE_HILBERT_N - 1; i >= 0; --i) {
            if (coord[i] & Q) {
                coord[0] ^= P;
            } else {
                const uint16_t tt = (uint16_t)((coord[0] ^ coord[i]) & P);
                coord[0] ^= tt;
                coord[i] ^= tt;
            }
        }
    }
}

/* Pack the transpose representation into a single 64-bit interleaved index.
 * Bit p*n-1 of the output = bit p-1 of coord[0], bit p*n-2 of output =
 * bit p-1 of coord[1], …, bit p*n-n of output = bit p-1 of coord[n-1], then
 * bit p-2 of coord[0], etc.
 */
static uint64_t pack_transpose(const uint16_t coord[LAPLACE_HILBERT_N])
{
    uint64_t h = 0;
    for (int b = LAPLACE_HILBERT_P - 1; b >= 0; --b) {
        for (int i = 0; i < LAPLACE_HILBERT_N; ++i) {
            h = (h << 1) | (uint64_t)((coord[i] >> b) & 1u);
        }
    }
    return h;
}

static void unpack_transpose(uint64_t h, uint16_t coord[LAPLACE_HILBERT_N])
{
    for (int i = 0; i < LAPLACE_HILBERT_N; ++i) {
        coord[i] = 0;
    }
    for (int b = LAPLACE_HILBERT_P - 1; b >= 0; --b) {
        for (int i = 0; i < LAPLACE_HILBERT_N; ++i) {
            const uint64_t bit = (h >> ((b * LAPLACE_HILBERT_N) + (LAPLACE_HILBERT_N - 1 - i))) & 1u;
            coord[i] = (uint16_t)(coord[i] | (bit << b));
        }
    }
}

uint64_t laplace_hilbert_xyzw_to_index(uint16_t x, uint16_t y, uint16_t z, uint16_t w)
{
    uint16_t coord[LAPLACE_HILBERT_N] = {x, y, z, w};
    axes_to_transpose(coord);
    return pack_transpose(coord);
}

void laplace_hilbert_index_to_xyzw(uint64_t h,
                                   uint16_t *x, uint16_t *y,
                                   uint16_t *z, uint16_t *w)
{
    uint16_t coord[LAPLACE_HILBERT_N];
    unpack_transpose(h, coord);
    transpose_to_axes(coord);
    *x = coord[0]; *y = coord[1]; *z = coord[2]; *w = coord[3];
}

uint64_t laplace_hilbert_point4d_to_index(const laplace_point4d_t *p)
{
    return laplace_hilbert_xyzw_to_index(
        clamp_to_u16(p->x), clamp_to_u16(p->y),
        clamp_to_u16(p->z), clamp_to_u16(p->w));
}

void laplace_hilbert_index_to_point4d(uint64_t h, laplace_point4d_t *out)
{
    uint16_t x, y, z, w;
    laplace_hilbert_index_to_xyzw(h, &x, &y, &z, &w);
    out->x = from_u16(x);
    out->y = from_u16(y);
    out->z = from_u16(z);
    out->w = from_u16(w);
}
