#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// FFN token->token contraction. The intermediate ("neuron") dimension is the
// model's internal join only: gate/up project token embeddings up into it, down
// projects back out, and it is contracted away inside the two GEMMs below. It is
// never materialized, never emitted, never crosses this boundary. What survives
// is the semantic relation between an input token and the output tokens its FFN
// pathway writes toward, above the noise floor theta.
//
//   G = E @ gate^T            [t x interm]   (skipped if gate == NULL)
//   U = E @ up^T              [t x interm]   (skipped if up   == NULL)
//   A = act(G, U)             [t x interm]   silu(G)*U | silu(G) | U
//   O = A @ down^T            [t x d]        neuron (interm) contracted here
//   O = normalize_rows(O)
//   S = O @ unemb^T           [t x n]
//   emit (row_begin+i, s, S[i,s]) where |S[i,s]| > theta
//
// emb / unemb are double, all n rows, row-major [n x d]; unemb must be L2-normalized.
// gate / up are [interm x d]; down is [d x interm]; at least one of gate/up non-NULL.
// out_vals receives the raw signed cosine S[i,s]. out_scores, when non-NULL, receives the
// int64 fixed-point Glicko score laplace_score_fp(S[i,s], 1.0) computed in-kernel, so the
// caller never crosses the managed/native boundary per edge to score. Pass NULL to skip.
// Returns 0 on success, -1 on bad args, -2 if built without MKL.
int ffn_token_pairs_tile(
    const double* emb,   size_t n, size_t d,
    const double* unemb,
    const double* gate, const double* up, const double* down, size_t interm,
    size_t row_begin, size_t row_end,
    double theta,
    int* out_rows, int* out_cols, double* out_vals, long long* out_scores,
    size_t cap, size_t* out_count, int* overflow);

#ifdef __cplusplus
}
#endif
