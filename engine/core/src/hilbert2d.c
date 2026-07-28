#include "laplace/core/hilbert2d.h"

#define HILBERT2D_MAX_ORDER 31u

/*
 * Quadrant rotation — the step that makes the curve continuous across quadrant
 * boundaries. Reflect/transpose the subsquare so the next level enters where the
 * previous one left off.
 */
static void hilbert2d_rot(uint64_t n, uint32_t* x, uint32_t* y, uint32_t rx, uint32_t ry) {
    if (ry == 0u) {
        if (rx == 1u) {
            *x = (uint32_t)(n - 1ull) - *x;
            *y = (uint32_t)(n - 1ull) - *y;
        }
        uint32_t t = *x;
        *x = *y;
        *y = t;
    }
}

int hilbert2d_encode(uint32_t order, uint32_t x, uint32_t y, uint64_t* out_d) {
    uint64_t n, d = 0ull;
    if (out_d == NULL || order == 0u || order > HILBERT2D_MAX_ORDER) return -1;
    n = hilbert2d_side(order);
    if ((uint64_t)x >= n || (uint64_t)y >= n) return -1;

    for (uint64_t s = n / 2ull; s > 0ull; s /= 2ull) {
        uint32_t rx = ((uint64_t)x & s) > 0ull ? 1u : 0u;
        uint32_t ry = ((uint64_t)y & s) > 0ull ? 1u : 0u;
        d += s * s * (uint64_t)((3u * rx) ^ ry);
        hilbert2d_rot(n, &x, &y, rx, ry);
    }
    *out_d = d;
    return 0;
}

int hilbert2d_decode(uint32_t order, uint64_t d, uint32_t* out_x, uint32_t* out_y) {
    uint64_t n, t = d;
    uint32_t x = 0u, y = 0u;
    if (out_x == NULL || out_y == NULL || order == 0u || order > HILBERT2D_MAX_ORDER) return -1;
    n = hilbert2d_side(order);
    if (d >= hilbert2d_cells(order)) return -1;

    for (uint64_t s = 1ull; s < n; s *= 2ull) {
        uint32_t rx = (uint32_t)(1ull & (t / 2ull));
        uint32_t ry = (uint32_t)(1ull & (t ^ (uint64_t)rx));
        hilbert2d_rot(s, &x, &y, rx, ry);
        x += (uint32_t)(s * (uint64_t)rx);
        y += (uint32_t)(s * (uint64_t)ry);
        t /= 4ull;
    }
    *out_x = x;
    *out_y = y;
    return 0;
}
