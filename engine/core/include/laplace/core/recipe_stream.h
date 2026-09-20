#pragma once
#include <stddef.h>
#include <stdint.h>
#include "laplace/core/intent_stage.h"
#ifdef __cplusplus
extern "C" {
#endif
typedef struct laplace_recipe_stream laplace_recipe_stream_t;
/* A compiled recipe is a versioned, little-endian instruction image. The
 * boundary compiles configuration once; this engine owns parsing/value loops.
 * No dataset names or property inventories belong in this implementation. */
int laplace_recipe_stream_new(const uint8_t* program, size_t program_bytes,
    const hash128_t* witness, double trust, laplace_recipe_stream_t** out);
/* Feed is legal only after all prior output was drained. Bytes are consumed
 * once through the registered streaming XML provider. */
int laplace_recipe_stream_feed(laplace_recipe_stream_t*, const uint8_t*, size_t, int final);
/* 1: batch produced, 0: needs input/end, negative: failure. Output stage ownership
 * transfers to the caller. Every batch respects the explicit tuple-row envelope.
 * A large source interval resumes at its exact next subject/fact, not by reparsing.
 * Complete entity interpretations travel with the coalesced E/P/A tuples.
 * maximum_bytes bounds the returned native stage; parser/recipe state and
 * bounded coalescing buffers are separate working memory. records_completed is
 * the number of physical records completed by this batch, not a cumulative count. */
int laplace_recipe_stream_drain(laplace_recipe_stream_t*, size_t maximum_rows,
    size_t maximum_bytes, intent_stage_t** out, uint64_t* records_completed);
const char* laplace_recipe_stream_error(const laplace_recipe_stream_t*);
void laplace_recipe_stream_free(laplace_recipe_stream_t*);
#ifdef __cplusplus
}
#endif
