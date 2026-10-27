#pragma once
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Construct a positive weighted transfer in a one-hot operand basis.
 * Token realization and output realization are explicit ordinal maps; they
 * never change the content identifiers. Duplicate COO entries are summed.
 * Empty rows terminate at eos. Returns -1 for invalid dimensions/maps,
 * -2 for allocation failure, -3 for negative/nonfinite input weights. */
int laplace_lookup_compile(size_t vocab, size_t dim, int eos, double rms_epsilon,
    const int *input_operand, const int *operand_output,
    const int *rows, const int *cols, const double *weights, size_t nnz,
    double *embedding, double *output);
int laplace_readout_embed(size_t,size_t,const int*,const int*,const int*,const int*,const double*,size_t,const double*,double*,size_t*);
int laplace_readout_embed_policy(size_t,size_t,const int*,const int*,const int*,const int*,const double*,size_t,const double*,double*,size_t*,double,int);
int laplace_graph_basis(const int*,const int*,const double*,size_t,size_t,size_t,double*,size_t*);
int laplace_operator_compile(const double*,size_t,size_t,const int*,const int*,const double*,size_t,
    double*,double*,size_t*,double*,double*);
int laplace_operator_compile_constant(const double*,size_t,size_t,const int*,const int*,const double*,size_t,double*,double*,size_t*,double*,double*,double*);
int laplace_operator_apply(const double*,size_t,size_t,const double*,double,double*);
int laplace_readout_fit(const float*,size_t,size_t,const int*,const int*,const int*,const int*,const double*,size_t,
    float*,size_t*,double*,double*);
int laplace_readout_measure(const float*,size_t,size_t,const int*,const int*,const int*,const int*,const double*,size_t,float*,size_t*,double*,double*);
#ifdef __cplusplus
}
#endif
