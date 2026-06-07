#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct procrustes_transform procrustes_transform_t;

procrustes_transform_t*
procrustes_fit(const double* source_pts,
               size_t        n,
               size_t        source_dim,
               const double* target_pts);

void
procrustes_apply(const procrustes_transform_t* T,
                 const double*                 source_vec,
                 size_t                        source_dim,
                 double                        out[4]);

double procrustes_residual(const procrustes_transform_t* T);

void   procrustes_free(procrustes_transform_t* T);

#ifdef __cplusplus
}
#endif
