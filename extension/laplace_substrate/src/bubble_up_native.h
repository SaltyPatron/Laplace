#ifndef LAPLACE_BUBBLE_UP_NATIVE_H
#define LAPLACE_BUBBLE_UP_NATIVE_H

#include "postgres.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceBubbleRow
{
    hash128_t   term;
    hash128_t   sense_id;
    hash128_t   synset_id;
    const char *via_relation;
    double      score;
    double      base_eff_mu;
    int64       domain_hits;
    int64       witnesses;
} LaplaceBubbleRow;

extern LaplaceBubbleRow *laplace_bubble_up_batch_c(
    const hash128_t *terms,
    int              n_terms,
    const hash128_t *domain_ctx,
    int              n_domain,
    bool             domain_is_null,
    int32            k,
    bool             k_is_null,
    int             *out_count);

#endif /* LAPLACE_BUBBLE_UP_NATIVE_H */
