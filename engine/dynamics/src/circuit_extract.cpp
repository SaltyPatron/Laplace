#include "laplace/dynamics/circuit_extract.h"

#include <cmath>
#include <cstddef>
#include <vector>

#include <utility>

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/blocked_range.h>
#  include <oneapi/tbb/enumerable_thread_specific.h>
#endif

/* The address-book read of a model's weights, in the engine (not managed scalar). Both
 * kernels are TBB-parallel when MKL/TBB are present, with a serial fallback otherwise; both
 * are deterministic (fixed output order, no thread-dependent reduction). */

extern "C"
double detect_energy_floor(const float* W, std::size_t n, double target_energy) {
    if (!W || n == 0 || !(target_energy > 0.0) || target_energy > 1.0) return -1.0;
    if (target_energy >= 1.0) return 0.0;   // keep all

    /* Pass 1: max |w| and total energy Σw² (TBB reduce). */
    double total_e = 0.0; float wmax = 0.0f;
    auto reduce = [&](std::size_t i0, std::size_t i1, double e, float mx) {
        for (std::size_t i = i0; i < i1; ++i) {
            float a = std::fabs(W[i]); e += (double)a * a; if (a > mx) mx = a;
        }
        return std::pair<double,float>(e, mx);
    };
#ifdef LAPLACE_HAS_MKL
    /* serial reduce is fine here; the histogram below is the O(n) part we parallelize. */
#endif
    { auto r = reduce(0, n, 0.0, 0.0f); total_e = r.first; wmax = r.second; }
    if (total_e <= 0.0 || wmax <= 0.0f) return 0.0;

    /* Pass 2: energy histogram over [0, wmax] (linear bins), TBB-combinable. Then walk bins
     * top-down, accumulating energy, until target_energy·total reached → floor = bin's lower edge. */
    const int BINS = 4096;
    const double inv = (double)BINS / (double)wmax;
    std::vector<double> hist(BINS, 0.0);
    auto bin_range = [&](std::size_t i0, std::size_t i1, std::vector<double>& h) {
        for (std::size_t i = i0; i < i1; ++i) {
            float a = std::fabs(W[i]);
            int b = (int)((double)a * inv); if (b >= BINS) b = BINS - 1; if (b < 0) b = 0;
            h[b] += (double)a * a;
        }
    };
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::enumerable_thread_specific<std::vector<double>> tls(
        [&]{ return std::vector<double>(BINS, 0.0); });
    oneapi::tbb::parallel_for(oneapi::tbb::blocked_range<std::size_t>(0, n),
        [&](const oneapi::tbb::blocked_range<std::size_t>& r) { bin_range(r.begin(), r.end(), tls.local()); });
    for (auto& local : tls) for (int b = 0; b < BINS; ++b) hist[b] += local[b];
#else
    bin_range(0, n, hist);
#endif

    const double want = target_energy * total_e;
    double acc = 0.0;
    for (int b = BINS - 1; b >= 0; --b) {
        acc += hist[b];
        if (acc >= want) return (double)b / inv;   // lower edge of this bin = the floor
    }
    return 0.0;
}

extern "C"
int build_address_book(const float* E, std::size_t vocab, std::size_t d_model,
                       const std::uint8_t* valid, std::int32_t* addr_out) {
    if (!E || !addr_out || vocab == 0 || d_model == 0) return -1;

    auto build = [&](std::size_t m0, std::size_t m1) {
        for (std::size_t m = m0; m < m1; ++m) {
            float    best = -1.0f;
            std::int32_t arg = -1;
            for (std::size_t t = 0; t < vocab; ++t) {
                if (valid && !valid[t]) continue;
                float a = std::fabs(E[t * d_model + m]);
                if (a > best) { best = a; arg = static_cast<std::int32_t>(t); }
            }
            addr_out[m] = arg;
        }
    };
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(
        oneapi::tbb::blocked_range<std::size_t>(0, d_model),
        [&](const oneapi::tbb::blocked_range<std::size_t>& r) { build(r.begin(), r.end()); });
#else
    build(0, d_model);
#endif
    return 0;
}

/* Count survivors in row u (|W|>floor && addr[col]>=0). */
static inline std::size_t row_count(const float* W, std::size_t u, std::size_t d_model,
                                    const std::int32_t* addr, float floor) {
    const float* row = W + u * d_model;
    std::size_t c = 0;
    for (std::size_t m = 0; m < d_model; ++m)
        if (std::fabs(row[m]) > floor && addr[m] >= 0) ++c;
    return c;
}

extern "C"
int resolve_matrix(const float* W, std::size_t n_units, std::size_t d_model,
                   const std::int32_t* addr, double floor,
                   std::size_t u0, std::size_t u1,
                   circuit_cell_t* out, std::size_t cap,
                   std::size_t* out_count, int* overflow) {
    if (!W || !addr || !out || !out_count || !overflow) return -1;
    if (u1 > n_units || u0 > u1 || d_model == 0) return -2;
    *out_count = 0; *overflow = 0;
    const std::size_t nu = u1 - u0;
    if (nu == 0) return 0;
    const float fl = static_cast<float>(floor);

    /* Pass 1: per-row survivor counts (parallel). */
    std::vector<std::size_t> counts(nu);
    auto count_range = [&](std::size_t i0, std::size_t i1) {
        for (std::size_t i = i0; i < i1; ++i) counts[i] = row_count(W, u0 + i, d_model, addr, fl);
    };
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(oneapi::tbb::blocked_range<std::size_t>(0, nu),
        [&](const oneapi::tbb::blocked_range<std::size_t>& r) { count_range(r.begin(), r.end()); });
#else
    count_range(0, nu);
#endif

    /* Prefix-sum → dense offsets; bail (overflow) if the window doesn't fit. */
    std::vector<std::size_t> offset(nu + 1);
    offset[0] = 0;
    for (std::size_t i = 0; i < nu; ++i) offset[i + 1] = offset[i] + counts[i];
    const std::size_t total = offset[nu];
    if (total > cap) { *overflow = 1; return 0; }

    /* Pass 2: fill at known offsets (parallel; each row writes its own slice → no races).
     * Within a row, ascending column order → deterministic. */
    auto fill_range = [&](std::size_t i0, std::size_t i1) {
        for (std::size_t i = i0; i < i1; ++i) {
            const std::size_t u = u0 + i;
            const float* row = W + u * d_model;
            std::size_t w = offset[i];
            for (std::size_t m = 0; m < d_model; ++m) {
                float v = row[m];
                if (std::fabs(v) > fl && addr[m] >= 0) {
                    out[w].unit  = static_cast<std::uint32_t>(u);
                    out[w].token = addr[m];
                    out[w].value = v;
                    ++w;
                }
            }
        }
    };
#ifdef LAPLACE_HAS_MKL
    oneapi::tbb::parallel_for(oneapi::tbb::blocked_range<std::size_t>(0, nu),
        [&](const oneapi::tbb::blocked_range<std::size_t>& r) { fill_range(r.begin(), r.end()); });
#else
    fill_range(0, nu);
#endif

    *out_count = total;
    return 0;
}
