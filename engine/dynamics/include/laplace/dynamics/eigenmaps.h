#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int laplacian_eigenmaps(const double* high_dim_pts,
                        size_t        n,
                        size_t        high_dim,
                        size_t        k_neighbors,
                        size_t        target_dim,
                        double*       low_dim_out);

int laplacian_eigenmaps_from_sparse_graph(const int*    coo_rows,
                                          const int*    coo_cols,
                                          const double* coo_weights,
                                          size_t        nnz,
                                          size_t        n,
                                          size_t        target_dim,
                                          double*       low_dim_out);

#ifdef __cplusplus
}
#endif
