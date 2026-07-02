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
    double c = math4d_dot(base, pn);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    const double theta = acos(c);

    double v[4];
    v[0] = pn[0] - c * base[0];
    v[1] = pn[1] - c * base[1];
    v[2] = pn[2] - c * base[2];
    v[3] = pn[3] - c * base[3];
    const double vn = math4d_norm(v);
    if (vn == 0.0) {
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
    const double st = sin(t) / t;
    out[0] = ct * base[0] + st * tangent[0];
    out[1] = ct * base[1] + st * tangent[1];
    out[2] = ct * base[2] + st * tangent[2];
    out[3] = ct * base[3] + st * tangent[3];
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

    double w_total = 0.0;
    for (size_t i = 0; i < n_points; ++i) {
        const double w = (weights != NULL) ? weights[i] : 1.0;
        w_total += w;
    }
    if (w_total == 0.0) {
        out[0] = points[0];
        out[1] = points[1];
        out[2] = points[2];
        out[3] = points[3];
        normalize4d(out);
        return;
    }

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
        est[0] = points[0];
        est[1] = points[1];
        est[2] = points[2];
        est[3] = points[3];
        normalize4d(est);
    }

    const double inv_w = 1.0 / w_total;
    for (int iter = 0; iter < max_iters; ++iter) {
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
        math4d_exp_s3(est, mean_t, est);

        if (step < tol) break;
    }

    out[0] = est[0];
    out[1] = est[1];
    out[2] = est[2];
    out[3] = est[3];
}

double math4d_frechet(const double* p, size_t np, const double* q, size_t nq) {
    if (np == 0 || nq == 0) return NAN;

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

    prev[0] = math4d_distance(rows + 0, cols + 0);
    for (size_t j = 1; j < n_cols; ++j) {
        const double d = math4d_distance(rows + 0, cols + j * 4);
        prev[j] = (d > prev[j - 1]) ? d : prev[j - 1];
    }

    for (size_t i = 1; i < n_rows; ++i) {
        const double d0 = math4d_distance(rows + i * 4, cols + 0);
        curr[0] = (d0 > prev[0]) ? d0 : prev[0];

        for (size_t j = 1; j < n_cols; ++j) {
            const double d = math4d_distance(rows + i * 4, cols + j * 4);
            double m = prev[j];
            if (prev[j - 1] < m) m = prev[j - 1];
            if (curr[j - 1] < m) m = curr[j - 1];
            curr[j] = (d > m) ? d : m;
        }

        double* tmp = prev;
        prev = curr;
        curr = tmp;
    }

    const double result = prev[n_cols - 1];
    free(prev);
    free(curr);
    return result;
}

static double directed_hausdorff(const double* a, size_t na, const double* b, size_t nb) {
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

/*
 * math4d_angular_distance_batch: see math4d.h for the contract. Runtime
 * CPUID-gated AVX2 dispatch, resolved once via a cached function pointer;
 * scalar fallback is always compiled and always correct, AVX2 only changes
 * performance.
 */
#if defined(_M_X64) || defined(__x86_64__) || defined(_M_AMD64)
#define LAPLACE_MATH4D_X86 1
#endif

#ifdef LAPLACE_MATH4D_X86
#include <immintrin.h>
/* <intrin.h>/__cpuidex exist only on Windows toolchains; Linux icx defines
 * __INTEL_LLVM_COMPILER too but has no MSVC intrin.h, so gate on _WIN32. */
#if defined(_WIN32)
#include <intrin.h>
static int math4d_cpu_has_avx2(void) {
    int info[4] = {0, 0, 0, 0};
    __cpuidex(info, 7, 0);
    return (info[1] & (1 << 5)) != 0; /* EBX bit 5 = AVX2 */
}
#elif defined(__GNUC__) || defined(__clang__)
#include <cpuid.h>
static int math4d_cpu_has_avx2(void) {
    unsigned int eax, ebx, ecx, edx;
    if (!__get_cpuid_count(7, 0, &eax, &ebx, &ecx, &edx)) return 0;
    return (ebx & (1 << 5)) != 0;
}
#else
static int math4d_cpu_has_avx2(void) { return 0; }
#endif

static double math4d_angular_one_avx2(const double a[4], const double b[4], double nb) {
    __m256d va = _mm256_loadu_pd(a);
    __m256d vb = _mm256_loadu_pd(b);
    __m256d dot_v = _mm256_mul_pd(va, vb);
    __m256d aa_v  = _mm256_mul_pd(va, va);
    __m256d sum2  = _mm256_hadd_pd(dot_v, aa_v); /* [d0+d1, q0+q1, d2+d3, q2+q3] */
    __m128d lo = _mm256_castpd256_pd128(sum2);
    __m128d hi = _mm256_extractf128_pd(sum2, 1);
    __m128d total = _mm_add_pd(lo, hi);          /* [dot_sum, aa_sum] */
    double dot = _mm_cvtsd_f64(total);
    double aa  = _mm_cvtsd_f64(_mm_unpackhi_pd(total, total));
    double na  = sqrt(aa);

    if (na == 0.0 || nb == 0.0) return 0.0;
    double c = dot / (na * nb);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    return acos(c);
}
#endif /* LAPLACE_MATH4D_X86 */

static double math4d_angular_one_scalar(const double a[4], const double b[4], double nb) {
    const double na = math4d_norm(a);
    if (na == 0.0 || nb == 0.0) return 0.0;
    double c = math4d_dot(a, b) / (na * nb);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    return acos(c);
}

typedef double (*math4d_angular_one_fn)(const double a[4], const double b[4], double nb);
static math4d_angular_one_fn g_math4d_angular_one = NULL;

static void math4d_ensure_angular_dispatch(void) {
    if (g_math4d_angular_one != NULL) return;
#ifdef LAPLACE_MATH4D_X86
    g_math4d_angular_one = math4d_cpu_has_avx2() ? math4d_angular_one_avx2
                                                  : math4d_angular_one_scalar;
#else
    g_math4d_angular_one = math4d_angular_one_scalar;
#endif
}

void math4d_angular_distance_batch(const double *a_flat, int n, const double b[4],
                                   double *out) {
    const double nb = math4d_norm(b);

    math4d_ensure_angular_dispatch();
    for (int i = 0; i < n; i++)
        out[i] = g_math4d_angular_one(a_flat + (size_t) i * 4, b, nb);
}
