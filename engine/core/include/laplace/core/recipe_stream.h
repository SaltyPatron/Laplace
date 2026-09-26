#pragma once
#include <stddef.h>
#include <stdint.h>
#include "laplace/core/intent_stage.h"
#include "laplace/core/ordered_composition.h"
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
 * once through the recipe-selected XML or delimited syntax provider. Both
 * providers recover records for the same field/subject/value executor. */
int laplace_recipe_stream_feed(laplace_recipe_stream_t*, const uint8_t*, size_t, int final);
/* A recipe that declares identity tables (Rcp7) reads the artifact once through
 * prescan before feed: the source's own statements of what its ids denote (a
 * synset id's ILI, a sense id's word) are collected so references resolve to
 * the denoted content. Returns 1 when the recipe declares tables, else 0. */
int laplace_recipe_stream_requires_prescan(const laplace_recipe_stream_t*);
int laplace_recipe_stream_prescan(laplace_recipe_stream_t*, const uint8_t*, size_t, int final);
/* 1: batch produced, 0: needs input/end, negative: failure. Output stage ownership
 * transfers to the caller. Every batch respects the explicit tuple-row envelope.
 * A large source interval resumes at its exact next subject/fact, not by reparsing.
 * Complete entity interpretations travel with the coalesced E/P/A tuples.
 * maximum_bytes bounds the returned native stage; parser/recipe state and
 * bounded coalescing buffers are separate working memory. records_completed is
 * the number of physical records completed by this batch, not a cumulative count. */
int laplace_recipe_stream_drain(laplace_recipe_stream_t*, size_t maximum_rows,
    size_t maximum_bytes, intent_stage_t** out, uint64_t* records_completed);
/* The metadata tree of the file this stream reads (its path and digest, composed by
 * the caller). Set before feed. After the last record the stream composes the file's
 * trunk: [metadata tree, every distinct content the file states in first-seen order],
 * so each content is a direct child of the file that witnessed it. */
int laplace_recipe_stream_set_file(laplace_recipe_stream_t*, const laplace_ordered_component_t* head);
/* 1 and the file trunk (id, coord, tier) once the final drain composed it, 0 before,
 * -1 on bad input. */
int laplace_recipe_stream_file_root(const laplace_recipe_stream_t*, laplace_ordered_component_t* out);
const char* laplace_recipe_stream_error(const laplace_recipe_stream_t*);
void laplace_recipe_stream_free(laplace_recipe_stream_t*);
#ifdef __cplusplus
}
#endif
