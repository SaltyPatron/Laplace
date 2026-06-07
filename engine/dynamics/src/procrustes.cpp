#include "laplace/dynamics/procrustes.h"

#include <Eigen/Core>
#include <Eigen/SVD>
#include <cstddef>
#include <new>

struct procrustes_transform {
    std::size_t            source_dim;
    Eigen::VectorXd        source_mean;
    Eigen::Vector4d        target_mean;
    Eigen::MatrixXd        rotation;
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

    const Eigen::MatrixXd H = Pc.transpose() * Qc;

    Eigen::JacobiSVD<Eigen::MatrixXd> svd(H, Eigen::ComputeThinU | Eigen::ComputeThinV);

    Eigen::MatrixXd R = svd.matrixU() * svd.matrixV().transpose();

    const Eigen::MatrixXd PcR = Pc * R;
    const double pcR_sq_norm = PcR.squaredNorm();
    const double scale = (pcR_sq_norm > 0.0)
        ? (svd.singularValues().sum() / pcR_sq_norm)
        : 1.0;

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
