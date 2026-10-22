#pragma once
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct laplace_synthesis_program laplace_synthesis_program;
/* One bulk tensor event. Either name can be NULL when a codec uses a packed
 * versus split representation. Shapes use HF row-major axis order. */
typedef int (*laplace_synthesis_emit)(void*,const char*,const char*,const size_t*,size_t,const float*,size_t);
typedef void (*laplace_synthesis_trace)(void*,size_t,const char*,size_t,double,double);
laplace_synthesis_program *laplace_program_create(size_t vocab,size_t width,const double *embedding,
    int bos,int eos,double epsilon,double rope_base,double rope_scale,double attention_gain,double residual_gain,size_t layers);
int laplace_program_tie_embeddings(laplace_synthesis_program*,int);
/* A finite conventional-softmax floor. bound=0 retains the default readout;
 * otherwise 0<bound<1 is the requested total mass outside the admitted set.
 * equal_candidates selects set membership instead of relative support scores. */
int laplace_program_output_floor(laplace_synthesis_program*,double bound,int equal_candidates);
/* Observed continuations are output targets only when the recipe selects that
 * job. Otherwise sequences supply execution context without replacing claims. */
int laplace_program_observation_readout(laplace_synthesis_program*,int enabled);
int laplace_program_plane(laplace_synthesis_program*,const int*,const int*,const double*,size_t);
/* Operator -1 is causal context attention. -2 explicitly disables a body
 * operator; it does not fabricate evidence or replace a requested operator. */
int laplace_program_layer(laplace_synthesis_program*,size_t heads,size_t kv_heads,size_t intermediate,
    const int *head_operators,int ffn_operator,const int *expert_operators,size_t experts,size_t experts_used);
/* Complete ordered witnessed input: token slots, one occurrence after another.
 * Prefixes share execution state; the end is recorded as EOS. Observations
 * become output targets only under explicit observation-readout selection. */
int laplace_program_sequence(laplace_synthesis_program*,const int*,size_t,double weight);
int laplace_program_compile(laplace_synthesis_program*,int readout_operator,const int *input_operand,
    const int *operand_output,laplace_synthesis_emit,laplace_synthesis_trace,void*);
const char *laplace_program_error(const laplace_synthesis_program*);
const float *laplace_program_states(const laplace_synthesis_program*,size_t *rows,size_t *width);
size_t laplace_program_nodes(const laplace_synthesis_program*,const int**,const int**,const int**);
size_t laplace_program_targets(const laplace_synthesis_program*,const size_t**,const double**,const size_t**,const int**,const double**);
void laplace_program_free(laplace_synthesis_program*);
#ifdef __cplusplus
}
#endif
