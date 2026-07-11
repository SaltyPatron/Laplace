#include "laplace/dynamics/gram_schmidt.h"

#include <Eigen/Core>
#include <Eigen/QR>
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>

extern "C"
int gram_schmidt_orthonormalize(double* vectors,
                                std::size_t n_vecs,
                                std::size_t dim) {
    if (!vectors)            return -1;
    if (n_vecs == 0 || dim == 0) return 0;
    if (n_vecs > dim)        return -2;

    using ColMajMat = Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::ColMajor>;
    Eigen::Map<ColMajMat> M(vectors,
                            static_cast<Eigen::Index>(dim),
                            static_cast<Eigen::Index>(n_vecs));

    Eigen::HouseholderQR<ColMajMat> qr(M);

    const ColMajMat R = qr.matrixQR().triangularView<Eigen::Upper>();
    const double scale = R.diagonal().cwiseAbs().maxCoeff();
    const double tol = std::max(static_cast<double>(dim), static_cast<double>(n_vecs))
                       * scale * std::numeric_limits<double>::epsilon();
    for (Eigen::Index i = 0; i < R.cols(); ++i) {
        if (std::abs(R(i, i)) <= tol) return -4;
    }

    ColMajMat Q = qr.householderQ() * ColMajMat::Identity(static_cast<Eigen::Index>(dim),
                                                          static_cast<Eigen::Index>(n_vecs));

    for (Eigen::Index i = 0; i < Q.cols(); ++i) {
        if (R(i, i) < 0.0) Q.col(i) *= -1.0;
    }

    for (std::size_t i = 0; i < n_vecs; ++i) {
        for (std::size_t j = 0; j < dim; ++j) {
            vectors[i * dim + j] = Q(static_cast<Eigen::Index>(j),
                                     static_cast<Eigen::Index>(i));
        }
    }
    return 0;
}
