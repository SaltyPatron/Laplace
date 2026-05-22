#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Glicko-2 fixed-point state. All math in int64 at scale 1e9 (per ADR 0004). */
typedef struct {
    int64_t rating;
    int64_t rd;
    int64_t volatility;
    int64_t last_observed_at_unix_ns;
    int64_t observation_count;
} glicko2_state_t;

/* Implementations land in Chunk 5. */
void glicko2_init(glicko2_state_t* st, int64_t r0, int64_t rd0, int64_t vol0);
void glicko2_update(glicko2_state_t* st, int64_t score,
                    int64_t source_credibility, int64_t now_ns);
void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns);

#ifdef __cplusplus
}
#endif
