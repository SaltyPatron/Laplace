#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct arch_template arch_template_t;

arch_template_t* arch_template_load(const char* template_name);

typedef struct {
    const char* name;
    size_t      rank;
    size_t      shape[8];
    int         dtype;
} tensor_spec_t;

int arch_template_required_tensors(const arch_template_t* tmpl,
                                   const void*            recipe,
                                   tensor_spec_t*         out_specs,
                                   size_t                 cap);

void arch_template_free(arch_template_t* t);

typedef struct {
    const double* per_token_consensus;
    size_t        vocab;
    const int*    per_pair_rows;
    const int*    per_pair_cols;
    const double* per_pair_vals;
    size_t        per_pair_nnz;
    double        norm_aggregate;
    const double* token_basis;
    size_t        basis_dim;
    const double* unary_gram;
    const double* binary_gram;
} substrate_view_t;

int compute_substrate_gram(
    const double* token_basis,
    const double* per_token,
    size_t        vocab,
    size_t        basis_dim,
    const int*    qk_rows,
    const int*    qk_cols,
    const double* qk_vals,
    size_t        nnz,
    double*       unary_gram,
    double*       binary_gram);

int arch_template_materialize_tensor(const arch_template_t*  tmpl,
                                     const tensor_spec_t*    spec,
                                     const substrate_view_t* view,
                                     void*                   out_values);

#ifdef __cplusplus
}
#endif
