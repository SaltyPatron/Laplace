// bench_bilinear_edges — race the REAL bilinear_edges_tile against cuBLAS.
//
// This is the contraction the model lane spends its time in: C = L * R^T,
// threshold |c| > theta, emit COO triplets plus an int64 score per surviving
// pair. It is deliberately the ONLY place a GPU is evaluated for this
// substrate, because the boundary is:
//
//     GPU FP32 for contraction magnitudes feeding Glicko.
//     NEVER for identity, placement, or ordering.
//
// bilinear_edges_tile output is thresholded and quantised into int64 via
// laplace_score_fp, then becomes graded votes on edges between entities that
// already exist. witnessWeight = 0.5*(1 + tanh(m/M)) is robust to small
// perturbation, so approximate testimony is adjudicated downstream. Nothing
// here touches a coord, a hilbert_index, a trajectory or a content hash, all of
// which require bit-reproducibility that a GPU reduction cannot offer at any
// width (CUDA defaults to -fmad=true and block reduction order is undefined).
//
// The CPU side calls the SHIPPED function out of liblaplace_dynamics. It is not
// a reimplementation -- benchmarking a lookalike would measure this file
// instead of the substrate.
//
// NOT wired into CMakeLists.txt on purpose: enabling the CUDA language in the
// shared build changes the toolchain for every target and every developer, to
// benchmark one function. Build it directly:
//
//   nvcc -O3 -std=c++17 -arch=sm_61 \
//     engine/dynamics/tools/bilinear_edges_bench/bench_bilinear_edges.cu \
//     -Iengine/dynamics/include -Iengine/core/include \
//     -Lbuild/dynamics -Lbuild/core -llaplace_dynamics -llaplace_core -lcublas \
//     -Wl,-rpath,$PWD/build/dynamics -Wl,-rpath,$PWD/build/core \
//     -o build/bench_bilinear_edges
//
// sm_61 is the 1080 Ti (Pascal, compute 6.1). Note that Pascal runs FP64 at
// 1/32 of FP32 rate, which is the entire reason both widths are timed.

#include <cublas_v2.h>
#include <cuda_runtime.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <vector>

#include "laplace/dynamics/bilinear_edges.h"

#define CK(expr)                                                               \
    do {                                                                       \
        cudaError_t err_ = (expr);                                             \
        if (err_ != cudaSuccess) {                                             \
            std::fprintf(stderr, "CUDA %s:%d: %s\n", __FILE__, __LINE__,       \
                         cudaGetErrorString(err_));                            \
            std::exit(1);                                                      \
        }                                                                      \
    } while (0)

#define CB(expr)                                                               \
    do {                                                                       \
        cublasStatus_t st_ = (expr);                                           \
        if (st_ != CUBLAS_STATUS_SUCCESS) {                                    \
            std::fprintf(stderr, "cuBLAS %s:%d: status %d\n", __FILE__,        \
                         __LINE__, (int)st_);                                  \
            std::exit(1);                                                      \
        }                                                                      \
    } while (0)

// laplace_score_fp(v, m) from engine/core/src/score.c, verbatim, on device.
// m is 1.0 at this call site (bilinear_edges.cpp:51).
__device__ __forceinline__ long long score_fp_dev(double v, double m) {
    const double s = 0.5 * (1.0 + v / (m + fabs(v)));
    return llround(s * 1e9);
}

struct Edge {
    int       row;
    int       col;
    double    val;
    long long score;
};

// One thread per (row, col). atomicAdd hands out slots, so emission ORDER is
// nondeterministic -- which is fine because the caller sorts before comparing,
// and is itself a demonstration of why this path may not feed an ordered
// structure.
template <typename T>
__global__ void threshold_emit(const T* __restrict__ C, int n_rows, int n_cols,
                               double theta, int cap, Edge* __restrict__ out,
                               unsigned int* __restrict__ count) {
    const long long idx = (long long)blockIdx.x * blockDim.x + threadIdx.x;
    const long long n   = (long long)n_rows * n_cols;
    if (idx >= n) return;
    const double v = (double)C[idx];
    if (fabs(v) <= theta) return;
    const unsigned int slot = atomicAdd(count, 1u);
    if (slot >= (unsigned int)cap) return;
    Edge e;
    e.row   = (int)(idx / n_cols);
    e.col   = (int)(idx % n_cols);
    e.val   = v;
    e.score = score_fp_dev(v, 1.0);
    out[slot] = e;
}

static bool edge_less(const Edge& a, const Edge& b) {
    if (a.row != b.row) return a.row < b.row;
    return a.col < b.col;
}

static double ms_since(cudaEvent_t a, cudaEvent_t b) {
    float ms = 0.0f;
    cudaEventElapsedTime(&ms, a, b);
    return (double)ms;
}

int main(int argc, char** argv) {
    // TinyLlama L0H0 shape: ~4k tokens against ~4k tokens over one head.
    int n_left  = (argc > 1) ? std::atoi(argv[1]) : 4096;
    int n_right = (argc > 2) ? std::atoi(argv[2]) : 4096;
    int r       = (argc > 3) ? std::atoi(argv[3]) : 64;
    double theta = (argc > 4) ? std::atof(argv[4]) : 2.0;

    const long long pairs = (long long)n_left * n_right;
    std::printf("bilinear_edges_tile: %d x %d over r=%d  (%lld pairs)  theta=%.3f\n",
                n_left, n_right, r, pairs, theta);

    // Deterministic inputs. A fixed LCG rather than rand(): the whole point of
    // this substrate is reproducibility, and a benchmark whose input changes
    // per run cannot be compared to a previous run.
    std::vector<double> L((size_t)n_left * r), R((size_t)n_right * r);
    unsigned long long st = 0x9E3779B97F4A7C15ULL;
    auto next = [&st]() {
        st = st * 6364136223846793005ULL + 1442695040888963407ULL;
        return (double)((st >> 11) & 0x1FFFFFFFFFFFFFULL) / (double)0x20000000000000ULL;
    };
    for (auto& x : L) x = next() * 2.0 - 1.0;
    for (auto& x : R) x = next() * 2.0 - 1.0;

    const size_t cap = (size_t)pairs / 4 + 1024;

    // ---------- CPU: the shipped function ----------
    std::vector<int>       c_rows(cap), c_cols(cap);
    std::vector<double>    c_vals(cap);
    std::vector<long long> c_scores(cap);
    size_t c_count = 0;
    int    c_over  = 0;

    cudaEvent_t t0, t1;
    CK(cudaEventCreate(&t0));
    CK(cudaEventCreate(&t1));

    // warm MKL's first-call setup out of the measurement
    {
        size_t w = 0; int wo = 0;
        bilinear_edges_tile(L.data(), 0, 1, R.data(), (size_t)n_right, (size_t)r,
                            theta, c_rows.data(), c_cols.data(), c_vals.data(),
                            c_scores.data(), cap, &w, &wo);
    }

    cudaEventRecord(t0);
    int rc = bilinear_edges_tile(L.data(), 0, (size_t)n_left,
                                 R.data(), (size_t)n_right, (size_t)r, theta,
                                 c_rows.data(), c_cols.data(), c_vals.data(),
                                 c_scores.data(), cap, &c_count, &c_over);
    cudaEventRecord(t1);
    CK(cudaEventSynchronize(t1));
    const double cpu_ms = ms_since(t0, t1);

    if (rc != 0) {
        std::fprintf(stderr, "bilinear_edges_tile rc=%d "
                             "(rc=-2 means the build has no MKL)\n", rc);
        return 1;
    }
    std::printf("CPU  fp64  MKL dgemm + emit   %9.2f ms   %zu edges%s\n",
                cpu_ms, c_count, c_over ? "  (OVERFLOW)" : "");

    std::vector<Edge> cpu_edges(c_count);
    for (size_t i = 0; i < c_count; ++i)
        cpu_edges[i] = Edge{c_rows[i], c_cols[i], c_vals[i], c_scores[i]};
    std::sort(cpu_edges.begin(), cpu_edges.end(), edge_less);

    // ---------- GPU ----------
    cublasHandle_t h;
    CB(cublasCreate(&h));

    Edge*         d_edges = nullptr;
    unsigned int* d_count = nullptr;
    CK(cudaMalloc(&d_edges, cap * sizeof(Edge)));
    CK(cudaMalloc(&d_count, sizeof(unsigned int)));

    const int  threads = 256;
    const long blocks  = (pairs + threads - 1) / threads;

    // cuBLAS is column-major. Computing C_col = R * L^T with (m,n,k) =
    // (n_right, n_left, r) yields exactly the row-major L * R^T the CPU path
    // produces, with no transposes and no relabelling of the output.
    auto run_gpu = [&](bool use_fp32, double* out_gemm_ms, double* out_total_ms,
                       std::vector<Edge>* out_edges, unsigned int* out_count) {
        cudaEvent_t g0, g1, g2, g3;
        CK(cudaEventCreate(&g0)); CK(cudaEventCreate(&g1));
        CK(cudaEventCreate(&g2)); CK(cudaEventCreate(&g3));

        void *dL = nullptr, *dR = nullptr, *dC = nullptr;
        const size_t esz = use_fp32 ? sizeof(float) : sizeof(double);
        CK(cudaMalloc(&dL, (size_t)n_left  * r * esz));
        CK(cudaMalloc(&dR, (size_t)n_right * r * esz));
        CK(cudaMalloc(&dC, (size_t)pairs * esz));

        std::vector<float> Lf, Rf;
        if (use_fp32) {
            Lf.assign(L.begin(), L.end());
            Rf.assign(R.begin(), R.end());
        }

        cudaEventRecord(g0);
        if (use_fp32) {
            CK(cudaMemcpy(dL, Lf.data(), Lf.size() * esz, cudaMemcpyHostToDevice));
            CK(cudaMemcpy(dR, Rf.data(), Rf.size() * esz, cudaMemcpyHostToDevice));
        } else {
            CK(cudaMemcpy(dL, L.data(), L.size() * esz, cudaMemcpyHostToDevice));
            CK(cudaMemcpy(dR, R.data(), R.size() * esz, cudaMemcpyHostToDevice));
        }
        cudaEventRecord(g1);

        if (use_fp32) {
            const float alpha = 1.0f, beta = 0.0f;
            CB(cublasSgemm(h, CUBLAS_OP_T, CUBLAS_OP_N, n_right, n_left, r,
                           &alpha, (const float*)dR, r, (const float*)dL, r,
                           &beta, (float*)dC, n_right));
        } else {
            const double alpha = 1.0, beta = 0.0;
            CB(cublasDgemm(h, CUBLAS_OP_T, CUBLAS_OP_N, n_right, n_left, r,
                           &alpha, (const double*)dR, r, (const double*)dL, r,
                           &beta, (double*)dC, n_right));
        }
        cudaEventRecord(g2);

        CK(cudaMemset(d_count, 0, sizeof(unsigned int)));
        if (use_fp32)
            threshold_emit<float><<<blocks, threads>>>(
                (const float*)dC, n_left, n_right, theta, (int)cap, d_edges, d_count);
        else
            threshold_emit<double><<<blocks, threads>>>(
                (const double*)dC, n_left, n_right, theta, (int)cap, d_edges, d_count);
        CK(cudaGetLastError());

        unsigned int cnt = 0;
        CK(cudaMemcpy(&cnt, d_count, sizeof(unsigned int), cudaMemcpyDeviceToHost));
        const unsigned int kept = std::min<unsigned int>(cnt, (unsigned int)cap);
        out_edges->resize(kept);
        if (kept) CK(cudaMemcpy(out_edges->data(), d_edges, (size_t)kept * sizeof(Edge),
                                cudaMemcpyDeviceToHost));
        cudaEventRecord(g3);
        CK(cudaEventSynchronize(g3));

        *out_gemm_ms  = ms_since(g1, g2);
        *out_total_ms = ms_since(g0, g3);
        *out_count    = cnt;
        std::sort(out_edges->begin(), out_edges->end(), edge_less);

        CK(cudaFree(dL)); CK(cudaFree(dR)); CK(cudaFree(dC));
        cudaEventDestroy(g0); cudaEventDestroy(g1);
        cudaEventDestroy(g2); cudaEventDestroy(g3);
    };

    for (int pass = 0; pass < 2; ++pass) {
        const bool fp32 = (pass == 1);
        double gemm_ms = 0.0, total_ms = 0.0;
        unsigned int gcount = 0;
        std::vector<Edge> gpu_edges;
        run_gpu(fp32, &gemm_ms, &total_ms, &gpu_edges, &gcount);

        const double gflop = 2.0 * (double)pairs * (double)r / 1e9;
        std::printf("GPU  %s cuBLAS %-6s        %9.2f ms   %u edges   "
                    "(gemm %7.2f ms = %6.1f GFLOP/s)\n",
                    fp32 ? "fp32" : "fp64", fp32 ? "sgemm" : "dgemm",
                    total_ms, gcount, gemm_ms, gflop / (gemm_ms / 1000.0));

        // Agreement against the shipped CPU result. This is the number that
        // decides whether the speedup is usable, not the milliseconds.
        size_t matched = 0, missing = 0, extra = 0;
        long long worst_score_delta = 0;
        size_t i = 0, j = 0;
        while (i < cpu_edges.size() && j < gpu_edges.size()) {
            if (edge_less(cpu_edges[i], gpu_edges[j])) { ++missing; ++i; }
            else if (edge_less(gpu_edges[j], cpu_edges[i])) { ++extra; ++j; }
            else {
                worst_score_delta = std::max(
                    worst_score_delta,
                    std::llabs(cpu_edges[i].score - gpu_edges[j].score));
                ++matched; ++i; ++j;
            }
        }
        missing += cpu_edges.size() - i;
        extra   += gpu_edges.size() - j;
        std::printf("     agreement vs CPU fp64: %zu matched, %zu missing, %zu extra, "
                    "worst |score delta| = %lld of 1e9\n",
                    matched, missing, extra, worst_score_delta);
    }

    CK(cudaFree(d_edges));
    CK(cudaFree(d_count));
    CB(cublasDestroy(h));
    cudaEventDestroy(t0);
    cudaEventDestroy(t1);
    return 0;
}
