#include "laplace/core/math4d.h"

#include <math.h>
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
    return dx * dx + dy * dy + dz * dz + dw * dw;
}

double math4d_distance(const double a[4], const double b[4]) {
    return sqrt(math4d_distance_sq(a, b));
}

double math4d_angular_distance(const double a[4], const double b[4]) {
    /* Geodesic distance on S³: acos(a·b) when |a|=|b|=1. We normalize defensively
     * so callers can pass non-unit vectors without surprise; clamp the cosine into
     * [-1, 1] because FP roundoff can push it just outside the domain of acos. */
    const double na = math4d_norm(a);
    const double nb = math4d_norm(b);
    if (na == 0.0 || nb == 0.0) return 0.0;
    double c = math4d_dot(a, b) / (na * nb);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    return acos(c);
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

void math4d_centroid(const double* points, size_t n_points, double out[4]) {
    out[0] = 0.0;
    out[1] = 0.0;
    out[2] = 0.0;
    out[3] = 0.0;
    if (n_points == 0) return;
    /* Sequential sum in input order — deterministic across thread counts because
     * single-threaded by construction (TBB schedules above this kernel per ADR 0030). */
    for (size_t i = 0; i < n_points; ++i) {
        const double* p = points + i * 4;
        out[0] += p[0];
        out[1] += p[1];
        out[2] += p[2];
        out[3] += p[3];
    }
    const double inv = 1.0 / (double)n_points;
    out[0] *= inv;
    out[1] *= inv;
    out[2] *= inv;
    out[3] *= inv;
}

/* Normalize a 4-vector in place onto the unit sphere. Returns the original
 * norm so callers can detect a (near-)zero vector. Deterministic scalar math. */
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
    /* Defensive: treat p as a direction on the sphere. base is assumed unit
     * (the Karcher iterate is always re-normalized), but we clamp the cosine
     * for FP safety exactly like math4d_angular_distance. */
    double pn[4] = {p[0], p[1], p[2], p[3]};
    if (normalize4d(pn) == 0.0) {
        out_tangent[0] = out_tangent[1] = out_tangent[2] = out_tangent[3] = 0.0;
        return;
    }
    double c = math4d_dot(base, pn);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    const double theta = acos(c);

    /* Tangent component: p projected off base, then rescaled to length θ.
     * v = p − c·base; |v| = sin θ. v̂ = v/|v|; Log = θ·v̂. */
    double v[4];
    v[0] = pn[0] - c * base[0];
    v[1] = pn[1] - c * base[1];
    v[2] = pn[2] - c * base[2];
    v[3] = pn[3] - c * base[3];
    const double vn = math4d_norm(v);
    if (vn == 0.0) {
        /* p ≈ base (θ≈0) → zero tangent; OR p ≈ −base (antipodal, θ≈π) →
         * geodesic direction undefined at the cut locus → documented fallback:
         * zero tangent (the antipode exerts no directional pull). */
        out_tangent[0] = out_tangent[1] = out_tangent[2] = out_tangent[3] = 0.0;
        return;
    }
    const double s = theta / vn;
    out_tangent[0] = v[0] * s;
    out_tangent[1] = v[1] * s;
    out_tangent[2] = v[2] * s;
    out_tangent[3] = v[3] * s;
}

void math4d_exp_s3(const double base[4], const double tangent[4], double out[4]) {
    const double t = math4d_norm(tangent);
    if (t == 0.0) {
        out[0] = base[0];
        out[1] = base[1];
        out[2] = base[2];
        out[3] = base[3];
        return;
    }
    const double ct = cos(t);
    const double st = sin(t) / t;   /* sin(t)·(tangent/t) = (sin(t)/t)·tangent */
    out[0] = ct * base[0] + st * tangent[0];
    out[1] = ct * base[1] + st * tangent[1];
    out[2] = ct * base[2] + st * tangent[2];
    out[3] = ct * base[3] + st * tangent[3];
    /* Re-normalize defensively against FP drift so the result stays on S³. */
    normalize4d(out);
}

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

    /* Total weight (sequential, input order → deterministic). With NULL
     * weights every point counts 1. */
    double w_total = 0.0;
    for (size_t i = 0; i < n_points; ++i) {
        const double w = (weights != NULL) ? weights[i] : 1.0;
        w_total += w;
    }
    if (w_total == 0.0) {
        /* All weights zero — no information. Fall back to first point. */
        out[0] = points[0];
        out[1] = points[1];
        out[2] = points[2];
        out[3] = points[3];
        normalize4d(out);
        return;
    }

    /* Initial estimate: weighted Euclidean mean, normalized to the sphere.
     * Sequential accumulation in input order matches math4d_centroid's
     * CBWR-aligned determinism (single-threaded; no non-associative parallel
     * reduction). */
    double est[4] = {0.0, 0.0, 0.0, 0.0};
    for (size_t i = 0; i < n_points; ++i) {
        const double w = (weights != NULL) ? weights[i] : 1.0;
        const double* p = points + i * 4;
        est[0] += w * p[0];
        est[1] += w * p[1];
        est[2] += w * p[2];
        est[3] += w * p[3];
    }
    if (normalize4d(est) == 0.0) {
        /* Antipodally balanced cluster → Euclidean mean is (near-)zero, so the
         * direction is undefined. Documented degeneracy: the Karcher mean is
         * genuinely non-unique here; seed deterministically from the first
         * point (normalized) and let the iteration settle. */
        est[0] = points[0];
        est[1] = points[1];
        est[2] = points[2];
        est[3] = points[3];
        normalize4d(est);
    }

    const double inv_w = 1.0 / w_total;
    for (int iter = 0; iter < max_iters; ++iter) {
        /* Weighted average of the Log-mapped tangents at the current estimate.
         * Sequential, input order. */
        double mean_t[4] = {0.0, 0.0, 0.0, 0.0};
        for (size_t i = 0; i < n_points; ++i) {
            const double w = (weights != NULL) ? weights[i] : 1.0;
            double tng[4];
            math4d_log_s3(est, points + i * 4, tng);
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
        math4d_exp_s3(est, mean_t, est);  /* walk the geodesic to the new estimate */

        if (step < tol) break;  /* converged: tangent update below tolerance */
    }

    out[0] = est[0];
    out[1] = est[1];
    out[2] = est[2];
    out[3] = est[3];
}

/* Discrete Fréchet distance via the Eiter-Mannila 1994 DP recurrence:
 *   ca[i,j] = max( min(ca[i-1,j], ca[i-1,j-1], ca[i,j-1]),  d(P[i], Q[j]) )
 *   ca[0,0] = d(P[0], Q[0])
 * Row i depends only on row i and row i-1, so two rolling rows suffice
 * (O(min(np,nq)) memory). We allocate the smaller dimension as the inner
 * (column) axis so the rolling buffer stays small even for ragged shapes.
 *
 * Deterministic: sequential scans in fixed order; no parallelism; no FP
 * non-associative reductions. Same input always same output bit-for-bit
 * across thread counts (per RULES.md R7 + ADR 0030 CBWR alignment). */
double math4d_frechet(const double* p, size_t np, const double* q, size_t nq) {
    if (np == 0 || nq == 0) return NAN;

    /* Swap so columns are the shorter axis — rolling buffer is nq doubles. */
    const double* rows = p;
    size_t        n_rows = np;
    const double* cols = q;
    size_t        n_cols = nq;
    if (n_cols > n_rows) {
        rows = q;   n_rows = nq;
        cols = p;   n_cols = np;
    }

    double* prev = (double*) malloc(sizeof(double) * n_cols);
    double* curr = (double*) malloc(sizeof(double) * n_cols);
    if (prev == NULL || curr == NULL) {
        free(prev);
        free(curr);
        return NAN;
    }

    /* Seed row 0: ca[0,j] = max(d(rows[0], cols[j]), ca[0, j-1]).
     * (Free leashes within the i=0 prefix must monotonically pay the worst
     *  cost seen so far on Q.) */
    prev[0] = math4d_distance(rows + 0, cols + 0);
    for (size_t j = 1; j < n_cols; ++j) {
        const double d = math4d_distance(rows + 0, cols + j * 4);
        prev[j] = (d > prev[j - 1]) ? d : prev[j - 1];
    }

    for (size_t i = 1; i < n_rows; ++i) {
        /* ca[i,0] = max(d(rows[i], cols[0]), ca[i-1, 0]). */
        const double d0 = math4d_distance(rows + i * 4, cols + 0);
        curr[0] = (d0 > prev[0]) ? d0 : prev[0];

        for (size_t j = 1; j < n_cols; ++j) {
            const double d = math4d_distance(rows + i * 4, cols + j * 4);
            /* min of the three predecessor cells: (i-1,j), (i-1,j-1), (i,j-1). */
            double m = prev[j];                /* ca[i-1, j] */
            if (prev[j - 1] < m) m = prev[j - 1]; /* ca[i-1, j-1] */
            if (curr[j - 1] < m) m = curr[j - 1]; /* ca[i, j-1] */
            curr[j] = (d > m) ? d : m;
        }

        /* Swap prev <-> curr without copying (pointer roll). */
        double* tmp = prev;
        prev = curr;
        curr = tmp;
    }

    const double result = prev[n_cols - 1];
    free(prev);
    free(curr);
    return result;
}

/* Symmetric Hausdorff distance: max(directed(a→b), directed(b→a)).
 * directed(a→b) = max over a∈A of (min over b∈B of d(a,b)).
 * O(na*nb) time, O(1) extra space. Deterministic per the same argument
 * as math4d_frechet — fixed scan order, no parallel reductions. */
static double directed_hausdorff(const double* a, size_t na, const double* b, size_t nb) {
    /* For each point in A, find min distance to B; track the max over A. */
    double cmax = 0.0;
    for (size_t i = 0; i < na; ++i) {
        double cmin = math4d_distance(a + i * 4, b + 0);
        for (size_t j = 1; j < nb; ++j) {
            const double d = math4d_distance(a + i * 4, b + j * 4);
            if (d < cmin) cmin = d;
        }
        if (cmin > cmax) cmax = cmin;
    }
    return cmax;
}

double math4d_hausdorff(const double* a, size_t na, const double* b, size_t nb) {
    if (na == 0 || nb == 0) return NAN;
    const double dab = directed_hausdorff(a, na, b, nb);
    const double dba = directed_hausdorff(b, nb, a, na);
    return (dab > dba) ? dab : dba;
}
