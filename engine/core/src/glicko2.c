#include "laplace/core/glicko2.h"

/* Real implementation lands Chunk 5 — Glicko-2 update equations per ADR 0004
 * (int64 fixed-point, scale 1e9; deterministic, vectorizable). Arena/source-
 * trust semantics per the rule referenced as Hard Rule 9 in CLAUDE.md. Stubs
 * satisfy linkage. */

void glicko2_init(glicko2_state_t* st, int64_t r0, int64_t rd0, int64_t vol0) {
    if (!st) return;
    st->rating = r0;
    st->rd = rd0;
    st->volatility = vol0;
    st->last_observed_at_unix_ns = 0;
    st->observation_count = 0;
}

void glicko2_update(glicko2_state_t* st, int64_t score,
                    int64_t source_credibility, int64_t now_ns) {
    (void)score; (void)source_credibility;
    if (st) {
        st->last_observed_at_unix_ns = now_ns;
        st->observation_count += 1;
    }
}

void glicko2_decay_rd_in_place(glicko2_state_t* st, int64_t now_ns) {
    (void)now_ns;
    (void)st;
}
