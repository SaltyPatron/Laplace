#include "laplace/dynamics/procrustes.h"

#include <Eigen/Core>
#include <Eigen/SVD>
#include <cstddef>
#include <new>

/* Rectangular orthogonal Procrustes alignment with optional Umeyama scale.
 *
 * Source point set P (n × source_dim, row-major), target point set Q
 * (n × 4, row-major XYZM). Finds the (source_dim × 4) orthogonal-columns
 * projection R, scale s ∈ ℝ_+, and translations such that
 *   Q ≈ s · (P − μ_P) · R + μ_Q
 * is minimized in Frobenius norm.
 *
 * Algorithm (Schönemann 1966 + Umeyama 1991, via SVD):
 *   1. μ_P = mean(P), μ_Q = mean(Q); P_c = P − μ_P, Q_c = Q − μ_Q.
 *   2. H = P_cᵀ · Q_c  (source_dim × 4 cross-covariance).
 *   3. SVD: H = U · Σ · Vᵀ  (thin SVD).
 *   4. R = U · Vᵀ.
 *   5. s = Σ_ii summed / ||P_c||_F²  (Umeyama optimal scale).
 *   6. Residual ε = ||s · P_c · R − Q_c||_F.
 *
 * SVD is computed via Eigen's JacobiSVD — a NATIVE Eigen kernel. JacobiSVD and
 * BDCSVD have no LAPACKE backend, so EIGEN_USE_MKL_ALL does NOT dispatch them to
 * MKL (unlike Eigen's MKL-backed dense GEMM/LU). The cross-covariance H is small
 * (source_dim × 4), so native Jacobi is exact and single-threaded deterministic —
 * the fit is reproducible independent of the MKL_CBWR lock (which governs the
 * MKL-dispatched paths elsewhere in the engine).
 *
 * Reflection correction: not applied. For the substrate, source-embedding
 * spaces and the 4D target are both used as inner-product Euclidean
 * structures; handedness is not a substrate-level invariant. If a
 * downstream component requires proper rotations only, add a check on the
 * last singular value's sign per Kabsch's standard correction. */

struct procrustes_transform {
    std::size_t            source_dim;
    Eigen::VectorXd        source_mean;
    Eigen::Vector4d        target_mean;
    Eigen::MatrixXd        rotation;   /* source_dim × 4 */
    double                 scale;
    double                 residual;
};

extern "C"
procrustes_transform_t* procrustes_fit(const double* source_pts,
                                       std::size_t   n,
                                       std::size_t   source_dim,
                                       const double* target_pts) {
    if (n < 2 || source_dim < 1 || !source_pts || !target_pts) return nullptr;

    using RowMajMat = Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::RowMajor>;
    using RowMaj4   = Eigen::Matrix<double, Eigen::Dynamic, 4,              Eigen::RowMajor>;

    Eigen::Map<const RowMajMat> P(source_pts, static_cast<Eigen::Index>(n),
                                              static_cast<Eigen::Index>(source_dim));
    Eigen::Map<const RowMaj4>   Q(target_pts, static_cast<Eigen::Index>(n), 4);

    Eigen::VectorXd     p_mean     = P.colwise().mean();
    Eigen::RowVector4d  q_mean_row = Q.colwise().mean();

    Eigen::MatrixXd Pc = P.rowwise() - p_mean.transpose();
    Eigen::MatrixXd Qc = Q.rowwise() - q_mean_row;

    /* H = Pcᵀ · Qc — (source_dim × 4) cross-covariance. */
    const Eigen::MatrixXd H = Pc.transpose() * Qc;

    Eigen::JacobiSVD<Eigen::MatrixXd> svd(H, Eigen::ComputeThinU | Eigen::ComputeThinV);

    /* R = U · Vᵀ — (source_dim × 4) orthogonal-columns matrix. */
    Eigen::MatrixXd R = svd.matrixU() * svd.matrixV().transpose();

    /* Optimal scale minimizes ||s · Pc · R − Qc||_F²:
     *   s_opt = trace(Rᵀ · H) / ||Pc · R||_F² = Σ.sum() / ||Pc · R||_F²
     * For SQUARE R (d_src == 4) this reduces to Umeyama's classic
     *   s_opt = Σ.sum() / ||Pc||_F²,
     * because ||Pc · R||_F² = ||Pc||_F² when R is a full-dim isometry. For
     * RECTANGULAR R (d_src != 4) the denominator differs and the classic
     * Umeyama formula gives the wrong scale. */
    const Eigen::MatrixXd PcR = Pc * R;
    const double pcR_sq_norm = PcR.squaredNorm();
    const double scale = (pcR_sq_norm > 0.0)
        ? (svd.singularValues().sum() / pcR_sq_norm)
        : 1.0;

    /* Residual ε = ||s · Pc · R − Qc||_F. */
    const Eigen::MatrixXd predicted = scale * PcR;
    const double residual = (predicted - Qc).norm();

    auto* T = new (std::nothrow) procrustes_transform;
    if (!T) return nullptr;
    T->source_dim  = source_dim;
    T->source_mean = std::move(p_mean);
    T->target_mean = q_mean_row.transpose();
    T->rotation    = std::move(R);
    T->scale       = scale;
    T->residual    = residual;
    return T;
}

extern "C"
void procrustes_apply(const procrustes_transform_t* T,
                      const double*                 source_vec,
                      std::size_t                   source_dim,
                      double                        out[4]) {
    if (!T || !source_vec || !out || source_dim != T->source_dim) {
        if (out) { out[0] = out[1] = out[2] = out[3] = 0.0; }
        return;
    }
    Eigen::Map<const Eigen::VectorXd> p(source_vec, static_cast<Eigen::Index>(source_dim));
    const Eigen::Vector4d q =
        (T->scale * ((p - T->source_mean).transpose() * T->rotation)).transpose()
        + T->target_mean;
    out[0] = q[0]; out[1] = q[1]; out[2] = q[2]; out[3] = q[3];
}

extern "C"
double procrustes_residual(const procrustes_transform_t* T) {
    return T ? T->residual : 0.0;
}

extern "C"
void procrustes_free(procrustes_transform_t* T) {
    delete T;
}
