#ifndef LAPLACE_SUBSTRATE_FOLD_ROUTE_INTERNAL_H
#define LAPLACE_SUBSTRATE_FOLD_ROUTE_INTERNAL_H

#include "postgres.h"

#include "access/table.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "miscadmin.h"
#include "partitioning/partbounds.h"
#include "hash_leaves.h"
#include "partitioning/partdesc.h"
#include "storage/fd.h"
#include <inttypes.h>
#ifdef _WIN32
#include <windows.h>
#endif
#include "utils/array.h"
#include "utils/acl.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/resowner.h"
#include "utils/rls.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/sql_catalog.h"
#include "consensus_fold_math.h"
#include "consensus_bulk_write.h"

static inline hash128_t
strand_id(const uint8_t *subject, const uint8_t *type, const uint8_t *object)
{
    hash128_t vertices[3], id;
    memcpy(vertices, subject, 16);
    memcpy(vertices + 1, type, 16);
    if (object) memcpy(vertices + 2, object, 16);
    hash128_merkle(0, vertices, object ? 3 : 2, &id);
    return id;
}

typedef struct TypePlanEntry
{
    char       type_id[16];
    SPIPlanPtr plan;
} TypePlanEntry;

typedef struct PriorRouteEntry
{
    char        type_id[16];
    const LaplaceHashLeaves *leaves;
    SPIPlanPtr *leaf_plans;    /* per leaf remainder */
    SPIPlanPtr *update_plans;  /* per leaf remainder */
} PriorRouteEntry;

typedef struct InArray
{
    ArrayType *array;          /* original detoasted argument; reusable whole */
    Datum *elems;
    bool  *nulls;
    int    n;
} InArray;

typedef struct FoldStateArrays
{
    ArrayType *seen_array;
    ArrayType *rating_array;
    ArrayType *rd_array;
    ArrayType *volatility_array;
} FoldStateArrays;

typedef struct FoldPriorStates
{
    bool   *matched;
    Datum  *ratings;
    Datum  *rds;
    Datum  *volatilities;
    int     n;
    uint64  matched_n;
    int    *leaves;     /* exact HASH leaf remainder of each row */
} FoldPriorStates;

typedef struct PriorLeafBatch
{
    Datum *ids;
    Datum *subjects;
    int   *positions;
    int    n;
    int    fill;
} PriorLeafBatch;

typedef struct FoldMemo
{
    int64 input[7]; /* rating, rd, volatility, opponent, phi, games, score sum */
    glicko2_state_t result;
} FoldMemo;

typedef struct PeriodArrays
{
    InArray offsets; /* zero-based, one entry per cell plus terminal */
    InArray opponents;
    InArray phis;
    InArray games;
    InArray sums;
    bool exact;
} PeriodArrays;

typedef struct PeriodWindow
{
    ArrayType *starts;
    ArrayType *ends;
    ArrayType *opponents;
    ArrayType *phis;
    ArrayType *games;
    ArrayType *sums;
} PeriodWindow;

typedef struct CellSeen
{
    char id[16];
} CellSeen;

typedef struct FoldEvidenceStates
{
    bool *recomputed;
    Datum *counts;
    Datum *timestamps;
} FoldEvidenceStates;

/* fold_route_plans.c */
SPIPlanPtr laplace_fold_typed_plan(HTAB **slot, const char *name,
                                   const uint8_t *type16,
                                   const char *template_sql,
                                   int nargs, const Oid *argtypes);
PriorRouteEntry *laplace_fold_prior_route(const uint8_t *type16,
                                          Datum type_datum,
                                          const char *label);
SPIPlanPtr laplace_fold_update_leaf_plan(PriorRouteEntry *route,
                                         int remainder,
                                         const char *label);
void laplace_fold_in_array(FunctionCallInfo fcinfo, int argno, Oid elmtype,
                           int elmlen, bool elmbyval, char elmalign,
                           bool allow_nulls, const char *label, InArray *out);
const uint8_t *laplace_fold_bytea16(Datum d, const char *label);
ArrayType *laplace_fold_neutral_opponent_array(int n);
ArrayType *laplace_fold_array_window(ArrayType *original, const Datum *src,
                                     const bool *src_nulls, int total,
                                     int start, int n, Oid elmtype,
                                     int elmlen, bool elmbyval, char elmalign);
InArray laplace_fold_in_array_window(const InArray *source, int start, int n,
                                     Oid elmtype, int elmlen,
                                     bool elmbyval, char elmalign);
FoldPriorStates *laplace_fold_prior_states_create(int n);
FoldPriorStates *laplace_fold_read_run_priors(const uint8_t *type16,
                                              Datum type_datum,
                                              const Datum *cell_ids,
                                              const InArray *subjects,
                                              int run_start, int run_n,
                                              const char *label);

/* fold_route_states.c */
void laplace_fold_read_period_arrays(FunctionCallInfo fcinfo, int cell_count,
                                     const char *label, PeriodArrays *periods);
PeriodWindow laplace_fold_period_window(const PeriodArrays *periods,
                                        const InArray *cell_opponents,
                                        const InArray *cell_phis,
                                        const InArray *cell_games,
                                        const InArray *cell_sums,
                                        int cell_start, int cell_n);
void laplace_fold_run_states(const InArray *phis, const InArray *opps,
                             const InArray *games, const InArray *sums,
                             const PeriodArrays *periods,
                             int run_start, int run_n,
                             const FoldPriorStates *priors,
                             const char *label, FoldStateArrays *out,
                             const bool *recomputed);
Datum *laplace_fold_typed_cell_ids(const uint8_t *type16,
                                   const InArray *subjects,
                                   const InArray *objects,
                                   const char *label);

/* fold_route_evidence.c */
void laplace_fold_lock_evidence_targets(const uint8_t *type16, ArrayType *ids,
                                        const InArray *subjects,
                                        const InArray *objects,
                                        const InArray *ts, const char *label);
FoldEvidenceStates *laplace_fold_read_evidence_states_batch(
    const InArray *types, const InArray *subjects, const InArray *objects,
    const InArray *games, const InArray *ts, FoldPriorStates *priors,
    const char *label, bool replayable_only, bool require_all,
    bool matched_only);
FoldEvidenceStates *laplace_fold_read_evidence_states(
    const uint8_t *type16, const InArray *subjects, const InArray *objects,
    const InArray *games, const InArray *ts, FoldPriorStates *priors,
    const char *label, bool replayable_only, bool require_all);
int64 laplace_fold_write_evidence_states(
    const uint8_t *type16, ArrayType *ids, const InArray *subjects,
    const InArray *objects, const FoldEvidenceStates *evidence,
    const FoldStateArrays *folds, const char *label);
void laplace_fold_write_run(
    const uint8_t *type16, Datum type, const Datum *types, ArrayType *ids,
    const InArray *subjects, const InArray *objects,
    const FoldEvidenceStates *evidence, const FoldStateArrays *folds,
    const FoldPriorStates *priors, int64 *affected, const char *label);
void laplace_fold_validate_evidence_delta(
    const InArray *phis, const InArray *games, const InArray *sums,
    const PeriodArrays *periods, const char *label);

#endif
