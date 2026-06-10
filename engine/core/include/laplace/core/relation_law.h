#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    LAPLACE_REL_SYMMETRY_ASYMMETRIC = 0,
    LAPLACE_REL_SYMMETRY_SYMMETRIC   = 1,
} laplace_rel_symmetry_t;

typedef struct {
    const char*     canonical;
    hash128_t       type_id;
    double          rank;
    laplace_rel_symmetry_t symmetry;
    int16_t         parent_idx;   /* -1 if none */
    int16_t         family_root_idx;
    uint8_t         flip;         /* alias-only; canonical entries 0 */
} laplace_relation_def_t;

typedef struct {
    const char* surface;
    int16_t     canon_idx;
    uint8_t     flip;
} laplace_relation_alias_t;

extern const laplace_relation_def_t* laplace_relation_table;
extern const size_t laplace_relation_table_count;

extern const laplace_relation_alias_t* laplace_relation_alias_table;
extern const size_t laplace_relation_alias_table_count;

int laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id);
int laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def);
int laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out);

int laplace_relation_resolve_deprel(const char* deprel, hash128_t* out_type_id,
                                    double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                    uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_enhanced_deprel(const char* deprel, hash128_t* out_type_id,
                                             double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                             uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_feature(const char* feature_name, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id);

#ifdef __cplusplus
}
#endif
