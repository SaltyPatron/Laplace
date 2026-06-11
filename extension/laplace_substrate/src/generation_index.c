/*
 * generation_index.c — native content_index lifecycle (rebuild procedures).
 *
 * Replaces the batched plpgsql rebuild_content_index / rebuild_content_index_deep
 * bodies with SPI-driven DDL+DML. Batch commits match the resumable plpgsql law.
 */
#include "postgres.h"

#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"

#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_rebuild_content_index);
PG_FUNCTION_INFO_V1(pg_laplace_rebuild_content_index_deep);

static void
spi_exec(const char *query)
{
    int rc = SPI_execute(query, false, 0);

    if (rc < 0)
        elog(ERROR, "rebuild_content_index: %s failed: %s",
             query, SPI_result_code_string(rc));
}

static void
spi_cache_reset(void)
{
    int rc = SPI_execute("SELECT laplace.generation_cache_reset()", false, 1);

    if (rc != SPI_OK_SELECT)
        elog(ERROR, "rebuild_content_index: generation_cache_reset failed: %s",
             SPI_result_code_string(rc));
}

static Datum
empty_bytea(void)
{
    bytea *b = (bytea *) palloc(VARHDRSZ);

    SET_VARSIZE(b, VARHDRSZ);
    return PointerGetDatum(b);
}

/* copy_bytea_datum lives in spi_common.h */

static void
rebuild_content_index_impl(int32 batch)
{
    static const char *BATCH_SQL =
        "SELECT max(entity_id), count(*)::int FROM ("
        "  SELECT DISTINCT p.entity_id "
        "  FROM laplace.physicalities p "
        "  JOIN laplace.entities e ON e.id = p.entity_id AND e.tier = 3 "
        "  WHERE p.type = 1 AND p.trajectory IS NOT NULL AND p.entity_id > $1 "
        "  ORDER BY p.entity_id "
        "  LIMIT $2"
        ") z";

    static const char *INSERT_SQL =
        "INSERT INTO laplace.content_index (seq_id, token, pos) "
        "SELECT s.entity_id, u.entity_id, "
        "       row_number() OVER (PARTITION BY s.entity_id ORDER BY dp.path[1])::int "
        "FROM ("
        "  SELECT DISTINCT ON (p.entity_id) p.entity_id, p.trajectory "
        "  FROM laplace.physicalities p "
        "  JOIN laplace.entities e ON e.id = p.entity_id AND e.tier = 3 "
        "  WHERE p.type = 1 AND p.trajectory IS NOT NULL "
        "    AND p.entity_id > $1 AND p.entity_id <= $2"
        ") s, "
        "LATERAL public.ST_DumpPoints(s.trajectory) dp, "
        "LATERAL public.laplace_mantissa_unpack(dp.geom) u "
        "WHERE EXISTS ("
        "  SELECT 1 FROM laplace.consensus c "
        "  WHERE c.type_id = laplace.relation_type_id('PRECEDES') "
        "    AND (c.subject_id = u.entity_id OR c.object_id = u.entity_id) "
        "  LIMIT 1)";

    Oid     argtypes[2] = { BYTEAOID, INT4OID };
    Datum   last_id = empty_bytea();

    spi_exec("DROP TABLE IF EXISTS laplace.content_index");
    spi_exec("CREATE TABLE laplace.content_index "
             "(seq_id bytea NOT NULL, token bytea NOT NULL, pos int NOT NULL)");

    for (;;)
    {
        Datum  args[2];
        char   nulls[3] = "  ";
        int    rc;
        bool   isnull;
        Datum  vmax;
        int32  vcnt;

        args[0] = last_id;
        args[1] = Int32GetDatum(batch);

        rc = SPI_execute_with_args(BATCH_SQL, 2, argtypes, args, nulls, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
            elog(ERROR, "rebuild_content_index: batch probe failed: %s",
                 SPI_result_code_string(rc));

        vmax = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        vcnt = DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[0],
                                            SPI_tuptable->tupdesc, 2, &isnull));
        if (vcnt == 0)
            break;

        {
            Oid  ins_types[2] = { BYTEAOID, BYTEAOID };
            Datum ins_args[2];
            char  ins_nulls[3] = "  ";

            ins_args[0] = last_id;
            ins_args[1] = isnull ? (Datum) 0 : copy_bytea_datum(vmax);
            if (isnull)
                ins_nulls[1] = 'n';

            rc = SPI_execute_with_args(INSERT_SQL, 2, ins_types, ins_args, ins_nulls,
                                       false, 0);
            if (rc != SPI_OK_INSERT)
                elog(ERROR, "rebuild_content_index: batch insert failed: %s",
                     SPI_result_code_string(rc));
        }

        SPI_commit();
        last_id = isnull ? empty_bytea() : copy_bytea_datum(vmax);
    }

    spi_exec("CREATE INDEX content_index_seq ON laplace.content_index(seq_id, pos)");
    spi_exec("CREATE INDEX content_index_tok ON laplace.content_index(token, pos)");
    spi_exec("ANALYZE laplace.content_index");
    spi_cache_reset();
}

static void
rebuild_content_index_deep_impl(void)
{
    static const char *CONSTITUENCY_SQL =
        "CREATE TABLE laplace.constituency_edge AS "
        "SELECT p.entity_id AS parent, u.entity_id AS child, "
        "       u.ordinal::int AS ord, GREATEST(u.run_length, 1)::int AS run "
        "FROM (SELECT DISTINCT ON (entity_id) entity_id, trajectory "
        "      FROM laplace.physicalities "
        "      WHERE type = 1 AND trajectory IS NOT NULL) p "
        "JOIN laplace.entities e ON e.id = p.entity_id AND e.tier > 2, "
        "LATERAL public.ST_DumpPoints(p.trajectory) dp, "
        "LATERAL public.laplace_mantissa_unpack(dp.geom) u";

    static const char *INSERT_SQL =
        "INSERT INTO laplace.content_index (seq_id, token, pos) "
        "WITH RECURSIVE roots AS ("
        "  SELECT DISTINCT p.parent AS id "
        "  FROM laplace.constituency_edge p "
        "  WHERE NOT EXISTS ("
        "    SELECT 1 FROM laplace.constituency_edge c WHERE c.child = p.parent)"
        "), walk AS ("
        "  SELECT r.id AS seq_id, t.child AS node, ARRAY[t.ord, g.i] AS path "
        "  FROM roots r "
        "  JOIN laplace.constituency_edge t ON t.parent = r.id, "
        "  LATERAL generate_series(1, t.run) g(i) "
        "  UNION ALL "
        "  SELECT w.seq_id, t.child, w.path || t.ord || g.i "
        "  FROM walk w "
        "  JOIN laplace.constituency_edge t ON t.parent = w.node, "
        "  LATERAL generate_series(1, t.run) g(i)"
        ") "
        "SELECT w.seq_id, w.node, "
        "       row_number() OVER (PARTITION BY w.seq_id ORDER BY w.path)::int "
        "FROM walk w "
        "WHERE NOT EXISTS ("
        "  SELECT 1 FROM laplace.constituency_edge t WHERE t.parent = w.node)";

    spi_exec("DROP TABLE IF EXISTS laplace.constituency_edge");
    spi_exec(CONSTITUENCY_SQL);
    spi_exec("CREATE INDEX constituency_edge_parent ON laplace.constituency_edge(parent)");
    spi_exec("ANALYZE laplace.constituency_edge");
    SPI_commit();

    spi_exec("DROP TABLE IF EXISTS laplace.content_index");
    spi_exec("CREATE TABLE laplace.content_index "
             "(seq_id bytea NOT NULL, token bytea NOT NULL, pos int NOT NULL)");
    spi_exec(INSERT_SQL);
    spi_exec("CREATE INDEX content_index_seq ON laplace.content_index(seq_id, pos)");
    spi_exec("CREATE INDEX content_index_tok ON laplace.content_index(token, pos)");
    spi_exec("ANALYZE laplace.content_index");
    spi_cache_reset();
}

Datum
pg_laplace_rebuild_content_index(PG_FUNCTION_ARGS)
{
    int32 batch = PG_ARGISNULL(0) ? 20000 : PG_GETARG_INT32(0);

    if (batch < 1)
        ereport(ERROR, (errmsg("rebuild_content_index: batch must be >= 1")));

    /* nonatomic: the rebuild COMMITs per batch page (memory-bounded, resumable) */
    if (SPI_connect_ext(SPI_OPT_NONATOMIC) != SPI_OK_CONNECT)
        elog(ERROR, "rebuild_content_index: SPI_connect failed");

    rebuild_content_index_impl(batch);
    SPI_finish();
    PG_RETURN_VOID();
}

Datum
pg_laplace_rebuild_content_index_deep(PG_FUNCTION_ARGS)
{
    /* nonatomic: edge build + index fill COMMIT between phases */
    if (SPI_connect_ext(SPI_OPT_NONATOMIC) != SPI_OK_CONNECT)
        elog(ERROR, "rebuild_content_index_deep: SPI_connect failed");

    rebuild_content_index_deep_impl();
    SPI_finish();
    PG_RETURN_VOID();
}

/*
 * content_pairs_scan(max_gap) — the native build behind the trajectory planes.
 *
 * The SQL self-join formulation materializes positions × max_gap intermediate
 * rows through executor nodes; this is one ordered scan of content_index with a
 * sliding window and an in-memory hash aggregation (engine computes, SQL
 * orchestrates — the same law as the bulk fold). Emits (gap, subject, object,
 * cnt); per-(subject,gap) totals are a window aggregate in the caller.
 */
PG_FUNCTION_INFO_V1(pg_laplace_content_pairs_scan);

typedef struct ContentPairKey
{
    uint8 subject[16];
    uint8 object[16];
    int32 gap;
} ContentPairKey;

typedef struct ContentPairEntry
{
    ContentPairKey key;            /* dynahash requires key first */
    int64          cnt;
} ContentPairEntry;

Datum
pg_laplace_content_pairs_scan(PG_FUNCTION_ARGS)
{
    int32           max_gap = PG_GETARG_INT32(0);
    ReturnSetInfo*  rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    TupleDesc       tupdesc;
    Tuplestorestate* tupstore;
    MemoryContext   per_query;
    MemoryContext   oldctx;
    HASHCTL         hctl;
    HTAB*           pairs;
    Portal          portal;
    uint8           win_tok[64][16];
    uint8           cur_seq[16];
    int             win_len = 0;
    bool            have_seq = false;
    HASH_SEQ_STATUS seq;
    ContentPairEntry* e;

    if (max_gap < 1 || max_gap > 64)
        ereport(ERROR, (errmsg("content_pairs_scan: max_gap must be 1..64 (got %d)", max_gap)));
    if (rsinfo == NULL || !IsA(rsinfo, ReturnSetInfo) ||
        (rsinfo->allowedModes & SFRM_Materialize) == 0)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("content_pairs_scan: set-valued function called in context "
                    "that cannot accept a set")));

    per_query = rsinfo->econtext->ecxt_per_query_memory;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR, (errmsg("content_pairs_scan: return type must be a row type")));

    oldctx = MemoryContextSwitchTo(per_query);
    tupstore = tuplestore_begin_heap(false, false, work_mem);
    rsinfo->returnMode = SFRM_Materialize;
    rsinfo->setResult = tupstore;
    rsinfo->setDesc = CreateTupleDescCopy(tupdesc);
    MemoryContextSwitchTo(oldctx);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize = sizeof(ContentPairKey);
    hctl.entrysize = sizeof(ContentPairEntry);
    hctl.hcxt = CurrentMemoryContext;
    pairs = hash_create("content_pairs_scan", 4 * 1024 * 1024, &hctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "content_pairs_scan: SPI_connect failed");

    portal = SPI_cursor_open_with_args(
        "content_pairs_scan",
        "SELECT seq_id, token FROM laplace.content_index ORDER BY seq_id, pos",
        0, NULL, NULL, NULL, true, 0);

    for (;;)
    {
        uint64 i;

        SPI_cursor_fetch(portal, true, 100000);
        if (SPI_processed == 0)
            break;

        for (i = 0; i < SPI_processed; i++)
        {
            HeapTuple   tup = SPI_tuptable->vals[i];
            TupleDesc   td  = SPI_tuptable->tupdesc;
            bool        null1, null2;
            bytea*      seq_b = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &null1));
            bytea*      tok_b = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &null2));
            const uint8* seq;
            const uint8* tok;
            int         d;

            if (null1 || null2 ||
                VARSIZE_ANY_EXHDR(seq_b) != 16 || VARSIZE_ANY_EXHDR(tok_b) != 16)
                ereport(ERROR, (errmsg("content_pairs_scan: content_index ids must be 16 bytes")));
            seq = (const uint8 *) VARDATA_ANY(seq_b);
            tok = (const uint8 *) VARDATA_ANY(tok_b);

            if (!have_seq || memcmp(seq, cur_seq, 16) != 0)
            {
                memcpy(cur_seq, seq, 16);
                have_seq = true;
                win_len = 0;
            }

            for (d = 1; d <= win_len; d++)
            {
                ContentPairKey   key;
                ContentPairEntry* ent;
                bool             found;

                /* HASH_BLOBS hashes/compares the full keysize — padding included */
                memset(&key, 0, sizeof(key));
                memcpy(key.subject, win_tok[(win_len - d) % 64], 16);
                memcpy(key.object, tok, 16);
                key.gap = d;
                ent = (ContentPairEntry *) hash_search(pairs, &key, HASH_ENTER, &found);
                if (!found)
                    ent->cnt = 0;
                ent->cnt++;
            }

            /* slide: the window holds the last max_gap tokens of this sequence */
            if (win_len < max_gap)
            {
                memcpy(win_tok[win_len % 64], tok, 16);
                win_len++;
            }
            else
            {
                int j;
                for (j = 0; j < max_gap - 1; j++)
                    memcpy(win_tok[j % 64], win_tok[(j + 1) % 64], 16);
                memcpy(win_tok[(max_gap - 1) % 64], tok, 16);
            }
        }
        SPI_freetuptable(SPI_tuptable);
    }
    SPI_cursor_close(portal);
    SPI_finish();

    hash_seq_init(&seq, pairs);
    while ((e = (ContentPairEntry *) hash_seq_search(&seq)) != NULL)
    {
        Datum  values[4];
        bool   nulls[4] = { false, false, false, false };
        bytea* s = (bytea *) palloc(VARHDRSZ + 16);
        bytea* o = (bytea *) palloc(VARHDRSZ + 16);

        SET_VARSIZE(s, VARHDRSZ + 16);
        memcpy(VARDATA(s), e->key.subject, 16);
        SET_VARSIZE(o, VARHDRSZ + 16);
        memcpy(VARDATA(o), e->key.object, 16);

        values[0] = Int32GetDatum(e->key.gap);
        values[1] = PointerGetDatum(s);
        values[2] = PointerGetDatum(o);
        values[3] = Int64GetDatum(e->cnt);
        tuplestore_putvalues(tupstore, rsinfo->setDesc, values, nulls);
        pfree(s);
        pfree(o);
    }
    hash_destroy(pairs);

    return (Datum) 0;
}
