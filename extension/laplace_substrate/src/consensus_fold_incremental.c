/*
 * Native replacement for materialize_period_partition[_fresh].sql.in (PL/pgSQL) --
 * the INCREMENTAL fold lane, used mid-ingest to UPSERT staged observations
 * directly into the LIVE consensus table (unlike consensus_fold_walks.c /
 * consensus_fold_engine.c, which build a shadow consensus_next table for a
 * terminal/bulk rebuild via rename-swap -- no swap is possible here since
 * concurrent readers depend on `consensus` staying live throughout).
 *
 * The Glicko-2 math was already 100% native (consensus_fold_apply_partial,
 * shared with the terminal-fold engine); the PL/pgSQL orchestration around it
 * (GROUP BY merge, LEFT JOIN prior state, UPSERT) is what's replaced here,
 * following the same heap-scan + in-memory HTAB merge technique as
 * consensus_fold_walks.c, but reading ONE plain staging table (no
 * per-partition epoch tagging needed -- this lane folds a single staged
 * batch per call, not a multi-epoch terminal rebuild) and writing back via a
 * single batched SPI UPSERT instead of table_multi_insert into a shadow
 * table.
 */

#include "postgres.h"

#include "access/heapam.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "nodes/makefuncs.h"
#include "portability/instr_time.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "consensus_fold_math.h"
#include "consensus_fold_io.h"

typedef struct IncMergeKey
{
    uint8 ident[FOLD_IDENT_LEN];   /* subject(16) + type(16) + object-or-zero(16) */
} IncMergeKey;

typedef struct IncMergeEntry
{
    IncMergeKey key;
    bool   has_object;
    int64  phi_min, phi_max;
    int64  games;
    int64  sum_score;
    int64  last_ts;
    /* filled in after the prior-state batch read: */
    bool   has_prior;
    int64  prior_rating, prior_rd, prior_volatility, prior_witness;
} IncMergeEntry;

typedef struct PriorKey
{
    uint8 cid[16];
} PriorKey;

typedef struct PriorEntry
{
    PriorKey key;
    int64 rating, rd, volatility, witness_count;
} PriorEntry;

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_partition_incremental);

Datum
pg_laplace_consensus_fold_partition_incremental(PG_FUNCTION_ARGS)
{
    text   *table_text = PG_GETARG_TEXT_PP(0);
    bool    fresh = PG_ARGISNULL(1) ? false : PG_GETARG_BOOL(1);
    char   *tablename = text_to_cstring(table_text);

    MemoryContext fold_cxt, oldcxt;
    HASHCTL hctl;
    HTAB   *merge;
    int64   tau;
    int64   n_groups;
    int64   n_rows = 0;
    instr_time t0, t1;
    IncMergeEntry **groups;
    int64   i;

    INSTR_TIME_SET_CURRENT(t0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "consensus_fold_partition_incremental: SPI_connect failed");
    {
        Oid    argtypes[1] = { TEXTOID };
        Datum  args[1] = { CStringGetTextDatum(tablename) };
        bool   isnull;
        int    rc = SPI_execute_with_args("SELECT to_regclass($1)", 1, argtypes, args,
                                          NULL, true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed == 0)
            elog(ERROR, "consensus_fold_partition_incremental: to_regclass check failed");
        SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (isnull)
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("consensus_fold_partition_incremental: staging %s does not exist",
                            tablename)));
    }
    {
        bool  isnull;
        int   rc = SPI_execute("SELECT laplace.glicko2_tau()", true, 1);

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "consensus_fold_partition_incremental: glicko2_tau() read failed");
        tau = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
    }
    SPI_finish();

    fold_cxt = AllocSetContextCreate(CurrentMemoryContext,
                                     "consensus_fold_partition_incremental",
                                     ALLOCSET_DEFAULT_SIZES);
    oldcxt = MemoryContextSwitchTo(fold_cxt);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize = sizeof(IncMergeKey);
    hctl.entrysize = sizeof(IncMergeEntry);
    hctl.hcxt = fold_cxt;
    merge = hash_create("consensus_fold_partition_incremental merge", 1024, &hctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    /* Heap-scan the staging table, merging by (subject,type,object) -- same
     * GROUP BY min(phi)/max(phi)/sum(games)/sum(sum_score)/max(last_ts)
     * semantics as the PL/pgSQL this replaces. */
    {
        RangeVar   *rv = makeRangeVar(NULL, pstrdup(tablename), -1);
        Relation    rel = table_open(RangeVarGetRelid(rv, AccessShareLock, false), NoLock);
        TupleTableSlot *slot = table_slot_create(rel, NULL);
        TableScanDesc scan = table_beginscan(rel, GetActiveSnapshot(), 0, NULL);
        int a_subject = fold_attno(rel, "subject_id");
        int a_type    = fold_attno(rel, "type_id");
        int a_object  = fold_attno(rel, "object_id");
        int a_phi     = fold_attno(rel, "phi");
        int a_games   = fold_attno(rel, "games");
        int a_sum     = fold_attno(rel, "sum_score");
        int a_ts      = fold_attno(rel, "last_ts");

        while (table_scan_getnextslot(scan, ForwardScanDirection, slot))
        {
            IncMergeKey key;
            IncMergeEntry *ent;
            bool  found;
            bool  has_object;
            int64 phi, games, sum_score, last_ts;

            slot_getallattrs(slot);
            memset(&key, 0, sizeof(key));
            fold_read_bytea16(slot->tts_values[a_subject - 1], key.ident);
            fold_read_bytea16(slot->tts_values[a_type - 1], key.ident + 16);
            has_object = !slot->tts_isnull[a_object - 1];
            if (has_object)
                fold_read_bytea16(slot->tts_values[a_object - 1], key.ident + 32);

            phi       = DatumGetInt64(slot->tts_values[a_phi - 1]);
            games     = DatumGetInt64(slot->tts_values[a_games - 1]);
            sum_score = DatumGetInt64(slot->tts_values[a_sum - 1]);
            last_ts   = (int64) DatumGetTimestampTz(slot->tts_values[a_ts - 1]);

            ent = (IncMergeEntry *) hash_search(merge, &key, HASH_ENTER, &found);
            if (!found)
            {
                ent->has_object = has_object;
                ent->phi_min = ent->phi_max = phi;
                ent->games = games;
                ent->sum_score = sum_score;
                ent->last_ts = last_ts;
                ent->has_prior = false;
            }
            else
            {
                if (phi < ent->phi_min) ent->phi_min = phi;
                if (phi > ent->phi_max) ent->phi_max = phi;
                ent->games += games;
                ent->sum_score += sum_score;
                if (last_ts > ent->last_ts) ent->last_ts = last_ts;
            }
            if ((++n_rows & 0xFFFF) == 0)
                CHECK_FOR_INTERRUPTS();
        }
        table_endscan(scan);
        ExecDropSingleTupleTableSlot(slot);
        table_close(rel, NoLock);
    }

    n_groups = hash_get_num_entries(merge);
    groups = (IncMergeEntry **) palloc(sizeof(IncMergeEntry *) * (n_groups > 0 ? n_groups : 1));
    {
        HASH_SEQ_STATUS hs;
        IncMergeEntry *e;
        int64 gi = 0;

        hash_seq_init(&hs, merge);
        while ((e = (IncMergeEntry *) hash_seq_search(&hs)) != NULL)
        {
            if (e->phi_min != e->phi_max)
                ereport(ERROR,
                        (errcode(ERRCODE_DATA_EXCEPTION),
                         errmsg("accumulation invariant violated: relation "
                                "observed with mixed phi within one period")));
            groups[gi++] = e;
        }
        Assert(gi == n_groups);
    }

    /* Batch-read prior consensus state for exactly the touched cids (skip
     * entirely when fresh -- the target is assumed empty, same as the
     * PL/pgSQL _fresh variant never LEFT JOINs). */
    if (!fresh && n_groups > 0)
    {
        Datum      *cid_elems = (Datum *) palloc(sizeof(Datum) * n_groups);
        ArrayType  *cid_arr;
        Oid         argtypes[1] = { BYTEAARRAYOID };
        Datum       args[1];
        int         rc;
        HASHCTL     phctl;
        HTAB       *prior;

        for (i = 0; i < n_groups; i++)
        {
            hash128_t cid;
            bytea    *b = (bytea *) palloc(VARHDRSZ + 16);

            hash128_blake3(groups[i]->key.ident, FOLD_IDENT_LEN, &cid);
            SET_VARSIZE(b, VARHDRSZ + 16);
            memcpy(VARDATA(b), &cid, 16);
            cid_elems[i] = PointerGetDatum(b);
        }
        cid_arr = construct_array(cid_elems, (int) n_groups, BYTEAOID, -1, false, TYPALIGN_INT);
        args[0] = PointerGetDatum(cid_arr);

        memset(&phctl, 0, sizeof(phctl));
        phctl.keysize = sizeof(PriorKey);
        phctl.entrysize = sizeof(PriorEntry);
        phctl.hcxt = fold_cxt;
        prior = hash_create("consensus_fold_partition_incremental prior", (long) n_groups,
                            &phctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        if (SPI_connect() != SPI_OK_CONNECT)
            elog(ERROR, "consensus_fold_partition_incremental: SPI_connect (prior) failed");
        rc = SPI_execute_with_args(
            "SELECT id, rating, rd, volatility, witness_count "
            "FROM laplace.consensus WHERE id = ANY($1::bytea[])",
            1, argtypes, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "consensus_fold_partition_incremental: prior-state query failed: %s",
                 SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool isnull;
            PriorKey key;
            PriorEntry *pe;
            bool found;
            bytea *idb = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));

            memset(&key, 0, sizeof(key));
            memcpy(key.cid, VARDATA_ANY(idb), 16);
            pe = (PriorEntry *) hash_search(prior, &key, HASH_ENTER, &found);
            pe->rating        = DatumGetInt64(SPI_getbinval(tup, td, 2, &isnull));
            pe->rd            = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
            pe->volatility    = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
            pe->witness_count = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
        }
        SPI_finish();

        for (i = 0; i < n_groups; i++)
        {
            hash128_t cid;
            PriorKey  key;
            PriorEntry *pe;
            bool found;

            hash128_blake3(groups[i]->key.ident, FOLD_IDENT_LEN, &cid);
            memset(&key, 0, sizeof(key));
            memcpy(key.cid, &cid, 16);
            pe = (PriorEntry *) hash_search(prior, &key, HASH_FIND, &found);
            if (found)
            {
                groups[i]->has_prior = true;
                groups[i]->prior_rating     = pe->rating;
                groups[i]->prior_rd         = pe->rd;
                groups[i]->prior_volatility = pe->volatility;
                groups[i]->prior_witness    = pe->witness_count;
            }
        }
        hash_destroy(prior);
    }

    /* Fold each group's Glicko-2 state natively (same math the terminal-fold
     * engine already uses -- consensus_fold_apply_partial ->
     * glicko2_fold_uniform_period), then batch-UPSERT the results. */
    {
        Datum *cid_d, *subj_d, *type_d, *obj_d, *rating_d, *rd_d, *vol_d, *wc_d, *ts_d;
        bool  *obj_null;
        ArrayType *cid_arr, *subj_arr, *type_arr, *obj_arr, *rating_arr, *rd_arr,
                  *vol_arr, *wc_arr, *ts_arr;
        FoldScratch scratch;
        Oid    argtypes[9] = { BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID,
                               INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID,
                               TIMESTAMPTZARRAYOID };
        Datum  args[9];
        int    dims[1], lbs[1] = {1};
        int    rc;

        memset(&scratch, 0, sizeof(scratch));
        scratch.cxt = fold_cxt;

        cid_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        subj_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        type_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        obj_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        obj_null = (bool *) palloc(sizeof(bool) * (n_groups > 0 ? n_groups : 1));
        rating_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        rd_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        vol_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        wc_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));
        ts_d = (Datum *) palloc(sizeof(Datum) * (n_groups > 0 ? n_groups : 1));

        for (i = 0; i < n_groups; i++)
        {
            IncMergeEntry *g = groups[i];
            hash128_t cid;
            glicko2_state_t st;
            bytea *cidb, *subjb, *typeb;

            hash128_blake3(g->key.ident, FOLD_IDENT_LEN, &cid);

            if (g->has_prior)
                glicko2_init(&st, g->prior_rating, g->prior_rd, g->prior_volatility);
            else
                glicko2_init(&st, CONSENSUS_FOLD_NEUTRAL_MU, CONSENSUS_FOLD_INITIAL_RD,
                            CONSENSUS_FOLD_INITIAL_VOLATILITY);

            if (g->games > FOLD_GAMES_BOUND)
                ereport(ERROR,
                        (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                         errmsg("consensus_fold_partition_incremental: " INT64_FORMAT
                                " games exceed the per-period bound", g->games)));
            fold_scratch_reserve(&scratch, g->games);
            consensus_fold_apply_partial(&st, g->phi_min, g->games, g->sum_score, tau,
                                         scratch.obs);

            cidb = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(cidb, VARHDRSZ + 16);
            memcpy(VARDATA(cidb), &cid, 16);
            cid_d[i] = PointerGetDatum(cidb);

            subjb = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(subjb, VARHDRSZ + 16);
            memcpy(VARDATA(subjb), g->key.ident, 16);
            subj_d[i] = PointerGetDatum(subjb);

            typeb = (bytea *) palloc(VARHDRSZ + 16);
            SET_VARSIZE(typeb, VARHDRSZ + 16);
            memcpy(VARDATA(typeb), g->key.ident + 16, 16);
            type_d[i] = PointerGetDatum(typeb);

            if (g->has_object)
            {
                bytea *objb = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(objb, VARHDRSZ + 16);
                memcpy(VARDATA(objb), g->key.ident + 32, 16);
                obj_d[i] = PointerGetDatum(objb);
                obj_null[i] = false;
            }
            else
            {
                obj_d[i] = (Datum) 0;
                obj_null[i] = true;
            }

            rating_d[i] = Int64GetDatum(st.rating);
            rd_d[i]     = Int64GetDatum(st.rd);
            vol_d[i]    = Int64GetDatum(st.volatility);
            wc_d[i]     = Int64GetDatum((g->has_prior ? g->prior_witness : 0) + g->games);
            ts_d[i]     = TimestampTzGetDatum((TimestampTz) g->last_ts);
        }

        dims[0] = (int) n_groups;
        {
            /* Look up the real catalog storage properties instead of assuming
             * -- INT8OID and TIMESTAMPTZOID happen to both be 8-byte
             * pass-by-value double-aligned on any 64-bit build this targets,
             * but get_typlenbyvalalign is the correct, always-safe way to
             * learn that rather than hardcoding it. */
            int16   int8_len, ts_len;
            bool    int8_byval, ts_byval;
            char    int8_align, ts_align;

            get_typlenbyvalalign(INT8OID, &int8_len, &int8_byval, &int8_align);
            get_typlenbyvalalign(TIMESTAMPTZOID, &ts_len, &ts_byval, &ts_align);

            cid_arr    = construct_array(cid_d, (int) n_groups, BYTEAOID, -1, false, TYPALIGN_INT);
            subj_arr   = construct_array(subj_d, (int) n_groups, BYTEAOID, -1, false, TYPALIGN_INT);
            type_arr   = construct_array(type_d, (int) n_groups, BYTEAOID, -1, false, TYPALIGN_INT);
            obj_arr    = construct_md_array(obj_d, obj_null, 1, dims, lbs, BYTEAOID, -1, false, TYPALIGN_INT);
            rating_arr = construct_array(rating_d, (int) n_groups, INT8OID, int8_len, int8_byval, int8_align);
            rd_arr     = construct_array(rd_d, (int) n_groups, INT8OID, int8_len, int8_byval, int8_align);
            vol_arr    = construct_array(vol_d, (int) n_groups, INT8OID, int8_len, int8_byval, int8_align);
            wc_arr     = construct_array(wc_d, (int) n_groups, INT8OID, int8_len, int8_byval, int8_align);
            ts_arr     = construct_array(ts_d, (int) n_groups, TIMESTAMPTZOID, ts_len, ts_byval, ts_align);
        }

        argtypes[0] = BYTEAARRAYOID; argtypes[1] = BYTEAARRAYOID;
        argtypes[2] = BYTEAARRAYOID; argtypes[3] = BYTEAARRAYOID;
        argtypes[4] = INT8ARRAYOID;  argtypes[5] = INT8ARRAYOID;
        argtypes[6] = INT8ARRAYOID;  argtypes[7] = INT8ARRAYOID;
        argtypes[8] = TIMESTAMPTZARRAYOID;
        args[0] = PointerGetDatum(cid_arr);
        args[1] = PointerGetDatum(subj_arr);
        args[2] = PointerGetDatum(type_arr);
        args[3] = PointerGetDatum(obj_arr);
        args[4] = PointerGetDatum(rating_arr);
        args[5] = PointerGetDatum(rd_arr);
        args[6] = PointerGetDatum(vol_arr);
        args[7] = PointerGetDatum(wc_arr);
        args[8] = PointerGetDatum(ts_arr);

        if (SPI_connect() != SPI_OK_CONNECT)
            elog(ERROR, "consensus_fold_partition_incremental: SPI_connect (upsert) failed");
        rc = SPI_execute_with_args(
            fresh
            ? "INSERT INTO laplace.consensus "
              "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
              "   witness_count, last_observed_at) "
              "SELECT * FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::bytea[], "
              "  $5::int8[], $6::int8[], $7::int8[], $8::int8[], $9::timestamptz[]) "
              "ON CONFLICT (id) DO NOTHING"
            : "INSERT INTO laplace.consensus "
              "  (id, subject_id, type_id, object_id, rating, rd, volatility, "
              "   witness_count, last_observed_at) "
              "SELECT * FROM unnest($1::bytea[], $2::bytea[], $3::bytea[], $4::bytea[], "
              "  $5::int8[], $6::int8[], $7::int8[], $8::int8[], $9::timestamptz[]) "
              "ON CONFLICT (id) DO UPDATE SET "
              "  rating = EXCLUDED.rating, rd = EXCLUDED.rd, "
              "  volatility = EXCLUDED.volatility, witness_count = EXCLUDED.witness_count, "
              "  last_observed_at = EXCLUDED.last_observed_at",
            9, argtypes, args, NULL, false, 0);
        if (rc != SPI_OK_INSERT)
            elog(ERROR, "consensus_fold_partition_incremental: upsert failed: %s",
                 SPI_result_code_string(rc));

        {
            char *sql = psprintf("DROP TABLE %s", quote_identifier(tablename));

            if (SPI_execute(sql, false, 0) != SPI_OK_UTILITY)
                elog(ERROR, "consensus_fold_partition_incremental: DROP TABLE %s failed",
                     tablename);
        }
        SPI_finish();
    }

    INSTR_TIME_SET_CURRENT(t1);
    INSTR_TIME_SUBTRACT(t1, t0);
    ereport(LOG,
            (errmsg("incremental fold %s: " INT64_FORMAT " rows in, " INT64_FORMAT
                    " relations upserted, %.3f s%s",
                    tablename, n_rows, n_groups, INSTR_TIME_GET_DOUBLE(t1),
                    fresh ? " (fresh)" : "")));

    MemoryContextSwitchTo(oldcxt);
    MemoryContextDelete(fold_cxt);

    PG_RETURN_INT64(n_groups);
}
