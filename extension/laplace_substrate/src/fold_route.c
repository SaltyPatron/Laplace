/*
 * fold_route.c — native keyed routing for the two fold WRITE sites
 * (attestation_merge, consensus_upsert). Completes GH #565: the probe half
 * landed in descent_probe.c; this is the write half.
 *
 * Both tables are partitioned LIST(type_id) -> HASH(subject_id), and both
 * callers hold the partition keys for every row (the ids were computed FROM
 * them). Routing is therefore the caller's knowledge, not the planner's to
 * rediscover — binding law: routing/math in C/SPI, SQL orchestrates.
 *
 * What this replaces, and why (all measured on live seeds):
 *  - The plpgsql bodies materialized every batch into a fresh temp table
 *    (CREATE TEMP TABLE + CREATE INDEX + ANALYZE per call) purely so a
 *    per-type loop could bind the partition key to a variable/literal.
 *  - attestation_merge went further: EXECUTE format(%L) per type — correct
 *    pruning, but a full re-plan of a partitioned UPDATE for every type of
 *    every chunk of every apply, forever. (An UPDATE's result relations are
 *    locked at PLAN time, so only a literal key prunes; a generic plan opens
 *    all ~1,300 leaves — the disease its comment documents.)
 *
 * The native shape: group the batch by type in C (run detection — the caller
 *  contract already sorts by (type, subject, id), and correctness does not
 *  depend on it: a type split across runs just executes its plan twice on
 *  disjoint rows), then execute a SESSION-CACHED prepared plan per type whose
 *  type_id is a hex LITERAL in the plan text, kept in an HTAB of
 *  type_id -> SPI_keepplan'd SPIPlanPtr in TopMemoryContext. Plan-time LIST
 *  pruning happens once per (backend, type) and is reused across every chunk
 *  and every apply; runtime HASH pruning picks the one leaf per row. No temp
 *  table, no ANALYZE, no re-plan, no volatility trap.
 *
 * Fold math stays where it lives: the plans call the same native scalar
 * (laplace_glicko2_accumulate_games) the plpgsql called — one implementation
 * per fact. consensus_id stays one implementation the same way: the SQL
 * definition IS blake3(subject || type || COALESCE(object, 16 zero bytes))
 * via the core hash128_blake3; this file calls that exact core function over
 * the exact 48-byte layout.
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"

PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert);

/* ------------------------------------------------------------------ */
/* Session plan cache: one HTAB per statement family, keyed by type id */
/* ------------------------------------------------------------------ */

typedef struct TypePlanEntry
{
    char       type_id[16];
    SPIPlanPtr plan;
} TypePlanEntry;

static HTAB *merge_plans = NULL;          /* attestations UPDATE            */
static HTAB *upsert_update_plans = NULL;  /* consensus re-fold UPDATE       */
static HTAB *upsert_insert_plans = NULL;  /* consensus novel-cell INSERT    */

static HTAB *
plan_htab(HTAB **slot, const char *name)
{
    if (*slot == NULL)
    {
        HASHCTL ctl;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(TypePlanEntry);
        ctl.hcxt = TopMemoryContext;
        *slot = hash_create(name, 256, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    return *slot;
}

/* Fetch (or build+cache) the per-type plan for one statement family.
 * `template_sql` contains one %s to receive the 32-hex-char type literal
 * (it may appear multiple times via %1$s-style repetition below). */
static SPIPlanPtr
typed_plan(HTAB **slot, const char *name, const uint8_t *type16,
           const char *template_sql, int nargs, const Oid *argtypes)
{
    TypePlanEntry *entry;
    bool           found;

    entry = (TypePlanEntry *) hash_search(plan_htab(slot, name), type16,
                                          HASH_ENTER, &found);
    if (!found)
    {
        char       hex[33];
        StringInfoData sql;
        SPIPlanPtr plan;
        const char *p;
        int         j;

        for (j = 0; j < 16; j++)
            snprintf(hex + j * 2, 3, "%02x", type16[j]);

        /* substitute every %s in the template with the hex literal */
        initStringInfo(&sql);
        for (p = template_sql; *p; p++)
        {
            if (p[0] == '%' && p[1] == 's')
            {
                appendStringInfoString(&sql, hex);
                p++;
            }
            else
                appendStringInfoChar(&sql, *p);
        }

        plan = SPI_prepare(sql.data, nargs, (Oid *) argtypes);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: SPI_prepare failed: %s",
                            name, SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: SPI_keepplan failed", name)));
        entry->plan = plan;
        pfree(sql.data);
    }
    return entry->plan;
}

/* ------------------------------------------------------------------ */
/* Array plumbing                                                      */
/* ------------------------------------------------------------------ */

typedef struct InArray
{
    Datum *elems;
    bool  *nulls;
    int    n;
} InArray;

static void
in_array(FunctionCallInfo fcinfo, int argno, Oid elmtype, int elmlen,
         bool elmbyval, char elmalign, bool allow_nulls, const char *label,
         InArray *out)
{
    ArrayType *arr;

    if (PG_ARGISNULL(argno))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not be NULL", label, argno + 1)));
    arr = PG_GETARG_ARRAYTYPE_P(argno);
    if (ARR_NDIM(arr) > 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: argument %d must be 1-dimensional", label, argno + 1)));
    deconstruct_array(arr, elmtype, elmlen, elmbyval, elmalign,
                      &out->elems, &out->nulls, &out->n);
    if (!allow_nulls)
    {
        int i;

        for (i = 0; i < out->n; i++)
            if (out->nulls[i])
                ereport(ERROR,
                        (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                         errmsg("%s: argument %d must not contain NULLs",
                                label, argno + 1)));
    }
}

static const uint8_t *
bytea16(Datum d, const char *label)
{
    bytea *b = DatumGetByteaPP(d);

    if (VARSIZE_ANY_EXHDR(b) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("%s: expected 16-byte id, got %zu bytes",
                        label, (size_t) VARSIZE_ANY_EXHDR(b))));
    return (const uint8_t *) VARDATA_ANY(b);
}

static ArrayType *
slice_array(const Datum *src, const bool *src_nulls, const int *idx, int n,
            Oid elmtype, int elmlen, bool elmbyval, char elmalign)
{
    Datum *d = (Datum *) palloc(sizeof(Datum) * n);
    bool  *nu = (bool *) palloc(sizeof(bool) * n);
    int    dims[1];
    int    lbs[1] = {1};
    int    i;
    bool   any_null = false;

    for (i = 0; i < n; i++)
    {
        bool isnull = src_nulls != NULL && src_nulls[idx[i]];

        d[i] = isnull ? (Datum) 0 : src[idx[i]];
        nu[i] = isnull;
        any_null |= isnull;
    }
    dims[0] = n;
    if (any_null)
        return construct_md_array(d, nu, 1, dims, lbs,
                                  elmtype, elmlen, elmbyval, elmalign);
    return construct_array(d, n, elmtype, elmlen, elmbyval, elmalign);
}

/* ------------------------------------------------------------------ */
/* attestation_merge — routed present-row observation merge            */
/* ------------------------------------------------------------------ */

static const char *MERGE_SQL =
    "UPDATE laplace.attestations a SET "
    "   observation_count = a.observation_count + b.games, "
    "   last_observed_at  = GREATEST(a.last_observed_at, b.ts) "
    "FROM unnest($1::bytea[], $2::bytea[], $3::int8[], $4::timestamptz[]) "
    "     AS b(id, s, games, ts) "
    "WHERE a.type_id = '\\x%s'::bytea AND a.subject_id = b.s AND a.id = b.id";

Datum
pg_laplace_attestation_merge(PG_FUNCTION_ARGS)
{
    const char *label = "attestation_merge";
    InArray     ids, types, subjects, games, ts;
    int64       affected = 0;
    int        *idx;
    int         run_start;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    if (types.n != ids.n || subjects.n != ids.n || games.n != ids.n || ts.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length "
                        "(%d/%d/%d/%d/%d)", label,
                        ids.n, types.n, subjects.n, games.n, ts.n)));
    if (ids.n == 0)
        PG_RETURN_INT64(0);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    idx = (int *) palloc(sizeof(int) * ids.n);

    run_start = 0;
    while (run_start < ids.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            i = run_start;
        SPIPlanPtr     plan;
        Datum          vals[4];
        static const Oid argtypes[4] =
            {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, 1185 /* timestamptz[] */};
        int            rc;

        while (i < ids.n &&
               memcmp(bytea16(types.elems[i], label), type16, 16) == 0)
        {
            idx[run_n++] = i;
            i++;
        }

        plan = typed_plan(&merge_plans, "attestation_merge plans", type16,
                          MERGE_SQL, 4, argtypes);
        vals[0] = PointerGetDatum(slice_array(ids.elems, NULL, idx, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[1] = PointerGetDatum(slice_array(subjects.elems, NULL, idx, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[2] = PointerGetDatum(slice_array(games.elems, NULL, idx, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[3] = PointerGetDatum(slice_array(ts.elems, NULL, idx, run_n,
                                              TIMESTAMPTZOID, 8, true, 'd'));

        rc = SPI_execute_plan(plan, vals, NULL, false, 0);
        if (rc != SPI_OK_UPDATE)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: UPDATE failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        run_start = i;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* ------------------------------------------------------------------ */
/* consensus_upsert — routed inline fold                               */
/* ------------------------------------------------------------------ */

/* EXISTING cells: re-fold against current state. Fold math = the same native
 * scalar the plpgsql called; one implementation per fact. */
static const char *UPSERT_UPDATE_SQL =
    "UPDATE laplace.consensus c SET "
    "   (rating, rd, volatility) = "
    "       (SELECT r.rating, r.rd, r.volatility "
    "        FROM laplace.laplace_glicko2_accumulate_games("
    "             c.rating, c.rd, c.volatility, laplace.glicko2_neutral_mu(), "
    "             b.phi, b.games, b.sum, laplace.glicko2_tau()) AS r), "
    "   witness_count    = c.witness_count + b.games, "
    "   last_observed_at = GREATEST(c.last_observed_at, b.ts) "
    "FROM unnest($1::bytea[], $2::bytea[], $3::int8[], $4::int8[], $5::int8[], "
    "            $6::timestamptz[]) AS b(id, s, phi, games, sum, ts) "
    "WHERE c.type_id = '\\x%s'::bytea AND c.subject_id = b.s AND c.id = b.id";

/* NOVEL cells: INSERT tuple-routes to the owning leaf; NOT EXISTS (literal
 * type -> pruned) proves novelty, no ON CONFLICT. Fresh fold state comes from
 * the same native scalar, seeded from the neutral prior. */
static const char *UPSERT_INSERT_SQL =
    "INSERT INTO laplace.consensus "
    "   (id, subject_id, type_id, object_id, "
    "    rating, rd, volatility, witness_count, last_observed_at) "
    "SELECT b.id, b.s, '\\x%s'::bytea, b.o, "
    "       f.rating, f.rd, f.volatility, b.games, b.ts "
    "FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::int8[], $5::int8[], "
    "            $6::int8[], $7::timestamptz[]) AS b(id, s, o, phi, games, sum, ts) "
    "CROSS JOIN LATERAL laplace.laplace_glicko2_accumulate_games("
    "     laplace.glicko2_neutral_mu(), laplace.glicko2_initial_rd(), "
    "     laplace.glicko2_initial_volatility(), laplace.glicko2_neutral_mu(), "
    "     b.phi, b.games, b.sum, laplace.glicko2_tau()) AS f "
    "WHERE NOT EXISTS (SELECT 1 FROM laplace.consensus c "
    "                  WHERE c.type_id = '\\x%s'::bytea "
    "                    AND c.subject_id = b.s AND c.id = b.id)";

/* NO mask queue here — parity with the plpgsql body this replaces
 * (2026-07-21): the caller deposits highway bits INLINE for this same delta
 * via highway_mask_deposit; highway_mask_dirty is populated only by the
 * repair verbs, which need to CLEAR bits (per-source evict). */

/* Duplicate-cell guard: parity with the plpgsql contract check. */
typedef struct CellSeen
{
    char id[16];
} CellSeen;

Datum
pg_laplace_consensus_upsert(PG_FUNCTION_ARGS)
{
    const char *label = "consensus_upsert";
    InArray     subjects, types, objects, phis, games, sums, ts;
    Datum      *cell_ids;
    int64       affected = 0;
    HTAB       *seen;
    HASHCTL     ctl;
    int        *idx;
    int         run_start;
    int         i;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    if (types.n != subjects.n || objects.n != subjects.n || phis.n != subjects.n ||
        games.n != subjects.n || sums.n != subjects.n || ts.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);

    /* Cell ids natively: blake3(subject || type || COALESCE(object, zeros)) —
     * the exact byte layout of the SQL consensus_id definition, through the
     * same core hash. Doubles as the duplicate-cell contract check. */
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);
    for (i = 0; i < subjects.n; i++)
    {
        uint8_t    buf[48];
        hash128_t  h;
        bytea     *out;
        bool       found;

        memcpy(buf, bytea16(subjects.elems[i], label), 16);
        memcpy(buf + 16, bytea16(types.elems[i], label), 16);
        if (objects.nulls[i])
            memset(buf + 32, 0, 16);
        else
            memcpy(buf + 32, bytea16(objects.elems[i], label), 16);
        hash128_blake3(buf, sizeof(buf), &h);

        hash_search(seen, &h, HASH_ENTER, &found);
        if (found)
            ereport(ERROR,
                    (errcode(ERRCODE_CARDINALITY_VIOLATION),
                     errmsg("consensus_upsert: duplicate cell in one call "
                            "(client-dedup contract violated)")));

        out = (bytea *) palloc(VARHDRSZ + 16);
        SET_VARSIZE(out, VARHDRSZ + 16);
        memcpy(VARDATA(out), &h, 16);
        cell_ids[i] = PointerGetDatum(out);
    }
    hash_destroy(seen);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    idx = (int *) palloc(sizeof(int) * subjects.n);

    run_start = 0;
    while (run_start < subjects.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            j = run_start;
        SPIPlanPtr     up_plan, ins_plan;
        Datum          up_vals[6], ins_vals[7];
        static const Oid up_args[6] =
            {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, 1185};
        static const Oid ins_args[7] =
            {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, INT8ARRAYOID, 1185};
        int            rc;

        while (j < subjects.n &&
               memcmp(bytea16(types.elems[j], label), type16, 16) == 0)
        {
            idx[run_n++] = j;
            j++;
        }

        /* EXISTING cells of this type: re-fold. */
        up_plan = typed_plan(&upsert_update_plans, "consensus_upsert update plans",
                             type16, UPSERT_UPDATE_SQL, 6, up_args);
        up_vals[0] = PointerGetDatum(slice_array(cell_ids, NULL, idx, run_n,
                                                 BYTEAOID, -1, false, 'i'));
        up_vals[1] = PointerGetDatum(slice_array(subjects.elems, NULL, idx, run_n,
                                                 BYTEAOID, -1, false, 'i'));
        up_vals[2] = PointerGetDatum(slice_array(phis.elems, NULL, idx, run_n,
                                                 INT8OID, 8, true, 'd'));
        up_vals[3] = PointerGetDatum(slice_array(games.elems, NULL, idx, run_n,
                                                 INT8OID, 8, true, 'd'));
        up_vals[4] = PointerGetDatum(slice_array(sums.elems, NULL, idx, run_n,
                                                 INT8OID, 8, true, 'd'));
        up_vals[5] = PointerGetDatum(slice_array(ts.elems, NULL, idx, run_n,
                                                 TIMESTAMPTZOID, 8, true, 'd'));
        rc = SPI_execute_plan(up_plan, up_vals, NULL, false, 0);
        if (rc != SPI_OK_UPDATE)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: UPDATE failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        /* NOVEL cells of this type. */
        ins_plan = typed_plan(&upsert_insert_plans, "consensus_upsert insert plans",
                              type16, UPSERT_INSERT_SQL, 7, ins_args);
        ins_vals[0] = up_vals[0];
        ins_vals[1] = up_vals[1];
        ins_vals[2] = PointerGetDatum(slice_array(objects.elems, objects.nulls,
                                                  idx, run_n,
                                                  BYTEAOID, -1, false, 'i'));
        ins_vals[3] = up_vals[2];
        ins_vals[4] = up_vals[3];
        ins_vals[5] = up_vals[4];
        ins_vals[6] = up_vals[5];
        rc = SPI_execute_plan(ins_plan, ins_vals, NULL, false, 0);
        if (rc != SPI_OK_INSERT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: INSERT failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        run_start = j;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}
