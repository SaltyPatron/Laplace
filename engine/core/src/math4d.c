#include "laplace/core/math4d.h"
#include "laplace/laplace.h"

#include <math.h>
#include <stdint.h>
#include <stdlib.h>

double math4d_dot(const double a[4], const double b[4]) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
}

double math4d_norm(const double v[4]) {
    return sqrt(math4d_dot(v, v));
}

double math4d_radius_from_origin(const double v[4]) {
    return math4d_norm(v);
}

double math4d_distance_sq(const double a[4], const double b[4]) {
    const double dx = a[0] - b[0];
    const double dy = a[1] - b[1];
    const double dz = a[2] - b[2];
    const double dw = a[3] - b[3];
    return ((dx * dx + dy * dy) + dz * dz) + dw * dw;
}

double math4d_distance(const double a[4], const double b[4]) {
    return lp_distance4(a, b);
}

double math4d_angular_distance(const double a[4], const double b[4]) {
    const double na = math4d_norm(a);
    const double nb = math4d_norm(b);
    if (na == 0.0 || nb == 0.0) return 0.0;
    return lp_angle4(a, b);
}

void math4d_add(const double a[4], const double b[4], double out[4]) {
    out[0] = a[0] + b[0];
    out[1] = a[1] + b[1];
    out[2] = a[2] + b[2];
    out[3] = a[3] + b[3];
}

void math4d_sub(const double a[4], const double b[4], double out[4]) {
    out[0] = a[0] - b[0];
    out[1] = a[1] - b[1];
    out[2] = a[2] - b[2];
    out[3] = a[3] - b[3];
}

void math4d_scale(const double a[4], double s, double out[4]) {
    out[0] = a[0] * s;
    out[1] = a[1] * s;
    out[2] = a[2] * s;
    out[3] = a[3] * s;
}

/* ---- canonical constituent order -----------------------------------------
 *
 * Floating-point addition is commutative but not associative, so a sum over
 * constituents depends on arrival order. A placement derived from constituents
 * must be reproducible under any reordering of the same constituents, so every
 * accumulation visits them in one canonical order.
 *
 * Ordering is numeric, component by component, never a memcmp over raw bytes:
 * memcmp walks bytes in address order, so little- and big-endian hosts would sort
 * differently and derive different placements from identical inputs. IEEE-754
 * comparison is the same on both, so placement is architecture-stable.
 *
 * Identical entries compare equal and are interchangeable in the sum, so an
 * unstable sort is safe. -0.0 and +0.0 compare equal and sum identically.
 */
typedef struct {
    const double* p;
    double        w;
} laplace_pt_t;

/* Total order over doubles independent of byte layout. NaN sorts last and
 * compares equal to itself, so the comparator stays a valid strict weak
 * ordering -- qsort is undefined behaviour otherwise. */
static int laplace_dbl_cmp(double a, double b) {
    if (a < b)  return -1;
    if (a > b)  return  1;
    if (a == b) return  0;              /* includes -0.0 vs +0.0 */
    const int an = isnan(a), bn = isnan(b);
    if (an && bn) return 0;
    return an ? 1 : -1;
}

static int laplace_pt_cmp(const void* a, const void* b) {
    const laplace_pt_t* x = (const laplace_pt_t*) a;
    const laplace_pt_t* y = (const laplace_pt_t*) b;
    for (int i = 0; i < 4; ++i) {
        const int c = laplace_dbl_cmp(x->p[i], y->p[i]);
        if (c != 0) return c;
    }
    return laplace_dbl_cmp(x->w, y->w);
}

static void laplace_pts_sift(laplace_pt_t* points, size_t root, size_t count) {
    while (root < count / 2u) {
        size_t child = root * 2u + 1u;
        if (child + 1u < count && laplace_pt_cmp(&points[child], &points[child + 1u]) < 0)
            ++child;
        if (laplace_pt_cmp(&points[root], &points[child]) >= 0) return;
        const laplace_pt_t swap = points[root];
        points[root] = points[child];
        points[child] = swap;
        root = child;
    }
}

/* In-place heapsort has a fixed stack footprint and never asks libc for a
 * hidden merge/sort workspace. Both centroid surfaces use this same ordering. */
static void laplace_pts_sort(laplace_pt_t* points, size_t count) {
    for (size_t root = count / 2u; root > 0u; --root)
        laplace_pts_sift(points, root - 1u, count);
    for (size_t end = count; end > 1u; --end) {
        const laplace_pt_t swap = points[0];
        points[0] = points[end - 1u];
        points[end - 1u] = swap;
        laplace_pts_sift(points, 0u, end - 1u);
    }
}

static void laplace_pts_fill(const double* points, size_t count, const double* weights,
                            laplace_pt_t* ordered) {
    for (size_t i = 0; i < count; ++i) {
        ordered[i].p = points + i * 4u;
        ordered[i].w = weights != NULL ? weights[i] : 1.0;
    }
    laplace_pts_sort(ordered, count);
}

static void laplace_centroid_reduce(const laplace_pt_t* points, size_t count, double out[4]) {
    double sum[4] = {0.0, 0.0, 0.0, 0.0};
    for (size_t i = 0; i < count; ++i)
        for (size_t axis = 0; axis < 4u; ++axis) sum[axis] += points[i].p[axis];
    const double inv = count == 0u ? 0.0 : 1.0 / (double)count;
    for (size_t axis = 0; axis < 4u; ++axis) out[axis] = sum[axis] * inv;
}

#define LAPLACE_PT_STACK 64

/* Sorts the constituents into canonical order. Returns stackbuf for small n,
 * a malloc'd block for large n, or NULL on allocation failure. On NULL the
 * centroid uses its allocation-free canonical scan; weighted callers apply their
 * own failure policy. Free with laplace_pts_release. */
static laplace_pt_t* laplace_pts_canonical(const double* points, size_t n_points,
                                           const double* weights,
                                           laplace_pt_t* stackbuf) {
    laplace_pt_t* pts = stackbuf;
    if (n_points > LAPLACE_PT_STACK) {
        pts = (laplace_pt_t*) malloc(sizeof(laplace_pt_t) * n_points);
        if (pts == NULL) return NULL;
    }
    laplace_pts_fill(points, n_points, weights, pts);
    return pts;
}

static void laplace_pts_release(laplace_pt_t* pts, laplace_pt_t* stackbuf) {
    if (pts != NULL && pts != stackbuf) free(pts);
}

int math4d_centroid_workspace_size(size_t n_points, size_t* out_bytes) {
    const size_t padding = _Alignof(laplace_pt_t) - 1u;
    if (out_bytes == NULL) return -1;
    *out_bytes = 0u;
    if (n_points > SIZE_MAX / (4u * sizeof(double)) ||
        n_points > (SIZE_MAX - padding) / sizeof(laplace_pt_t)) return -1;
    if (n_points != 0u) *out_bytes = n_points * sizeof(laplace_pt_t) + padding;
    return 0;
}

int math4d_centroid_with_workspace(const double* points, size_t n_points,
    void* workspace, size_t workspace_bytes, double out[4]) {
    size_t required;
    if (out == NULL || math4d_centroid_workspace_size(n_points, &required) != 0 ||
        workspace_bytes < required || (n_points != 0u && (points == NULL || workspace == NULL)))
        return -1;
    laplace_pt_t* ordered = NULL;
    if (n_points != 0u) {
        const size_t alignment = _Alignof(laplace_pt_t);
        const uintptr_t address = (uintptr_t)workspace;
        const size_t padding = (alignment - address % alignment) % alignment;
        if (address > UINTPTR_MAX - padding) return -1;
        ordered = (laplace_pt_t*)((uint8_t*)workspace + padding);
        laplace_pts_fill(points, n_points, NULL, ordered);
    }
    laplace_centroid_reduce(ordered, n_points, out);
    return 0;
}

/* The void centroid surface cannot return an allocation error. On OOM it walks
 * equal-key groups in canonical order without allocating; it never substitutes
 * input-order arithmetic. Bounded callers use the status-returning workspace
 * surface above and do not enter this slower path. */
static void laplace_centroid_without_allocation(const double* points, size_t count, double out[4]) {
    const double* previous = NULL;
    double sum[4] = {0.0, 0.0, 0.0, 0.0};
    size_t reduced = 0u;
    while (reduced < count) {
        const double* next = NULL;
        size_t equal_count = 0u;
        for (size_t i = 0; i < count; ++i) {
            const laplace_pt_t candidate = {points + i * 4u, 1.0};
            if (previous != NULL) {
                const laplace_pt_t last = {previous, 1.0};
                if (laplace_pt_cmp(&candidate, &last) <= 0) continue;
            }
            if (next == NULL) {
                next = candidate.p;
                equal_count = 1u;
            } else {
                const laplace_pt_t selected = {next, 1.0};
                const int order = laplace_pt_cmp(&candidate, &selected);
                if (order < 0) { next = candidate.p; equal_count = 1u; }
                else if (order == 0) ++equal_count;
            }
        }
        for (size_t i = 0; i < equal_count; ++i)
            for (size_t axis = 0; axis < 4u; ++axis) sum[axis] += next[axis];
        reduced += equal_count;
        previous = next;
    }
    const double inv = count == 0u ? 0.0 : 1.0 / (double)count;
    for (size_t axis = 0; axis < 4u; ++axis) out[axis] = sum[axis] * inv;
}

void math4d_centroid(const double* points, size_t n_points, double out[4]) {
    laplace_pt_t stackbuf[LAPLACE_PT_STACK];
    laplace_pt_t* pts = laplace_pts_canonical(points, n_points, NULL, stackbuf);
    if (pts != NULL) laplace_centroid_reduce(pts, n_points, out);
    else laplace_centroid_without_allocation(points, n_points, out);
    laplace_pts_release(pts, stackbuf);
}

static double normalize4d(double v[4]) {
    const double n = math4d_norm(v);
    if (n == 0.0) return 0.0;
    const double inv = 1.0 / n;
    v[0] *= inv;
    v[1] *= inv;
    v[2] *= inv;
    v[3] *= inv;
    return n;
}

void math4d_log_s3(const double base[4], const double p[4], double out_tangent[4]) {
    double pn[4] = {p[0], p[1], p[2], p[3]};
    if (normalize4d(pn) == 0.0) {
        out_tangent[0] = out_tangent[1] = out_tangent[2] = out_tangent[3] = 0.0;
        return;
    }
    lp_s3_log(base, pn, out_tangent);
}

void math4d_exp_s3(const double base[4], const double tangent[4], double out[4]) {
    lp_s3_exp(base, tangent, out);
}

/* Weighted Karcher mean, accumulated in the canonical constituent order defined
 * above math4d_centroid (laplace_pts_canonical), so both means follow one
 * ordering rule. Identical (point, weight) pairs are interchangeable in the sum. */

void math4d_karcher_mean(const double* points, size_t n_points,
                         const double* weights, double tol, int max_iters,
                         double out[4]) {
    out[0] = out[1] = out[2] = out[3] = 0.0;
    if (n_points == 0) return;

    if (n_points == 1) {
        out[0] = points[0];
        out[1] = points[1];
        out[2] = points[2];
        out[3] = points[3];
        normalize4d(out);
        return;
    }

    /* Establish the canonical visitation order once; every accumulation below
     * uses it -- the weight total, the seed estimate, and the tangent mean
     * recomputed on each iteration. On allocation failure the constituents are
     * visited in input order: the mean is still computed, but is not
     * reproducible under reordering. */
    laplace_pt_t  stackbuf[LAPLACE_PT_STACK];
    laplace_pt_t* pts = laplace_pts_canonical(points, n_points, weights, stackbuf);
#define LK_P(i) (pts != NULL ? pts[i].p : points + (i) * 4)
#define LK_W(i) (pts != NULL ? pts[i].w : ((weights != NULL) ? weights[i] : 1.0))

    double w_total = 0.0;
    for (size_t i = 0; i < n_points; ++i) w_total += LK_W(i);

    if (w_total == 0.0) {
        const double* p0 = LK_P(0);
        out[0] = p0[0]; out[1] = p0[1]; out[2] = p0[2]; out[3] = p0[3];
        normalize4d(out);
        laplace_pts_release(pts, stackbuf);
        return;
    }

    double est[4] = {0.0, 0.0, 0.0, 0.0};
    for (size_t i = 0; i < n_points; ++i) {
        const double  w = LK_W(i);
        const double* p = LK_P(i);
        est[0] += w * p[0];
        est[1] += w * p[1];
        est[2] += w * p[2];
        est[3] += w * p[3];
    }
    if (normalize4d(est) == 0.0) {
        const double* p0 = LK_P(0);
        est[0] = p0[0]; est[1] = p0[1]; est[2] = p0[2]; est[3] = p0[3];
        normalize4d(est);
    }

    const double inv_w = 1.0 / w_total;
    for (int iter = 0; iter < max_iters; ++iter) {
        double mean_t[4] = {0.0, 0.0, 0.0, 0.0};
        for (size_t i = 0; i < n_points; ++i) {
            const double w = LK_W(i);
            double tng[4];
            math4d_log_s3(est, LK_P(i), tng);
            mean_t[0] += w * tng[0];
            mean_t[1] += w * tng[1];
            mean_t[2] += w * tng[2];
            mean_t[3] += w * tng[3];
        }
        mean_t[0] *= inv_w;
        mean_t[1] *= inv_w;
        mean_t[2] *= inv_w;
        mean_t[3] *= inv_w;

        const double step = math4d_norm(mean_t);
        math4d_exp_s3(est, mean_t, est);

        if (step < tol) break;
    }

#undef LK_P
#undef LK_W
    laplace_pts_release(pts, stackbuf);

    out[0] = est[0];
    out[1] = est[1];
    out[2] = est[2];
    out[3] = est[3];
}

double math4d_frechet(const double* p, size_t np, const double* q, size_t nq) {
    if (np == 0 || nq == 0) return NAN;
    return lp_frechet4(p, np, q, nq);
}

double math4d_hausdorff(const double* a, size_t na, const double* b, size_t nb) {
    if (na == 0 || nb == 0) return NAN;
    return lp_hausdorff4(a, na, b, nb);
}

void math4d_angular_distance_batch(const double *a_flat, int n, const double b[4],
                                   double *out) {
    if (n <= 0) return;
    if (math4d_norm(b) == 0.0) {
        for (int i = 0; i < n; i++) out[i] = 0.0;
        return;
    }
    lp_angle4_batch(b, a_flat, (size_t)n, out);
    for (int i = 0; i < n; i++) {
        if (isnan(out[i])) out[i] = 0.0;
    }
}
