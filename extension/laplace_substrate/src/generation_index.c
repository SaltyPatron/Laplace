/*
 * generation_index.c — native content_index lifecycle (rebuild procedures).
 *
 * Replaces the batched plpgsql rebuild_content_index / rebuild_content_index_deep
 * bodies with SPI-driven DDL+DML. Batch commits match the resumable plpgsql law.
 */
#include "postgres.h"

#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

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

static Datum
copy_bytea_datum(Datum d)
{
    bytea *src = DatumGetByteaPP(d);
    Size   len = VARSIZE_ANY(src);
    bytea *dst = (bytea *) palloc(len);

    memcpy(dst, src, len);
    return PointerGetDatum(dst);
}

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
        "LATERAL laplace.laplace_mantissa_unpack(dp.geom) u "
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
        "LATERAL laplace.laplace_mantissa_unpack(dp.geom) u";

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

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "rebuild_content_index: SPI_connect failed");

    rebuild_content_index_impl(batch);
    SPI_finish();
    PG_RETURN_VOID();
}

Datum
pg_laplace_rebuild_content_index_deep(PG_FUNCTION_ARGS)
{
    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "rebuild_content_index_deep: SPI_connect failed");

    rebuild_content_index_deep_impl();
    SPI_finish();
    PG_RETURN_VOID();
}
