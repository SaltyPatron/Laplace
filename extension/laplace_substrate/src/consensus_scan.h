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
    int64 volatility;
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
/* Called inside the ordered range promised by the selected scan operation.
 * Return true only when this row and every lower score in that same range
 * cannot change the result. Endpoint-wide and exact-type ranges differ. */
typedef bool (*LaplaceConsensusCutoff)(const LaplaceConsensusRow *, void *);

/* NULL means unconstrained; an empty array means the empty set. At least one
 * endpoint set is required. Every matching stored cell is visited once, under
 * the caller's active MVCC snapshot. There is no ranking or result limit here. */
extern void laplace_consensus_scan(
    ArrayType *subjects, ArrayType *objects, ArrayType *types,
    LaplaceConsensusConsumer consume, void *context,
    LaplaceConsensusScanStats *stats);

/* Scan only the DEFAULT partition, which carries relation types without a
 * named partition. */
extern void laplace_consensus_scan_default(
    ArrayType *subjects, ArrayType *objects,
    LaplaceConsensusConsumer consume, void *context,
    LaplaceConsensusScanStats *stats);

/* Binary-neighbor projection: the consumer must discard unary cells, which
 * permits the object-IS-NOT-NULL partial index. Cells arrive per endpoint in
 * descending effective mu, and the cutoff covers that endpoint's whole range
 * across relation types. Without an endpoint/effective-mu index the complete
 * keyed scan is used and the cutoff is not applied. */
extern void laplace_consensus_scan_ranked(
    ArrayType *subjects, ArrayType *objects, ArrayType *types, bool default_only,
    LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff, void *context,
    LaplaceConsensusScanStats *stats);

/* COUPLE's typed response planes: each (endpoint, relation type) range is read
 * in descending effective mu, and the cutoff ends only that range; later types
 * still respond. Types present at an endpoint are discovered by index seeks,
 * so no roster or mask decides which planes exist. Without the typed rank
 * index the complete endpoint-indexed read is used without cutoff. */
extern void laplace_consensus_scan_ranked_planes(
    ArrayType *subjects, ArrayType *objects, ArrayType *types, bool default_only,
    LaplaceConsensusConsumer consume, LaplaceConsensusCutoff cutoff, void *context,
    LaplaceConsensusScanStats *stats);

#endif
