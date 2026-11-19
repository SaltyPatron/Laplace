#ifndef LAPLACE_RELATIONS_INTERNAL_H
#define LAPLACE_RELATIONS_INTERNAL_H

#include "postgres.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceRelationSurface
{
    hash128_t   surface;
    hash128_t   canonical;
    const char *name;
    bool        inverse;
    int16       qualifier;
} LaplaceRelationSurface;

extern bool laplace_relation_rank_resolve_c(hash128_t orig_id, double *out_rank);

extern bool laplace_relation_metadata_lookup_c(const hash128_t *surface,
                                               hash128_t *out_canonical,
                                               const char **out_name,
                                               bool *out_inverse,
                                               int16 *out_qualifier);

extern LaplaceRelationSurface *laplace_relation_surfaces_expand_c(
    const hash128_t *ids,
    int n_ids,
    int *out_count);

#endif /* LAPLACE_RELATIONS_INTERNAL_H */
