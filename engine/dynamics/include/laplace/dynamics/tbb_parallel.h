#pragma once

#ifdef LAPLACE_HAS_MKL

#  include <cstddef>

#  include <oneapi/tbb/blocked_range.h>
#  include <oneapi/tbb/parallel_for.h>
#  include <oneapi/tbb/task_arena.h>
#  include <utility>

namespace laplace {
namespace tbb_ops {

void warm_performance_arena(int host, int mkl_threads);

oneapi::tbb::task_arena& performance_arena();

template<typename Body>
void parallel_for_size(const oneapi::tbb::blocked_range<size_t>& range, Body&& body) {
    performance_arena().execute([&] {
        oneapi::tbb::parallel_for(range, std::forward<Body>(body));
    });
}

template<typename Body>
void parallel_for(const oneapi::tbb::blocked_range<std::size_t>& range, Body&& body) {
    performance_arena().execute([&] {
        oneapi::tbb::parallel_for(range, std::forward<Body>(body));
    });
}

}  // namespace tbb_ops
}  // namespace laplace

#endif
