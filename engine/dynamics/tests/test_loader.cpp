#include "laplace/dynamics/init.h"
#include "laplace/dynamics/bilinear_edges.h"

#include <cmath>
#include <cstdio>

#ifdef _WIN32
#include <windows.h>
using Library = HMODULE;
static Library load(const char* path) { return LoadLibraryA(path); }
static auto symbol(Library library, const char* name) { return GetProcAddress(library, name); }
#else
#include <dlfcn.h>
using Library = void*;
static Library load(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static auto symbol(Library library, const char* name) { return dlsym(library, name); }
#endif

int main(int argc, char** argv) {
    if (argc != 3) return 1;
    // Match NativeLibraryClosure: Core first, then Dynamics, both app-local.
    const Library core = load(argv[1]);
    const Library dynamics = core ? load(argv[2]) : nullptr;
    if (!dynamics) {
        std::fputs("Could not load the native engine closure.\n", stderr);
        return 2;
    }
    const auto init = reinterpret_cast<decltype(&laplace_runtime_init)>(symbol(dynamics, "laplace_runtime_init"));
    const auto arena = reinterpret_cast<decltype(&bilinear_arena_rms)>(symbol(dynamics, "bilinear_arena_rms"));
    if (!init || !arena) return 3;
    const int status = init(LAPLACE_RUNTIME_HOST_CLI, 2);
    if (status != 0) {
        std::fprintf(stderr, "Native runtime initialization failed: %d\n", status);
        return 4;
    }
#if defined(__linux__)
    if (!dlsym(RTLD_DEFAULT, "omp_get_num_procs")) return 5;
#endif
    // An actual MKL GEMM through the public ABI, after local-scope loading.
    const double left[] = {1, 0, 0, 2};
    const double right[] = {3, 0, 0, 4};
    double rms = 0;
    if (arena(left, 2, right, 2, 2, &rms) != 0
        || !std::isfinite(rms) || std::fabs(rms - std::sqrt(73.0 / 4.0)) > 1e-12)
        return 6;
    std::puts("Local-scope native initialization and MKL kernel passed.");
    return 0;
}
