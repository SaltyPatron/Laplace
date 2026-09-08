#ifndef LAPLACE_CONSENSUS_SCAN_H
#define LAPLACE_CONSENSUS_SCAN_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceConsensusRow
{
    hash128_t subject;
    hash128_t type;
    hash128_t object;
    int64 rating;
    int64 rd;
    int64 witnesses;
    bool object_is_null;
} LaplaceConsensusRow;

typedef struct LaplaceConsensusScanStats
{
    uint64 index_scans;
    uint64 rows_read;
    uint64 rows_matched;
} LaplaceConsensusScanStats;

typedef void (*LaplaceConsensusConsumer)(const LaplaceConsensusRow *, void *);
/* Called only inside one endpoint's descending effective-mu range. Return true
 * only when this row and every lower score cannot change the selected result. */
typedef bool (*LaplaceConsensusCutoff)(const LaplaceConsensusRow *, void *);

/* NULL means unconstrained; an empty array means the empty set. At least one
 * endpoint set is required. Every matching stored cell is visited once, under
 * the caller's active MVCC snapshot. There is no ranking or result limit here. */
extern void laplace_consensus_scan(
    ArrayType *subjects, ArrayType *objects, ArrayType *types,
    LaplaceConsensusConsumer consume, void *context,
    LaplaceConsensusScanStats *stats);

/* The physical DEFAULT partition carries relation types without a named
 * partition. Used by the existing highway-mask neighbor read contract. */
extern void laplace_consensus_scan_default(
    ArrayType *subjects, ArrayType *objects,
    LaplaceConsensusConsumer consume, void *context,
    LaplaceConsensusScanStats *stats);

/* Binary-neighbor projection: the consumer must discard unary cells. This
 * permits using a canonical object-IS-NOT-NULL partial index. Without an exact
 * endpoint/effective-mu index, storage falls back to the complete batch scan. */
extern void laplace_consensus_scan_ranked(
    ArrayType *subjects, ArrayType *objects, ArrayType *types, bool default_only,
    LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff, void *context,
    LaplaceConsensusScanStats *stats);

#endif
