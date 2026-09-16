#ifndef LAPLACE_GENERATED_STAGE_SINK_H
#define LAPLACE_GENERATED_STAGE_SINK_H

#include "postgres.h"
#include "laplace/core/intent_stage.h"

typedef struct LaplaceGeneratedStageSinkLimits {
    size_t maximum_rows;
    size_t maximum_bytes;
    size_t maximum_logical_occurrences;
    uint32 maximum_operations;
} LaplaceGeneratedStageSinkLimits;

typedef struct LaplaceGeneratedStageSinkReceipt {
    size_t input_rows[3];
    size_t distinct_rows[3];
    size_t inserted_rows[3];
    size_t folded_cells;
    int64 folded_observations;
    size_t mask_pairs;
    int64 mask_rows;
    size_t tuple_bytes;
    size_t logical_work;
    size_t stored_vertices;
    size_t peak_reserved_bytes;
    /* Actual sink SPI prepares + executions. Existing consensus/mask owners'
     * internal SPI operations are outside this counter; their input cardinality
     * is bounded above by inserted attestations. This includes the sink's
     * reentrant writer lock. A caller's earlier explicit lock remains separate. */
    uint32 operations;
} LaplaceGeneratedStageSinkReceipt;

/* Call before locking a session row. The existing shared apply lock is
 * transaction scoped and reentrant. This operation owns a nested SPI frame.
 * READ COMMITTED is required; callers pin provider snapshots AFTER this lock. */
void laplace_generated_stage_sink_lock(void);

/* Persist native-generated source/vocabulary/descriptor Content stages and
 * replayable HAS_PHYSICALITY evidence. This is not an arbitrary ingest API.
 * The stages remain borrowed. Duplicate exact witnesses are replay, never
 * additive evidence. Only INSERT RETURNING's accepted attestations are folded.
 * Existing native consensus and mask owners execute within this transaction.
 *
 * maximum_bytes caps sink-owned allocations, native body export, and reserved
 * transport/returned-tuple bytes. PostgreSQL executor/shared buffers, retained
 * plans, existing consensus/mask owner allocations, caller-owned stages and
 * allocator bookkeeping are outside this receipt. peak_reserved_bytes is a
 * conservative allocation reservation, not measured total backend memory.
 * Errors abort the caller transaction; no independent commit or retry occurs.
 * out_receipt publishes only on success. */
void laplace_generated_stage_sink(const intent_stage_t *const *stages,
    size_t stage_count, const LaplaceGeneratedStageSinkLimits *limits,
    LaplaceGeneratedStageSinkReceipt *out_receipt);

#endif
