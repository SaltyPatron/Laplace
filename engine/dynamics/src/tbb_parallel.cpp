#include "laplace/dynamics/tbb_parallel.h"

#include "laplace/dynamics/init.h"

#ifdef LAPLACE_HAS_MKL

#  include <atomic>
#  include <cstdio>
#  include <cstdlib>
#  include <mutex>

#  include <oneapi/tbb/global_control.h>
#  include <oneapi/tbb/info.h>
#  include <oneapi/tbb/task_arena.h>

namespace laplace {
namespace tbb_ops {

namespace {

int max_threads_per_core_from_env() {
    const char* v = std::getenv("LAPLACE_TBB_MAX_THREADS_PER_CORE");
    if (!v || !*v)
        return 0;
    char* end = nullptr;
    const long n = std::strtol(v, &end, 10);
    if (end == v || n < 1)
        return 0;
    return static_cast<int>(n);
}

// Atomic publication: the old plain pointer was a lockless double-checked
// init — a second thread could observe the store before the task_arena
// constructor's writes retired and dereference a half-constructed arena
// inside a parallel kernel. Release-store on construct, acquire-load on read.
std::atomic<oneapi::tbb::task_arena*> g_arena{nullptr};

void apply_max_threads_per_core(oneapi::tbb::task_arena::constraints& c) {
    if (const int m = max_threads_per_core_from_env())
        c.set_max_threads_per_core(m);
}

oneapi::tbb::task_arena make_arena(int host, int mkl_threads) {
    oneapi::tbb::task_arena::constraints c;
    if (host == LAPLACE_RUNTIME_HOST_PG) {
        c.set_max_concurrency(static_cast<unsigned int>(mkl_threads));
        apply_max_threads_per_core(c);
        return oneapi::tbb::task_arena(c);
    }
    const auto types = oneapi::tbb::info::core_types();
    if (types.size() > 1)
        c.set_core_type(types.back());
    apply_max_threads_per_core(c);
    const int n = oneapi::tbb::info::default_concurrency(c);
    if (n > 0)
        c.set_max_concurrency(n);
    return oneapi::tbb::task_arena(c);
}

}

int performance_concurrency() {
    oneapi::tbb::task_arena::constraints c;
    const auto types = oneapi::tbb::info::core_types();
    if (types.size() > 1)
        c.set_core_type(types.back());
    apply_max_threads_per_core(c);
    const int n = oneapi::tbb::info::default_concurrency(c);
    return n > 0 ? n : 0;
}

void warm_performance_arena(int host, int mkl_threads) {
    static oneapi::tbb::global_control thread_cap(
        oneapi::tbb::global_control::max_allowed_parallelism,
        static_cast<size_t>(mkl_threads));
    static std::mutex warm_mu;
    std::lock_guard<std::mutex> lk(warm_mu);
    if (!g_arena.load(std::memory_order_relaxed))
        g_arena.store(new oneapi::tbb::task_arena(make_arena(host, mkl_threads)),
                      std::memory_order_release);
}

oneapi::tbb::task_arena& performance_arena() {
    auto* arena = g_arena.load(std::memory_order_acquire);
    if (!arena) {
        (void)laplace_runtime_init(LAPLACE_RUNTIME_HOST_CLI, -1);
        arena = g_arena.load(std::memory_order_acquire);
    }
    if (!arena) {
        // Init failed before warming the arena. Dereferencing would segfault
        // deep inside a parallel kernel; die with a diagnosable message.
        std::fprintf(stderr,
                     "laplace_dynamics: performance_arena unavailable — "
                     "laplace_runtime_init failed (no resolvable thread count)\n");
        std::abort();
    }
    return *arena;
}

}

}

#endif
