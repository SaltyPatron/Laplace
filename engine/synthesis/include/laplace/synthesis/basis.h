#pragma once
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct {
    size_t rank,anchors;
    double orthogonality_error,span_relative_error,frame_orthogonality_error,anchor_error_before,anchor_error_after;
} laplace_basis_receipt;
typedef struct {
    size_t active_vertices,components,requested_modes,retained_modes,combined_rank;
    double eigen_residual_max,mass_orthogonality_error,discarded_svd_energy;
} laplace_spectral_receipt;
/* Basis columns are functions over distinct input operands. QR operates on
 * those functions. Full-dimensional orthogonal Procrustes orients the complete
 * frame against the supplied immutable 4D anchors, retaining the complement.
 * Only the first rank columns change; the last two carry RMS slack and bias.
 * Input maps and canonical coordinates are never changed. */
int laplace_basis_refine(double *embedding,size_t rows,size_t width,size_t rank,
    const double *coordinates,const int *input_operand,laplace_basis_receipt*);
/* Positive graph affinity -> normalized-Laplacian eigenmaps -> combined
 * coordinate/evidence column space. Signed planes remain separate operands. */
int laplace_spectral_embed(size_t,size_t,const int*,const double*,const int*,const int*,const double*,size_t,double*,size_t*,laplace_spectral_receipt*);
#ifdef __cplusplus
}
#endif
