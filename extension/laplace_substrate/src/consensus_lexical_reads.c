



#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_senses);
PG_FUNCTION_INFO_V1(pg_laplace_senses_context);
PG_FUNCTION_INFO_V1(pg_laplace_define);
PG_FUNCTION_INFO_V1(pg_laplace_define_context);
PG_FUNCTION_INFO_V1(pg_laplace_synonyms);
PG_FUNCTION_INFO_V1(pg_laplace_translations);
PG_FUNCTION_INFO_V1(pg_laplace_examples);

static void
emit_senses_rows(ReturnSetInfo *rsinfo, Datum word, Datum context_arr, bool has_context)
{
    hash128_t k_has_sense = rel_type_id("HAS_SENSE");
    hash128_t k_is_sense  = rel_type_id("IS_SENSE_OF");
    Oid       argtypes[3] = { BYTEAOID, BYTEAOID, BYTEAARRAYOID };
    Datum     args[3];
    int       rc;

    args[0] = word;
    args[1] = hash128_to_datum(&k_is_sense);
    args[2] = hash128_to_datum(&k_has_sense);
    if (has_context)
    {
        args[2] = context_arr;
        

        rc = SPI_execute_with_args(
            "SELECT s.object_id, ss.object_id, "
            "       laplace.eff_mu_display(s.rating, s.rd), "
            "       s.witness_count + ss.witness_count, "
            "       round(((laplace.eff_mu(s.rating, s.rd) + laplace.eff_mu(ss.rating, ss.rd) "
            "         + COALESCE((SELECT sum(laplace.eff_mu(c.rating, c.rd)) FROM laplace.consensus c "
            "                     WHERE c.subject_id = ANY ($3) AND c.object_id = ss.object_id "
            "                       AND NOT laplace.refuted(c.rating, c.rd)), 0) "
            "         + COALESCE((SELECT sum(laplace.eff_mu(c.rating, c.rd)) FROM laplace.consensus c "
            "                     WHERE c.subject_id = ss.object_id AND c.object_id = ANY ($3) "
            "                       AND NOT laplace.refuted(c.rating, c.rd)), 0)) / 1e9)::numeric, 3) "
            "FROM laplace.consensus s "
            "JOIN laplace.consensus ss ON ss.subject_id = s.object_id "
            "                         AND ss.type_id = $2 "
            "WHERE s.subject_id = $1 AND s.type_id = laplace.relation_type_id('HAS_SENSE') "
            "ORDER BY 5 DESC",
            3, argtypes, args, context_arr == (Datum) 0 ? "  n" : NULL, true, 0);
    }
    else
    {
        rc = SPI_execute_with_args(
            "SELECT s.object_id, ss.object_id, "
            "       laplace.eff_mu_display(s.rating, s.rd), "
            "       s.witness_count + ss.witness_count "
            "FROM laplace.consensus s "
            "JOIN laplace.consensus ss ON ss.subject_id = s.object_id "
            "                         AND ss.type_id = $2 "
            "WHERE s.subject_id = $1 AND s.type_id = laplace.relation_type_id('HAS_SENSE') "
            "ORDER BY laplace.eff_mu(s.rating, s.rd) + laplace.eff_mu(ss.rating, ss.rd) DESC",
            2, argtypes, args, NULL, true, 0);
    }
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_reads: senses query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[5];
        bool      nulls_out[5] = { false, false, false, false, false };
        bool      isnull;
        int       ncols = has_context ? 5 : 4;

        for (int c = 0; c < ncols; c++)
        {
            values[c] = SPI_getbinval(tup, td, c + 1, &isnull);
            nulls_out[c] = isnull;
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
}

Datum
pg_laplace_senses(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("senses: p_word must not be NULL")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "senses: SPI_connect failed");
    emit_senses_rows(rsinfo, PG_GETARG_DATUM(0), (Datum) 0, false);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_senses_context(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("senses: p_word must not be NULL")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "senses: SPI_connect failed");
    emit_senses_rows(rsinfo, PG_GETARG_DATUM(0),
                     PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1), true);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

static void
emit_define_rows(ReturnSetInfo *rsinfo, Datum word, Datum context_arr, bool has_context, int lim)
{
    int rc;

    if (has_context)
    {
        Oid   argtypes[3] = { BYTEAOID, BYTEAARRAYOID, INT4OID };
        Datum args[3];

        args[0] = word;
        args[1] = context_arr;
        args[2] = Int32GetDatum(lim);
        rc = SPI_execute_with_args(
            "SELECT laplace.render_text(g.object_id), "
            "       laplace.eff_mu_display(g.rating, g.rd), g.witness_count "
            "FROM laplace.senses($1, $2) sn "
            "JOIN laplace.consensus g ON g.subject_id = sn.synset_id "
            "                       AND g.type_id = laplace.relation_type_id('HAS_DEFINITION') "
            "ORDER BY sn.score + laplace.eff_mu_display(g.rating, g.rd) DESC "
            "LIMIT $3",
            3, argtypes, args, context_arr == (Datum) 0 ? " n " : NULL, true, 0);
    }
    else
    {
        Oid   argtypes[2] = { BYTEAOID, INT4OID };
        Datum args[2];

        args[0] = word;
        args[1] = Int32GetDatum(lim);
        rc = SPI_execute_with_args(
            "SELECT laplace.render_text(g.object_id), "
            "       laplace.eff_mu_display(g.rating, g.rd), g.witness_count "
            "FROM laplace.senses($1) sn "
            "JOIN laplace.consensus g ON g.subject_id = sn.synset_id "
            "                       AND g.type_id = laplace.relation_type_id('HAS_DEFINITION') "
            "ORDER BY sn.eff_mu + laplace.eff_mu_display(g.rating, g.rd) DESC "
            "LIMIT $2",
            2, argtypes, args, NULL, true, 0);
    }
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_reads: define query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[3];
        bool      nulls_out[3] = { false, false, false };
        bool      isnull;
        for (int c = 0; c < 3; c++)
        {
            values[c] = SPI_getbinval(tup, td, c + 1, &isnull);
            nulls_out[c] = isnull;
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
}

Datum
pg_laplace_define(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    int32 lim = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("define: p_word must not be NULL")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "define: SPI_connect failed");
    emit_define_rows(rsinfo, PG_GETARG_DATUM(0), (Datum) 0, false, lim);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_define_context(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    int32 lim = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("define: p_word must not be NULL")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "define: SPI_connect failed");
    emit_define_rows(rsinfo, PG_GETARG_DATUM(0),
                     PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1), true, lim);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_synonyms(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum word;
    int32 lim;
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("synonyms: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    lim = PG_ARGISNULL(1) ? 10 : PG_GETARG_INT32(1);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "synonyms: SPI_connect failed");

    args[0] = word;
    args[1] = Int32GetDatum(lim);
    rc = SPI_execute_with_args(
        "SELECT * FROM ( "
        "  SELECT DISTINCT ON (other.subject_id) "
        "         laplace.realize(other.subject_id, laplace.word_language($1)) AS synonym, "
        "         laplace.eff_mu_display(other.rating, other.rd) AS eff_mu, "
        "         other.witness_count AS witnesses "
        "  FROM laplace.consensus mine "
        "  JOIN laplace.consensus other ON other.object_id = mine.object_id "
        "                              AND other.type_id = laplace.relation_type_id('IS_SYNONYM_OF') "
        "                              AND other.subject_id <> $1 "
        "  WHERE mine.subject_id = $1 "
        "    AND mine.type_id = laplace.relation_type_id('IS_SYNONYM_OF') "
        "  ORDER BY other.subject_id, laplace.eff_mu(other.rating, other.rd) DESC "
        ") s WHERE NULLIF(btrim(s.synonym), '') IS NOT NULL "
        "ORDER BY s.eff_mu DESC, s.witnesses DESC LIMIT $2",
        2, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "synonyms: query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[3];
        bool      nulls_out[3] = { false, false, false };
        bool      isnull;
        for (int c = 0; c < 3; c++)
        {
            values[c] = SPI_getbinval(tup, td, c + 1, &isnull);
            nulls_out[c] = isnull;
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_translations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum word;
    int32 lim;
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("translations: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    lim = PG_ARGISNULL(1) ? 24 : PG_GETARG_INT32(1);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "translations: SPI_connect failed");

    args[0] = word;
    args[1] = Int32GetDatum(lim);
    rc = SPI_execute_with_args(
        "SELECT * FROM ( "
        "  SELECT DISTINCT ON (m.object_id) "
        "         laplace.render_text(m.object_id) AS translation, "
        "         laplace.label(laplace.word_language(m.object_id)) AS language, "
        "         laplace.eff_mu_display(m.rating, m.rd) AS eff_mu, "
        "         m.witness_count AS witnesses "
        "  FROM laplace.senses($1) sn "
        "  JOIN laplace.consensus m ON m.subject_id = sn.synset_id "
        "                          AND m.type_id = laplace.relation_type_id('IS_TRANSLATION_OF') "
        "                          AND m.object_id <> $1 "
        "  ORDER BY m.object_id, laplace.eff_mu(m.rating, m.rd) DESC "
        ") t ORDER BY t.witnesses DESC, t.eff_mu DESC LIMIT $2",
        2, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "translations: query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[4];
        bool      nulls_out[4] = { false, false, false, false };
        bool      isnull;
        for (int c = 0; c < 4; c++)
        {
            values[c] = SPI_getbinval(tup, td, c + 1, &isnull);
            nulls_out[c] = isnull;
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_examples(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum word;
    int32 lim;
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("examples: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    lim = PG_ARGISNULL(1) ? 5 : PG_GETARG_INT32(1);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "examples: SPI_connect failed");

    args[0] = word;
    args[1] = Int32GetDatum(lim);
    rc = SPI_execute_with_args(
        "SELECT laplace.render_text(g.object_id), "
        "       laplace.eff_mu_display(g.rating, g.rd), g.witness_count "
        "FROM laplace.senses($1) sn "
        "JOIN laplace.consensus g ON g.subject_id = sn.synset_id "
        "                        AND g.type_id = laplace.relation_type_id('HAS_EXAMPLE') "
        "ORDER BY sn.eff_mu + laplace.eff_mu_display(g.rating, g.rd) DESC "
        "LIMIT $2",
        2, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "examples: query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[3];
        bool      nulls_out[3] = { false, false, false };
        bool      isnull;
        for (int c = 0; c < 3; c++)
        {
            values[c] = SPI_getbinval(tup, td, c + 1, &isnull);
            nulls_out[c] = isnull;
        }
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
