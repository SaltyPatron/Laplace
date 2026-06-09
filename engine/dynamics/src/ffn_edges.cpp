#include "laplace/dynamics/ffn_edges.h"
#include "laplace/core/score.h"

#include <cmath>
#include <cstddef>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

namespace {
inline double silu(double x) {
    if (x > 40.0)  return x;
    if (x < -40.0) return 0.0;
    return x / (1.0 + std::exp(-x));
}
}  // namespace

extern "C"
int ffn_token_pairs_tile(
    const double* emb,   std::size_t n, std::size_t d,
    const double* unemb,
    const double* gate, const double* up, const double* down, std::size_t interm,
    std::size_t row_begin, std::size_t row_end,
    double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    std::size_t cap, std::size_t* out_count, int* overflow)
{
    if (!emb || !unemb || !down || !out_rows || !out_cols || !out_vals || !out_count || !overflow)
        return -1;
    if (!gate && !up) return -1;
    if (row_end <= row_begin || row_end > n || d == 0 || interm == 0) return -1;

    *out_count = 0;
    *overflow  = 0;
    const std::size_t t = row_end - row_begin;

#ifdef LAPLACE_HAS_MKL
    const double* E = emb + row_begin * d;

    // A = act(G, U), neuron-space activation [t x interm].
    std::vector<double> A((std::size_t)t * interm);
    std::vector<double> G, U;
    if (gate) {
        G.resize((std::size_t)t * interm);
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
            (MKL_INT)t, (MKL_INT)interm, (MKL_INT)d,
            1.0, E, (MKL_INT)d, gate, (MKL_INT)d, 0.0, G.data(), (MKL_INT)interm);
    }
    if (up) {
        U.resize((std::size_t)t * interm);
        cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
            (MKL_INT)t, (MKL_INT)interm, (MKL_INT)d,
            1.0, E, (MKL_INT)d, up, (MKL_INT)d, 0.0, U.data(), (MKL_INT)interm);
    }
    for (std::size_t i = 0; i < (std::size_t)t * interm; ++i) {
        if (gate && up)   A[i] = silu(G[i]) * U[i];
        else if (gate)    A[i] = silu(G[i]);
        else              A[i] = U[i];
    }

    // O = A @ down^T [t x d]; the neuron (interm) dimension is contracted here.
    std::vector<double> O((std::size_t)t * d);
    cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)t, (MKL_INT)d, (MKL_INT)interm,
        1.0, A.data(), (MKL_INT)interm, down, (MKL_INT)interm, 0.0, O.data(), (MKL_INT)d);

    // Normalize O rows so the score against the (already normalized) un-embedding
    // is a cosine, comparable to the c/sqrt(dim) noise floor.
    for (std::size_t i = 0; i < t; ++i) {
        double* Orow = O.data() + i * d;
        double ss = 0.0;
        for (std::size_t c = 0; c < d; ++c) ss += Orow[c] * Orow[c];
        const double inv = ss > 0.0 ? 1.0 / std::sqrt(ss) : 0.0;
        for (std::size_t c = 0; c < d; ++c) Orow[c] *= inv;
    }

    // S = O @ unemb^T [t x n], then threshold.
    std::vector<double> S((std::size_t)t * n);
    cblas_dgemm(CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)t, (MKL_INT)n, (MKL_INT)d,
        1.0, O.data(), (MKL_INT)d, unemb, (MKL_INT)d, 0.0, S.data(), (MKL_INT)n);

    std::size_t cnt = 0;
    for (std::size_t i = 0; i < t; ++i) {
        const double* Srow = S.data() + i * n;
        const int gi = (int)(row_begin + i);
        for (std::size_t s = 0; s < n; ++s) {
            const double v = Srow[s];
            if (std::fabs(v) > theta) {
                if (cnt >= cap) { *overflow = 1; *out_count = cnt; return 0; }
                out_rows[cnt] = gi;
                out_cols[cnt] = (int)s;
                out_vals[cnt] = v;
                if (out_scores) out_scores[cnt] = (long long)laplace_score_fp(v, 1.0);
                ++cnt;
            }
        }
    }
    *out_count = cnt;
    return 0;
#else
    (void)emb; (void)unemb; (void)gate; (void)up; (void)down; (void)interm;
    (void)theta; (void)out_rows; (void)out_cols; (void)out_vals; (void)out_scores; (void)cap; (void)t;
    return -2;
#endif
}
