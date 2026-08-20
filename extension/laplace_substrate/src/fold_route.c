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
 *  and every apply; runtime HASH pruning picks the one leaf per row. The fold
 *  plan is one MERGE, not an UPDATE plus an INSERT/NOT-EXISTS second probe.
 *  No temp table, no ANALYZE, no re-plan, no volatility trap.
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
PG_FUNCTION_INFO_V1(pg_laplace_attestation_merge_type);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_upsert_type);

/* ------------------------------------------------------------------ */
/* Session plan cache: one HTAB per statement family, keyed by type id */
/* ------------------------------------------------------------------ */

typedef struct TypePlanEntry
{
    char       type_id[16];
    SPIPlanPtr plan;
} TypePlanEntry;

static HTAB *merge_plans = NULL;          /* attestations matched MERGE     */
static HTAB *upsert_merge_plans = NULL;   /* consensus matched/unmatched     */

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
    ArrayType *array;          /* original detoasted argument; reusable whole */
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
    out->array = arr;
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
array_window(ArrayType *original, const Datum *src, const bool *src_nulls,
            int total, int start, int n,
            Oid elmtype, int elmlen, bool elmbyval, char elmalign)
{
    if (start == 0 && n == total)
        return original;

    Datum *d = (Datum *) palloc(sizeof(Datum) * n);
    bool  *nu = (bool *) palloc(sizeof(bool) * n);
    int    dims[1];
    int    lbs[1] = {1};
    int    i;
    bool   any_null = false;

    for (i = 0; i < n; i++)
    {
        bool isnull = src_nulls != NULL && src_nulls[start + i];

        d[i] = isnull ? (Datum) 0 : src[start + i];
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

/* sum_score_fp1e9 accumulates additively with observation_count (evidence is
 * the exact record of the fold's inputs); opponent_rd_fp1e9 is deliberately
 * NOT updated — it is the per-deposit phi, and the attestation identity pins
 * (subject, type, object, source, context), so a merge of the same row
 * re-witnesses under the same source/relation: incoming phi == stored phi.
 * See sql/functions/fold/attestation_merge.sql.in. */
static const char *MERGE_SQL =
    "MERGE INTO laplace.attestations a "
    "USING unnest($1::bytea[], $2::bytea[], $3::int8[], $4::int8[], "
    "             $5::timestamptz[]) "
    "      AS b(id, s, games, sum, ts) "
    "ON a.type_id = '\\x%s'::bytea AND a.subject_id = b.s AND a.id = b.id "
    "WHEN MATCHED THEN UPDATE SET "
    "   observation_count = a.observation_count + b.games, "
    "   sum_score_fp1e9   = a.sum_score_fp1e9 + b.sum, "
    "   last_observed_at  = GREATEST(a.last_observed_at, b.ts)";

Datum
pg_laplace_attestation_merge(PG_FUNCTION_ARGS)
{
    const char *label = "attestation_merge";
    InArray     ids, types, subjects, games, sums, ts;
    int64       affected = 0;
    int         run_start;

    in_array(fcinfo, 0, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &types);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    if (types.n != ids.n || subjects.n != ids.n || games.n != ids.n ||
        sums.n != ids.n || ts.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length "
                        "(%d/%d/%d/%d/%d/%d)", label,
                        ids.n, types.n, subjects.n, games.n, sums.n, ts.n)));
    if (ids.n == 0)
        PG_RETURN_INT64(0);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    run_start = 0;
    while (run_start < ids.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            i = run_start;
        SPIPlanPtr     plan;
        Datum          vals[5];
        static const Oid argtypes[5] =
            {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
             1185 /* timestamptz[] */};
        int            rc;

        while (i < ids.n &&
               memcmp(bytea16(types.elems[i], label), type16, 16) == 0)
        {
            run_n++;
            i++;
        }

        plan = typed_plan(&merge_plans, "attestation_merge plans", type16,
                          MERGE_SQL, 5, argtypes);
        vals[0] = PointerGetDatum(array_window(ids.array, ids.elems, NULL,
                                              ids.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[1] = PointerGetDatum(array_window(subjects.array, subjects.elems, NULL,
                                              subjects.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[2] = PointerGetDatum(array_window(games.array, games.elems, NULL,
                                              games.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[3] = PointerGetDatum(array_window(sums.array, sums.elems, NULL,
                                              sums.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[4] = PointerGetDatum(array_window(ts.array, ts.elems, NULL,
                                              ts.n, run_start, run_n,
                                              TIMESTAMPTZOID, 8, true, 'd'));

        rc = SPI_execute_plan(plan, vals, NULL, false, 0);
        if (rc != SPI_OK_MERGE)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: MERGE failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        run_start = i;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* Direct routed form for first-party ingest. The caller already owns a single
 * type run, so transmitting the same 16-byte type once per row merely to detect
 * that run again is pure allocation/wire/deconstruction work. */
Datum
pg_laplace_attestation_merge_type(PG_FUNCTION_ARGS)
{
    const char    *label = "attestation_merge_type";
    const uint8_t *type16;
    InArray        ids, subjects, games, sums, ts;
    SPIPlanPtr     plan;
    Datum          vals[5];
    static const Oid argtypes[5] =
        {BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
         1185 /* timestamptz[] */};
    int rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type must not be NULL", label)));
    type16 = bytea16(PG_GETARG_DATUM(0), label);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &ids);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 5, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    if (subjects.n != ids.n || games.n != ids.n || sums.n != ids.n || ts.n != ids.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (ids.n == 0)
        PG_RETURN_INT64(0);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    plan = typed_plan(&merge_plans, "attestation_merge plans", type16,
                      MERGE_SQL, 5, argtypes);
    vals[0] = PointerGetDatum(ids.array);
    vals[1] = PointerGetDatum(subjects.array);
    vals[2] = PointerGetDatum(games.array);
    vals[3] = PointerGetDatum(sums.array);
    vals[4] = PointerGetDatum(ts.array);
    rc = SPI_execute_plan(plan, vals, NULL, false, 0);
    if (rc != SPI_OK_MERGE)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: MERGE failed: %s", label,
                        SPI_result_code_string(rc))));
    {
        int64 affected = (int64) SPI_processed;
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
}

/* ------------------------------------------------------------------ */
/* consensus_upsert — routed inline fold                               */
/* ------------------------------------------------------------------ */

/* One literal-routed set merge per type. The old implementation retained two
 * statements after native routing landed: UPDATE every existing cell, then
 * INSERT ... WHERE NOT EXISTS over the same input. That was a double target
 * probe whose only historical justification was MERGE planned with a runtime
 * type key. The router now embeds type_id as a literal, so PostgreSQL prunes the
 * LIST partition before executing this single matched/unmatched join.
 *
 * Fresh-state Glicko values are prepared in the source. Existing cells use the
 * stored prior in the MATCHED action; novel cells consume the prepared neutral
 * fold in the NOT MATCHED action. */
static const char *UPSERT_MERGE_SQL =
    "MERGE INTO laplace.consensus c "
    "USING ("
    "  SELECT b.*, f.rating AS initial_rating, f.rd AS initial_rd, "
    "         f.volatility AS initial_volatility "
    "  FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::int8[], "
    "              $5::int8[], $6::int8[], $7::timestamptz[]) "
    "       AS b(id, s, o, phi, games, score_sum, ts) "
    "  CROSS JOIN LATERAL laplace.laplace_glicko2_accumulate_games("
    "       consensus.glicko2_neutral_mu(), consensus.glicko2_initial_rd(), "
    "       consensus.glicko2_initial_volatility(), consensus.glicko2_neutral_mu(), "
    "       b.phi, b.games, b.score_sum, consensus.glicko2_tau()) AS f"
    ") b "
    "ON c.type_id = '\\x%s'::bytea AND c.subject_id = b.s AND c.id = b.id "
    "WHEN MATCHED THEN UPDATE SET "
    "  (rating, rd, volatility) = "
    "      (SELECT r.rating, r.rd, r.volatility "
    "       FROM laplace.laplace_glicko2_accumulate_games("
    "            c.rating, c.rd, c.volatility, consensus.glicko2_neutral_mu(), "
    "            b.phi, b.games, b.score_sum, consensus.glicko2_tau()) AS r), "
    "  witness_count = c.witness_count + b.games, "
    "  last_observed_at = GREATEST(c.last_observed_at, b.ts) "
    "WHEN NOT MATCHED THEN INSERT "
    "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
    "   witness_count, last_observed_at) "
    "VALUES (b.id, b.s, '\\x%s'::bytea, b.o, b.initial_rating, b.initial_rd, "
    "        b.initial_volatility, b.games, b.ts)";

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
    ArrayType  *cell_id_array;
    int64       affected = 0;
    HTAB       *seen;
    HASHCTL     ctl;
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
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));

    run_start = 0;
    while (run_start < subjects.n)
    {
        const uint8_t *type16 = bytea16(types.elems[run_start], label);
        int            run_n = 0;
        int            j = run_start;
        SPIPlanPtr     merge_plan;
        Datum          vals[7];
        static const Oid args[7] =
            {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
             INT8ARRAYOID, INT8ARRAYOID, 1185};
        int            rc;

        while (j < subjects.n &&
               memcmp(bytea16(types.elems[j], label), type16, 16) == 0)
        {
            run_n++;
            j++;
        }

        merge_plan = typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
                                type16, UPSERT_MERGE_SQL, 7, args);
        vals[0] = PointerGetDatum(array_window(cell_id_array, cell_ids, NULL,
                                              subjects.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[1] = PointerGetDatum(array_window(subjects.array, subjects.elems, NULL,
                                              subjects.n, run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[2] = PointerGetDatum(array_window(objects.array, objects.elems,
                                              objects.nulls, objects.n,
                                              run_start, run_n,
                                              BYTEAOID, -1, false, 'i'));
        vals[3] = PointerGetDatum(array_window(phis.array, phis.elems, NULL,
                                              phis.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[4] = PointerGetDatum(array_window(games.array, games.elems, NULL,
                                              games.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[5] = PointerGetDatum(array_window(sums.array, sums.elems, NULL,
                                              sums.n, run_start, run_n,
                                              INT8OID, 8, true, 'd'));
        vals[6] = PointerGetDatum(array_window(ts.array, ts.elems, NULL,
                                              ts.n, run_start, run_n,
                                              TIMESTAMPTZOID, 8, true, 'd'));
        rc = SPI_execute_plan(merge_plan, vals, NULL, false, 0);
        if (rc != SPI_OK_MERGE)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: MERGE failed: %s",
                            label, SPI_result_code_string(rc))));
        affected += (int64) SPI_processed;

        run_start = j;
    }

    SPI_finish();
    PG_RETURN_INT64(affected);
}

/* Direct routed form for a caller-owned single type run. Besides eliminating
 * the redundant type array, this constructs the derived cell-id array exactly
 * once and passes every caller array straight through to the cached MERGE plan. */
Datum
pg_laplace_consensus_upsert_type(PG_FUNCTION_ARGS)
{
    const char    *label = "consensus_upsert_type";
    const uint8_t *type16;
    InArray        subjects, objects, phis, games, sums, ts;
    Datum         *cell_ids;
    ArrayType     *cell_id_array;
    HTAB          *seen;
    HASHCTL        ctl;
    SPIPlanPtr     merge_plan;
    Datum          vals[7];
    static const Oid args[7] =
        {BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID,
         INT8ARRAYOID, INT8ARRAYOID, 1185};
    int i;
    int rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: type must not be NULL", label)));
    type16 = bytea16(PG_GETARG_DATUM(0), label);
    in_array(fcinfo, 1, BYTEAOID, -1, false, 'i', false, label, &subjects);
    in_array(fcinfo, 2, BYTEAOID, -1, false, 'i', true, label, &objects);
    in_array(fcinfo, 3, INT8OID, 8, true, 'd', false, label, &phis);
    in_array(fcinfo, 4, INT8OID, 8, true, 'd', false, label, &games);
    in_array(fcinfo, 5, INT8OID, 8, true, 'd', false, label, &sums);
    in_array(fcinfo, 6, TIMESTAMPTZOID, 8, true, 'd', false, label, &ts);
    if (objects.n != subjects.n || phis.n != subjects.n || games.n != subjects.n ||
        sums.n != subjects.n || ts.n != subjects.n)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: parallel arrays must share length", label)));
    if (subjects.n == 0)
        PG_RETURN_INT64(0);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CellSeen);
    seen = hash_create("consensus_upsert_type cell guard", subjects.n, &ctl,
                       HASH_ELEM | HASH_BLOBS);
    cell_ids = (Datum *) palloc(sizeof(Datum) * subjects.n);
    for (i = 0; i < subjects.n; i++)
    {
        uint8_t   buf[48];
        hash128_t h;
        bytea    *out;
        bool      found;

        memcpy(buf, bytea16(subjects.elems[i], label), 16);
        memcpy(buf + 16, type16, 16);
        if (objects.nulls[i])
            memset(buf + 32, 0, 16);
        else
            memcpy(buf + 32, bytea16(objects.elems[i], label), 16);
        hash128_blake3(buf, sizeof(buf), &h);
        hash_search(seen, &h, HASH_ENTER, &found);
        if (found)
            ereport(ERROR,
                    (errcode(ERRCODE_CARDINALITY_VIOLATION),
                     errmsg("%s: duplicate cell in one call "
                            "(client-dedup contract violated)", label)));
        out = (bytea *) palloc(VARHDRSZ + 16);
        SET_VARSIZE(out, VARHDRSZ + 16);
        memcpy(VARDATA(out), &h, 16);
        cell_ids[i] = PointerGetDatum(out);
    }
    hash_destroy(seen);
    cell_id_array = construct_array(cell_ids, subjects.n,
                                    BYTEAOID, -1, false, 'i');

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: SPI_connect failed", label)));
    merge_plan = typed_plan(&upsert_merge_plans, "consensus_upsert merge plans",
                            type16, UPSERT_MERGE_SQL, 7, args);
    vals[0] = PointerGetDatum(cell_id_array);
    vals[1] = PointerGetDatum(subjects.array);
    vals[2] = PointerGetDatum(objects.array);
    vals[3] = PointerGetDatum(phis.array);
    vals[4] = PointerGetDatum(games.array);
    vals[5] = PointerGetDatum(sums.array);
    vals[6] = PointerGetDatum(ts.array);
    rc = SPI_execute_plan(merge_plan, vals, NULL, false, 0);
    if (rc != SPI_OK_MERGE)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: MERGE failed: %s", label,
                        SPI_result_code_string(rc))));
    {
        int64 affected = (int64) SPI_processed;
        SPI_finish();
        PG_RETURN_INT64(affected);
    }
}
