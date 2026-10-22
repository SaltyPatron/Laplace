#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Canonical touched-cell/witness observations. Rating/RD carriers are Q1e9
 * Glicko-1 units, matching laplace.attestations and laplace.consensus. */
typedef struct {
    uint8_t source_id[16],claim_id[16];
    int64_t games,rating,rd,opponent_rating,opponent_rd;
} laplace_source_observation;
typedef struct {
    uint8_t source_id[16];
    uint64_t claims_touched,games;
    double fisher_weight;
    uint32_t share_bp;
} laplace_source_share;
/* Output is lexicographic source-ID order; exact largest remainder settlement
 * sums to 10000. Zero information is an error, never invented equal shares. */
int laplace_source_attribution(const laplace_source_observation*,size_t,
    laplace_source_share*,size_t,size_t*);
#ifdef __cplusplus
}
#endif
