#include "laplace/dynamics/gram_schmidt.h"

#include <Eigen/Core>
#include <Eigen/QR>
#include <cstddef>
#include <cstring>

/* Orthonormalize n_vecs row-major vectors of length `dim` in place.
 *
 * Backed by Eigen's `HouseholderQR`, which is the numerically-stable
 * Householder reflections approach to QR (classical and modified
 * Gram-Schmidt both lose orthogonality on ill-conditioned input bases;
 * Householder QR is backward-stable). This is Eigen's QR; it is single-threaded
 * deterministic, so the orthonormal basis is reproducible regardless of the
 * MKL_CBWR lock. (We do not claim a specific LAPACKE dispatch — Eigen's MKL
 * backend coverage for HouseholderQR is version-dependent; correctness and
 * determinism hold either way.)
 *
 * Input layout: `vectors` is (n_vecs * dim) doubles, row-major — row i
 * is the i-th vector at offset (i * dim). After successful return, the
 * rows of `vectors` form an orthonormal basis of the same row space
 * (within the rank of the input).
 *
 * The implementation maps the input as a (dim × n_vecs) column-major
 * Eigen matrix (each column is one input vector, since row-major
 * (n_vecs × dim) == col-major (dim × n_vecs)), then QR-decomposes
 * to extract the orthonormal Q factor's first n_vecs columns. Those
 * columns are the orthonormalized vectors, written back into the
 * same memory.
 *
 * Returns:
 *   0  on success.
 *  -1  on null input.
 *  -2  if n_vecs > dim (cannot have more orthonormal vectors than the
 *      space dimension).
 *  -3  if the input is rank-deficient (some vector lies in the span of
 *      the others; QR succeeds but Q's columns won't span the input).
 *  -4  if QR decomposition's diagonal has a near-zero entry (numerical
 *      rank deficiency).
 */

extern "C"
int gram_schmidt_orthonormalize(double* vectors,
                                std::size_t n_vecs,
                                std::size_t dim) {
    if (!vectors)            return -1;
    if (n_vecs == 0 || dim == 0) return 0;  /* nothing to do */
    if (n_vecs > dim)        return -2;

    /* Map row-major (n_vecs × dim) as col-major (dim × n_vecs):
     * row i of input = column i of the Eigen matrix. */
    using ColMajMat = Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::ColMajor>;
    Eigen::Map<ColMajMat> M(vectors,
                            static_cast<Eigen::Index>(dim),
                            static_cast<Eigen::Index>(n_vecs));

    Eigen::HouseholderQR<ColMajMat> qr(M);

    /* Detect numerical rank deficiency: any near-zero diagonal entry in R
     * means some input vector is (numerically) linearly dependent. The
     * threshold scales with the input's largest magnitude. */
    const ColMajMat R = qr.matrixQR().triangularView<Eigen::Upper>();
    const double scale = R.diagonal().cwiseAbs().maxCoeff();
    const double tol = std::max(static_cast<double>(dim), static_cast<double>(n_vecs))
                       * scale * std::numeric_limits<double>::epsilon();
    for (Eigen::Index i = 0; i < R.cols(); ++i) {
        if (std::abs(R(i, i)) <= tol) return -4;
    }

    /* Extract Q's first n_vecs columns — the orthonormal basis. */
    ColMajMat Q = qr.householderQ() * ColMajMat::Identity(static_cast<Eigen::Index>(dim),
                                                          static_cast<Eigen::Index>(n_vecs));

    /* Fix sign so that R(i,i) > 0 — convention for unique QR. */
    for (Eigen::Index i = 0; i < Q.cols(); ++i) {
        if (R(i, i) < 0.0) Q.col(i) *= -1.0;
    }

    /* Write back: column i of Q → row i of vectors. */
    for (std::size_t i = 0; i < n_vecs; ++i) {
        for (std::size_t j = 0; j < dim; ++j) {
            vectors[i * dim + j] = Q(static_cast<Eigen::Index>(j),
                                     static_cast<Eigen::Index>(i));
        }
    }
    return 0;
}
