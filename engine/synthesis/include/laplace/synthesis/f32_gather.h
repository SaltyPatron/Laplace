#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int laplace_f32_gather_to_f64(
    const float* src,
    const int*   row_map,
    size_t       n_rows,
    size_t       d,
    double*      out);

#ifdef __cplusplus
}
#endif
