#pragma once

#include "postgres.h"

#include "utils/array.h"

/*
 * Batch presence probes for ingest convergence. Bit i of the caller-zeroed
 * bitmap is set only when candidate i is positively confirmed present (a
 * perfcache codepoint resolution or a stored row); no id is assumed present.
 * Trunk-to-leaf descent is the caller's: it probes one tier per round and
 * omits descendants of nodes already present, since a present composition's
 * whole subtree is present.
 */

/* Which ids resolve as entities: perfcache codepoints count as present without
 * a read; the rest are probed as stored rows. */
int laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Same presence semantics as laplace_entities_present_bitmap; the entry point
 * for one round of trunk-to-leaf descent. */
int laplace_tier_batch_existence_probe(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Presence of attestation ids. An id alone cannot prune the HASH(subject_id)
 * partitioning, so each id is routed by its subject to the one leaf that can
 * hold it. type_ids is length-checked with the other arrays. No perfcache path:
 * attestation ids are never codepoint ids. */
int laplace_attestations_present_bitmap_keyed(ArrayType *ids_array, ArrayType *type_ids_array,
                                              ArrayType *subject_ids_array,
                                              uint8_t *bm, int candidate_count);

/* Presence of physicality rows by their own id. A physicality can be staged
 * for an entity already stored, so physicality presence is never inferred from
 * entity presence. */
int laplace_physicalities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Whether a committed entities row exists, with the perfcache path off.
 * Resolvability (entities_present_bitmap) counts codepoints as present; the
 * write path's in-transaction verification decides what to write, and tier-0
 * codepoint rows are themselves written through it. */
int laplace_entities_stored_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Keyed forms carry the partition key parallel to the ids so each probe prunes
 * to one index descent per id: entities by tier (LIST; tier 2 also HASH(id)),
 * physicalities by hilbert_index (RANGE). Perfcache: off for stored-row
 * semantics, on for descent resolvability, never for physicalities. */
int laplace_entities_stored_bitmap_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                         uint8_t *bm, int candidate_count);
int laplace_tier_batch_existence_probe_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                             uint8_t *bm, int candidate_count);
int laplace_physicalities_present_bitmap_keyed(ArrayType *ids_array, ArrayType *hilberts_array,
                                               uint8_t *bm, int candidate_count);
