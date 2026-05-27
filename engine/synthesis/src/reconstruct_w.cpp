#include "laplace/synthesis/reconstruct_w.h"

#include <Eigen/Core>
#include <Eigen/Cholesky>
#include <Eigen/Eigenvalues>
#include <Eigen/SVD>
#include <Eigen/Sparse>

#include <algorithm>
#include <cstddef>
#include <vector>

/* Substrate-native interior-tensor reconstruction.
 *
 * Math reminder:
 *   Substrate stores S[i,j] = aggregated bilinear score E[i]ᵀ · M · E[j],
 *   where M is the per-kind weight composition (Wᵀ·W for self-bilinear
 *   kinds; Wqᵀ·Wk for joint Q+K). E is the freshly-synthesized embedding
 *   from spectral embedding of substrate's typed-edge graph.
 *
 *   To recover M from S given E:
 *     G = EᵀE + λI         (small dense [N × N], symmetric PSD)
 *     G⁻¹ via Eigen LDLT (avoids forming the inverse explicitly)
 *     T = Eᵀ · S · E       (small dense [N × N]; sparse-dense via Eigen)
 *     M = G⁻¹ · T · G⁻¹
 *
 *   Then:
 *     Symmetric (Wᵀ·W = M):
 *       SelfAdjointEigenSolver(M) → eigenvalues λᵢ (ascending), eigenvectors Vᵢ
 *       W[i,:] = √λ_(N-1-i) · V_(N-1-i)ᵀ  for i ∈ [0, out_dim) (descending order)
 *
 *     Asymmetric (Wqᵀ·Wk = M):
 *       JacobiSVD(M) → singular values σᵢ (descending), left U, right V
 *       Wq[i,:] = √σᵢ · U.col(i)ᵀ  for i ∈ [0, out_dim_q)
 *       Wk[i,:] = √σᵢ · V.col(i)ᵀ  for i ∈ [0, out_dim_k)
 *
 * W is recovered up to an orthogonal gauge — behavioral fidelity is the
 * criterion, not bit-faithful recovery of the source model's arbitrary
 * frame. The cross-source consensus through laplace_glicko2_accumulate
 * (applied to S before this primitive is called) is the substrate's
 * actual semantic content; reconstruction surfaces it through the
 * recipe's structural mold. */

namespace {

using SpMat   = Eigen::SparseMatrix<double>;
using Triplet = Eigen::Triplet<double>;

/* Build sparse S from COO triples (positive weights only — substrate's
 * noise floor is "aggregate == 0"). Caller chooses whether to symmetrize. */
SpMat build_sparse_S(const int*    rows,
                     const int*    cols,
                     const double* weights,
                     std::size_t   nnz,
                     std::size_t   vocab,
                     bool          symmetrize) {
    std::vector<Triplet> triplets;
    triplets.reserve(nnz);
    for (std::size_t e = 0; e < nnz; ++e) {
        if (weights[e] <= 0.0) continue;
        const int r = rows[e];
        const int c = cols[e];
        if (r < 0 || c < 0) continue;
        if ((std::size_t)r >= vocab || (std::size_t)c >= vocab) continue;
        triplets.emplace_back(r, c, weights[e]);
    }
    SpMat S(static_cast<int>(vocab), static_cast<int>(vocab));
    S.setFromTriplets(triplets.begin(), triplets.end(),
                      [](const double& a, const double& b){ return a + b; });
    if (symmetrize) {
        SpMat ST = SpMat(S.transpose());
        S = 0.5 * (S + ST);
    }
    S.makeCompressed();
    return S;
}

/* Given freshly-synthesized E and the kind's sparse adjacency S, compute
 *   M = G⁻¹ · T · G⁻¹   where  G = EᵀE + λI ,  T = Eᵀ · S · E.
 *
 * G is dense [N × N] symmetric PSD — Eigen LDLT solves G·X = B without
 * forming G⁻¹. We solve twice: X = G⁻¹·T, then M = (G⁻¹·Xᵀ)ᵀ = G⁻¹·X
 * (since X is structurally symmetric in the self-bilinear path; for the
 * asymmetric path we apply the second solve on Xᵀ explicitly). */
Eigen::MatrixXd compute_M(const Eigen::Map<const Eigen::MatrixXd>& E_map,
                          const SpMat&  S,
                          double        lambda,
                          bool          symmetrize_M_at_end) {
    const Eigen::Index N = E_map.cols();
    /* G = EᵀE + λI */
    Eigen::MatrixXd G = E_map.transpose() * E_map;
    G.diagonal().array() += lambda;
    Eigen::LDLT<Eigen::MatrixXd> ldlt(G);

    /* T = Eᵀ · S · E   (sparse-dense via Eigen operator*) */
    Eigen::MatrixXd SE = S * E_map;                     /* [vocab × N] */
    Eigen::MatrixXd T  = E_map.transpose() * SE;        /* [N × N] */

    /* M = G⁻¹ · T · G⁻¹ */
    Eigen::MatrixXd X = ldlt.solve(T);                  /* G⁻¹·T */
    Eigen::MatrixXd M = ldlt.solve(X.transpose()).transpose(); /* G⁻¹·T·G⁻¹ */

    if (symmetrize_M_at_end) {
        M = 0.5 * (M + M.transpose());
    }
    /* Sanity: dims */
    (void)N;
    return M;
}

} /* namespace */

extern "C"
int reconstruct_w_from_token_pair_attestations(
    const double* E_ptr, std::size_t vocab, std::size_t N,
    const int* s_rows, const int* s_cols, const double* s_weights, std::size_t s_nnz,
    std::size_t out_dim, double lambda,
    float* W_out) {

    if (!E_ptr || !W_out) return -1;
    if (!s_rows || !s_cols || !s_weights) return -1;
    if (vocab == 0 || N == 0 || out_dim == 0) return -2;
    if (out_dim > N) return -2;

    /* Map E [vocab × N] row-major */
    Eigen::Map<const Eigen::MatrixXd> E_map(E_ptr,
                                            static_cast<Eigen::Index>(vocab),
                                            static_cast<Eigen::Index>(N));

    /* Symmetric path: build SYMMETRIC S */
    SpMat S = build_sparse_S(s_rows, s_cols, s_weights, s_nnz, vocab, /*symmetrize*/ true);
    if (S.nonZeros() == 0) return -4;

    Eigen::MatrixXd M = compute_M(E_map, S, lambda, /*symmetrize_M_at_end*/ true);

    /* M = WᵀW → eigendecomposition (M is symmetric PSD up to numerical noise).
     * Eigenvalues ascending by Eigen's convention. */
    Eigen::SelfAdjointEigenSolver<Eigen::MatrixXd> es(M);
    if (es.info() != Eigen::Success) return -3;

    Eigen::VectorXd evals = es.eigenvalues();
    Eigen::MatrixXd evecs = es.eigenvectors();

    /* Take top out_dim eigenvalues (descending magnitude),
     * clamp ≥ 0 (numerical noise). W[i,:] = sqrt(λ_i) · v_iᵀ. */
    for (std::size_t i = 0; i < out_dim; ++i) {
        const Eigen::Index col = static_cast<Eigen::Index>(evals.size())
                                  - 1 - static_cast<Eigen::Index>(i);
        double lam = std::max(0.0, evals(col));
        double scale = std::sqrt(lam);
        for (std::size_t j = 0; j < N; ++j) {
            W_out[i * N + j] = static_cast<float>(scale * evecs(static_cast<Eigen::Index>(j), col));
        }
    }
    return 0;
}

extern "C"
int reconstruct_qk_from_token_pair_attestations(
    const double* E_ptr, std::size_t vocab, std::size_t N,
    const int* s_rows, const int* s_cols, const double* s_weights, std::size_t s_nnz,
    std::size_t out_dim_q, std::size_t out_dim_k, double lambda,
    float* Wq_out, float* Wk_out) {

    if (!E_ptr || !Wq_out || !Wk_out) return -1;
    if (!s_rows || !s_cols || !s_weights) return -1;
    if (vocab == 0 || N == 0 || out_dim_q == 0 || out_dim_k == 0) return -2;
    if (out_dim_q > N || out_dim_k > N) return -2;

    Eigen::Map<const Eigen::MatrixXd> E_map(E_ptr,
                                            static_cast<Eigen::Index>(vocab),
                                            static_cast<Eigen::Index>(N));

    /* Asymmetric path: do NOT symmetrize S. */
    SpMat S = build_sparse_S(s_rows, s_cols, s_weights, s_nnz, vocab, /*symmetrize*/ false);
    if (S.nonZeros() == 0) return -4;

    Eigen::MatrixXd M = compute_M(E_map, S, lambda, /*symmetrize_M_at_end*/ false);

    /* M = Wqᵀ·Wk → SVD M = U·Σ·Vᵀ. */
    Eigen::JacobiSVD<Eigen::MatrixXd> svd(M, Eigen::ComputeFullU | Eigen::ComputeFullV);
    if (svd.info() != Eigen::Success) return -3;

    Eigen::VectorXd sv = svd.singularValues();          /* descending by convention */
    Eigen::MatrixXd U  = svd.matrixU();
    Eigen::MatrixXd V  = svd.matrixV();

    /* Wq[i,:] = sqrt(σ_i) · U.col(i)ᵀ */
    for (std::size_t i = 0; i < out_dim_q; ++i) {
        double s = std::max(0.0, sv(static_cast<Eigen::Index>(i)));
        double scale = std::sqrt(s);
        for (std::size_t j = 0; j < N; ++j) {
            Wq_out[i * N + j] = static_cast<float>(scale * U(static_cast<Eigen::Index>(j), static_cast<Eigen::Index>(i)));
        }
    }
    /* Wk[i,:] = sqrt(σ_i) · V.col(i)ᵀ */
    for (std::size_t i = 0; i < out_dim_k; ++i) {
        double s = std::max(0.0, sv(static_cast<Eigen::Index>(i)));
        double scale = std::sqrt(s);
        for (std::size_t j = 0; j < N; ++j) {
            Wk_out[i * N + j] = static_cast<float>(scale * V(static_cast<Eigen::Index>(j), static_cast<Eigen::Index>(i)));
        }
    }
    return 0;
}
