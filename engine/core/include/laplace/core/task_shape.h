#pragma once

#include <stddef.h>
#include "laplace/core/hash128.h"
#include "laplace/core/ud_parse.h"

#ifdef __cplusplus
extern "C" {
#endif

/* A source-declared relation-read shape, not an ISA opcode or a language cue.
 * Canonical Document-tier trajectory:
 * [schema, exemplar_parse, predicate, (slot_id, token_ref, entity_type)*, end].
 * slot_id = Merkle(4, [slot_schema, token_ref, entity_type]).
 * The schema permits one complete UD token per variable slot and requires an
 * immutable lexical token. No span, subtree, negation or role is discarded. */
typedef struct {
    hash128_t schema, slot_schema, end;
} laplace_task_shape_markers_t;

typedef struct {
    hash128_t exemplar_parse, predicate;
    const hash128_t *slots; /* borrowed ordered triples */
    size_t slot_count;
} laplace_task_shape_t;

void laplace_task_shape_markers_init(laplace_task_shape_markers_t *out);
/* 0 success; -1 unsupported schema; -2 malformed; -3 allocation failure. */
int laplace_task_shape_decode(const hash128_t *flat, size_t count,
                              laplace_task_shape_t *out);

/* Exact structure unification. Returns 1 for a complete match, 0 for a mismatch,
 * -1 for invalid arguments/allocation. slot_ordinals receives the current token
 * ordinal for each declared slot. Inputs must be complete validated UD parses.
 * Only declared token form/lemma fields may vary; refs are compared by logical
 * token ordinal, never by coincidentally equal source-local ref IDs. */
int laplace_task_shape_match(const laplace_task_shape_t *shape,
    const laplace_ud_parse_t *exemplar, const laplace_ud_parse_t *current,
    size_t *slot_ordinals);

/* Instantiate the explicitly declared token pattern against a novel complete
 * occurrence sequence. This produces a hypothesis, not an observed UD parse.
 * Every non-slot form and every token position must match. The exemplar must
 * still have complete rooted topology. A caller with current parse evidence
 * additionally constrains this projection using laplace_task_shape_match. */
int laplace_task_shape_match_forms(const laplace_task_shape_t *shape,
    const laplace_ud_parse_t *exemplar, const hash128_t *forms, size_t form_count,
    size_t *slot_ordinals);

#ifdef __cplusplus
}
#endif
