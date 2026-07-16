#pragma once

#include "postgres.h"

#include "utils/array.h"

/*
 * Batch existence probe primitives.
 *
 * Both functions below share the same safe semantics: the caller-supplied
 * bitmap `bm` is assumed pre-zeroed ("unknown"/"absent" by default), and a
 * bit is ONLY ever set when a real, positive confirmation of presence was
 * obtained (perfcache codepoint match, or a real SPI batch query against
 * `entities`). Neither function ever assumes presence and neither ever
 * short-circuits a whole subtree based on an unconfirmed default -- that
 * "assume-present, clear-on-disproof" scheme is exactly the shape of bug
 * that previously lived in this file's tree-walking
 * laplace_content_descent_bitmap_core() (removed) and, independently, in
 * TierTreeDescent.cs's now-fixed unconditional MarkProven() call on the C#
 * side. Presence is only ever asserted from a real query result.
 *
 * Trunk-to-leaf, tier-by-tier descent (do we need to check this node's
 * children at all?) is deliberately NOT implemented here anymore. It is
 * driven entirely by the C# orchestrator (TierTreeDescent.cs), which calls
 * laplace_tier_batch_existence_probe() once per tier/round with exactly the
 * candidate ids for that round (already filtered to exclude descendants of
 * nodes proven present in an earlier round, per the content-addressing
 * guarantee that a present node's whole subtree is present too). Keeping
 * the tree-walk in C# rather than recursing inside a single SPI call keeps
 * each round a clean, auditable batch query and lets the caller decide when
 * to stop descending -- rather than baking that policy into native code.
 */

/* Used by entities_exist_bitmap(ids bytea[]) -- general-purpose "which of
 * these ids have a committed entities row" check, independent of any
 * tier-tree/parent structure. */
int laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Used by tier_batch_existence_probe(ids bytea[]) -- the tier-by-tier
 * descent primitive. Functionally identical presence semantics to
 * laplace_entities_present_bitmap (same shared implementation), exposed
 * under its own name/SQL entry point so the ingest-descent call sites are
 * self-documenting about *why* they're calling it (one round of a
 * trunk-to-leaf batch probe) rather than incidentally reusing a
 * general-purpose existence check. */
int laplace_tier_batch_existence_probe(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Used by attestations_exist_bitmap(ids, type_ids, subject_ids) -- same
 * positive-confirmation-only semantics against `attestations`. No perfcache
 * fast path (attestation ids are never codepoint ids by construction).
 * KEYED: attestations are partitioned LIST(type_id) -> HASH(subject_id) and
 * `id` is not a partition key, so an id-only probe cannot prune -- every id
 * paid one index descent per leaf (~145x read amplification; measured
 * 2026-07-16 growing 1.2s -> 165s as the substrate filled). The caller
 * computed each id FROM (subject, type, object, source, context), so it
 * always holds the partition keys -- passing them restores one pruned
 * descent per id. Arrays are parallel and length-checked by the SQL entry
 * point. */
int laplace_attestations_present_bitmap_keyed(ArrayType *ids_array, ArrayType *type_ids_array,
                                              ArrayType *subject_ids_array,
                                              uint8_t *bm, int candidate_count);

/* Used by physicalities_exist_bitmap(ids bytea[]) -- positive-confirmation
 * presence against `physicalities` by the row's OWN id. A physicality may
 * legitimately be staged for an already-stored entity (projections /
 * building blocks arrive after the entity), so the write lane must never
 * infer physicality presence from entity presence. */
int laplace_physicalities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/* Used by entities_stored_bitmap(ids bytea[]) -- "is there a committed
 * entities ROW", with the perfcache codepoint fast path deliberately OFF.
 * entities_exist_bitmap answers resolvability (perfcache ids count as
 * present without a table read), which is right for dedup/descent but wrong
 * for the write lane's in-transaction verification: that probe decides what
 * gets written, and tier-0 rows only exist because the unicode seed writes
 * them through this very lane. */
int laplace_entities_stored_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);
