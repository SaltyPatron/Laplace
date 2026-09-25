#pragma once

/*
 * Per-source reduction and rating-period fold of native intent stages.
 *
 * A source's native stream emits entities, physicalities and claims in many
 * transport batches. The reducer owns the order of operations that follows:
 *
 *   1. reduce: one row per entity id and physicality id (first occurrence), one
 *      row per attestation id (games and score sums added, qualifier masks OR-ed,
 *      the outcome classified from the merged totals, the latest observation
 *      retained);
 *   2. masks: every claimed relation's Highway bit OR-ed into its subject's and
 *      object's mask, resolved through the loaded Highway registry. OR is idempotent,
 *      so a claim that turns out to be a replay changes nothing; the masks ride on
 *      the entity rows, and entities the claims name without staging get a mask row;
 *   3. route: every row to one of N lanes by the PostgreSQL HASH partition of its
 *      storage key (entities and physicalities by id, claims and their consensus
 *      cells by subject) as sorted binary COPY streams, so each lane is one
 *      set-level write into partitions no other lane touches;
 *   4. fold: one Glicko-2 rating period per witness per typed cell, applied to the
 *      cell's prior standing, which PostgreSQL supplies once per cell.
 *
 * Standing math is glicko2_fold_uniform_period / glicko2_fold_grouped_period,
 * exactly as the consensus fold defines it. Relation rank never enters the fold.
 *
 * Every function returns 0 on success and a negative value on failure; the
 * reducer's last error message is available through laplace_source_reduce_error.
 * Buffers returned by the reducer remain owned by it and stay valid until the
 * next call that rebuilds the same stream or until the reducer is freed.
 */

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct laplace_source_reduce laplace_source_reduce_t;

typedef enum {
    LAPLACE_SOURCE_REDUCE_ENTITIES      = 1, /* id, tier, type_id, placed, highway_mask */
    LAPLACE_SOURCE_REDUCE_PHYSICALITIES = 2, /* the physicalities COPY columns */
    LAPLACE_SOURCE_REDUCE_ATTESTATIONS  = 3, /* the attestations COPY columns */
    LAPLACE_SOURCE_REDUCE_CELL_KEYS     = 4, /* id, subject_id, type_id */
    LAPLACE_SOURCE_REDUCE_CONSENSUS     = 5, /* novel cells: id, subject_id, type_id, object_id, rating,
                                                rd, volatility, witness_count, last_observed_at */
    LAPLACE_SOURCE_REDUCE_MASKS         = 6, /* claimed entities outside the reduction: id, highway_mask */
    LAPLACE_SOURCE_REDUCE_STANDING      = 7, /* cells with prior standing, same columns; witness_count
                                                is this source's games, added to the stored count */
} laplace_source_reduce_stream_t;

typedef struct {
    uint64_t entities;          /* distinct entity rows */
    uint64_t physicalities;     /* distinct physicality rows */
    uint64_t attestations;      /* distinct attestation rows */
    uint64_t staged_entities;   /* rows received before reduction */
    uint64_t staged_physicalities;
    uint64_t staged_attestations;
    uint64_t merged_attestations; /* attestation ids received more than once */
    uint64_t unplaced_entities; /* entities without a physicality of their own in the reduction */
    uint64_t observations;      /* games folded so far */
    uint64_t cells;             /* consensus cells folded so far */
    uint64_t bytes;             /* retained row bytes */
    uint64_t masked_entities;   /* claimed entities outside the reduction whose masks update */
} laplace_source_reduce_stats_t;

laplace_source_reduce_t* laplace_source_reduce_new(void);
void laplace_source_reduce_free(laplace_source_reduce_t* reducer);
const char* laplace_source_reduce_error(const laplace_source_reduce_t* reducer);

/* Absorbs a stage's entity, physicality and attestation rows. The stage is only
 * read; the caller keeps ownership. Rejects non-replayable claims: a categorical
 * receipt needs its transient score and cannot be folded from the row alone. */
int laplace_source_reduce_add_stage(laplace_source_reduce_t* reducer, const intent_stage_t* stage);

/* Rows of these relation types are stored but never folded or masked (operational edges). */
int laplace_source_reduce_exclude_fold_types(laplace_source_reduce_t* reducer,
                                             const hash128_t* type_ids, size_t count);

/* Declares the HASH partition modulus of the entity, physicality and claim tables
 * (0 = not HASH partitioned on the routing key). A lane then owns whole partitions:
 * PostgreSQL's own partition routing (hash_bytes_extended with HASH_PARTITION_SEED,
 * hash_combine64, modulo the greatest modulus) decides a row's partition, and
 * partition % lanes its lane, so no two lanes write the same index trees. */
int laplace_source_reduce_partitions(laplace_source_reduce_t* reducer, uint32_t entity_modulus,
                                     uint32_t physicality_modulus, uint32_t claim_modulus);

/* Freezes the reduction, derives the entity masks and routes every row to one of
 * `lanes` lanes. No stage may be added afterwards. */
int laplace_source_reduce_route(laplace_source_reduce_t* reducer, uint32_t lanes);

void laplace_source_reduce_stats(const laplace_source_reduce_t* reducer,
                                 laplace_source_reduce_stats_t* out);

/* Sorted binary COPY stream (header, rows, trailer) of one lane. `rows` receives
 * the row count. Entity, physicality, attestation and mask streams exist after
 * routing; cell-key streams after the lane's cell call; consensus and standing
 * streams after its fold. */
const uint8_t* laplace_source_reduce_stream(laplace_source_reduce_t* reducer,
                                            laplace_source_reduce_stream_t stream,
                                            uint32_t lane, size_t* out_len, uint64_t* rows);

/* Restricts a lane's fold to the claims PostgreSQL admitted as novel. `novel` is a
 * binary COPY stream of one bytea column (attestation ids), header included; NULL with
 * length 0 admits every claim of the lane again. Without this call every claim of the
 * lane is novel. A claim already stored is a replay of an observation that was folded
 * when it was first admitted. */
int laplace_source_reduce_admit(laplace_source_reduce_t* reducer, uint32_t lane,
                                const uint8_t* novel, size_t len);

/* Builds the lane's typed cells from its admitted claims and the cell-key stream
 * used to read their prior standing. Returns the number of cells through `cells`. */
int laplace_source_reduce_cells(laplace_source_reduce_t* reducer, uint32_t lane, uint64_t* cells);

/* Folds the lane's cells. `priors` is a binary COPY stream (id, subject_id, rating,
 * rd, volatility) of the cells that already have standing, header included; every
 * other cell starts from the neutral prior. One rating period per witness per cell,
 * witnesses in id order. Produces the lane's consensus (novel cells) and standing
 * (cells with prior standing) streams. */
int laplace_source_reduce_fold(laplace_source_reduce_t* reducer, uint32_t lane,
                               const uint8_t* priors, size_t len);

/* Claimed (entity, relation type) pairs whose relation has no Highway bit, for the
 * dynamic-family resolver. Valid after routing. */
int laplace_source_reduce_masks(laplace_source_reduce_t* reducer,
                                const hash128_t** unresolved_pairs, size_t* unresolved_count);

#ifdef __cplusplus
}
#endif
