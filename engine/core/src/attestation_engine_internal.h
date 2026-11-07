#ifndef LAPLACE_CORE_ATTESTATION_ENGINE_INTERNAL_H
#define LAPLACE_CORE_ATTESTATION_ENGINE_INTERNAL_H

#include "laplace/core/attestation_engine.h"

int64_t laplace_attestation_unix_us_now(void);

int laplace_attestation_orient_resolved(
    const hash128_t* type_id,
    const hash128_t* subject,
    const hash128_t* object,
    uint8_t          object_is_null,
    hash128_t*       out_subj,
    hash128_t*       out_obj,
    uint8_t*         out_obj_null);

int laplace_attestation_resolved_finish(
    hash128_t subj,
    hash128_t type_id,
    hash128_t obj,
    hash128_t src,
    hash128_t ctx,
    uint8_t obj_null,
    uint8_t ctx_null,
    double witness_weight,
    int64_t score_fp,
    int16_t outcome,
    int64_t observation_count,
    int64_t now_unix_us,
    int64_t sum_score_fp1e9,
    uint8_t is_aggregated,
    laplace_attestation_staged_t* out);

#endif
