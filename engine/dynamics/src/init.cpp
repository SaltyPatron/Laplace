#include "laplace/dynamics/init.h"

#ifndef LAPLACE_RUNTIME_NO_TBB
#include "laplace/dynamics/tbb_parallel.h"
#endif

#include <atomic>
#include <cstdlib>
#include <cstdio>
#include <mutex>

#ifdef LAPLACE_HAS_MKL
#include <mkl.h>
#if defined(__linux__)
#include <dlfcn.h>
#endif
#ifndef LAPLACE_MKL_CBWR_MODE
#  if defined(LAPLACE_MKL_CBWR_AVX512)
#    define LAPLACE_MKL_CBWR_MODE MKL_CBWR_AVX512
#  elif defined(LAPLACE_MKL_CBWR_AVX2)
#    define LAPLACE_MKL_CBWR_MODE MKL_CBWR_AVX2
#  else
#    define LAPLACE_MKL_CBWR_MODE MKL_CBWR_AUTO
#  endif
#endif

namespace {

int parse_positive_env(const char* name) {
    const char* v = std::getenv(name);
    if (!v || !*v)
        return 0;
    char* end = nullptr;
    const long n = std::strtol(v, &end, 10);
    if (end == v || n < 1)
        return 0;
    return static_cast<int>(n);
}

int resolve_thread_count_from_env() {
    if (const int n = parse_positive_env("MKL_NUM_THREADS"))
        return n;
    if (const int n = parse_positive_env("TBB_NUM_THREADS"))
        return n;
    if (const int n = parse_positive_env("LAPLACE_NATIVE_THREADS"))
        return n;
    return 0;
}

// Env vars are overrides only. When unset, self-detect from CPU topology —
// TBB's P-core concurrency (mirrors CpuTopology.PerformanceCoreCount on the
// C# side, which sets these env vars for managed hosts before native init).
int resolve_thread_count() {
    if (const int n = resolve_thread_count_from_env())
        return n;
#ifndef LAPLACE_RUNTIME_NO_TBB
    if (const int n = laplace::tbb_ops::performance_concurrency())
        return n;
#endif
    return 0;
}

int g_mkl_num_threads = -1;
int g_runtime_host = -1;

int initialize_mkl_threading() {
#ifdef LAPLACE_MKL_STATIC_SEQUENTIAL
    // PostgreSQL links mkl_sequential directly; SDL's layer selector is neither
    // available nor needed in this archive.
    return 0;
#else
#if defined(__linux__)
    // Managed hosts load the app-local engine with RTLD_LOCAL. MKL's SDL
    // subsequently opens its threading plugin, whose OpenMP references need
    // process-global scope. Reuse/promote the packaged Intel runtime by SONAME;
    // keep its handle for the same process lifetime as MKL and the engine.
    static void* openmp_runtime = nullptr;
    if (!openmp_runtime)
        openmp_runtime = dlopen("libiomp5.so", RTLD_NOW | RTLD_GLOBAL);
    if (!openmp_runtime) {
        std::fprintf(stderr, "laplace runtime: Intel OpenMP initialization failed: %s\n", dlerror());
        return -3;
    }
#endif
    // SDL selection must precede every other MKL call. TBB still schedules
    // engine work, while Intel OpenMP supplies MKL's reproducible kernels.
    if (mkl_set_threading_layer(MKL_THREADING_INTEL) != MKL_THREADING_INTEL)
        return -4;
    return 0;
#endif
}

}

#endif

extern "C" int laplace_runtime_resolve_thread_count(void) {
#ifdef LAPLACE_HAS_MKL
    return resolve_thread_count();
#else
    return 0;
#endif
}

extern "C" int laplace_runtime_init(int host, int mkl_threads) {
#ifndef LAPLACE_HAS_MKL
    (void)host;
    (void)mkl_threads;
    return -2;
#else
    // `static int initialized` was a non-atomic double-checked guard: two
    // threads first-touching the Dynamics and Synthesis natives concurrently
    // (any host without Laplace.Cli's single-threaded Program.Main warm-up —
    // the xunit host is the live case) could both enter the body and race
    // MKL's process-global first-init (torn CBWR/threading state). The mutex
    // serializes the body; the atomic keeps the completed fast path lock-free.
    // Early -1 returns stay outside the completed state so they remain
    // retryable, preserving the original contract.
    static std::mutex init_mu;
    static std::atomic<int> initialized{0};
    static int init_rc = 0;
    if (initialized.load(std::memory_order_acquire))
        return init_rc;
    std::lock_guard<std::mutex> lk(init_mu);
    if (initialized.load(std::memory_order_relaxed))
        return init_rc;

    g_runtime_host = host;
    if (mkl_threads > 0) {
        g_mkl_num_threads = mkl_threads;
    } else if (host == LAPLACE_RUNTIME_HOST_PG) {
        // Never autodetect inside a postgres backend — the GUC must decide.
        return -1;
    } else {
        g_mkl_num_threads = resolve_thread_count();
        if (g_mkl_num_threads <= 0)
            return -1;
    }

    const int threading_rc = initialize_mkl_threading();
    if (threading_rc != 0)
        return threading_rc;

    mkl_set_dynamic(0);
    mkl_set_num_threads(g_mkl_num_threads);

#ifndef LAPLACE_RUNTIME_NO_TBB
    laplace::tbb_ops::warm_performance_arena(host, g_mkl_num_threads);
#endif

    const int rc = mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE);
    init_rc = (rc == MKL_CBWR_SUCCESS) ? 0 : -1;
    initialized.store(1, std::memory_order_release);
    return init_rc;
#endif
}

extern "C" int laplace_runtime_host(void) {
#ifdef LAPLACE_HAS_MKL
    return g_runtime_host;
#else
    return -1;
#endif
}

extern "C" int laplace_dynamics_init(void) {
    return laplace_runtime_init(LAPLACE_RUNTIME_HOST_CLI, -1);
}

extern "C" const char* laplace_dynamics_version(void) {
    return "0.1.0";
}

extern "C" int laplace_dynamics_has_mkl(void) {
#ifdef LAPLACE_HAS_MKL
    return 1;
#else
    return 0;
#endif
}

extern "C" int laplace_dynamics_mkl_num_threads(void) {
#ifdef LAPLACE_HAS_MKL
    return g_mkl_num_threads;
#else
    return -1;
#endif
}

extern "C" int laplace_dynamics_cbwr_branch(void) {
#ifdef LAPLACE_HAS_MKL
    return mkl_cbwr_get(MKL_CBWR_BRANCH);
#else
    return -1;
#endif
}
