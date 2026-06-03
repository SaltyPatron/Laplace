#include "laplace/core/astar.h"

#include <stddef.h>

/* Real implementations land Chunk 5+ — compiled cascade,
 * Glicko-2-weighted edge costs. Stubs satisfy linkage. */

struct astar_query {
    int _placeholder;  /* opaque type body */
};

astar_query_t* astar_open(const hash128_t* start,
                          const hash128_t* goal_region,
                          size_t max_depth,
                          size_t k_paths) {
    (void)start; (void)goal_region; (void)max_depth; (void)k_paths;
    return NULL;
}

bool astar_next(astar_query_t* q, astar_step_t* out_step) {
    (void)q; (void)out_step;
    return false;
}

void astar_close(astar_query_t* q) {
    (void)q;
}
