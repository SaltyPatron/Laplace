/*
 * converse_reads_native.c — lexical / relational converse read SRFs.
 * Migrated from 20_converse.sql.in (route_prompt/respond/converse stay SQL).
 */
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
PG_FUNCTION_INFO_V1(pg_laplace_expansion);
PG_FUNCTION_INFO_V1(pg_laplace_related);
PG_FUNCTION_INFO_V1(pg_laplace_related_in);
PG_FUNCTION_INFO_V1(pg_laplace_describe);
PG_FUNCTION_INFO_V1(pg_laplace_usage_overlap);
PG_FUNCTION_INFO_V1(pg_laplace_reason);
PG_FUNCTION_INFO_V1(pg_laplace_relatedness);
PG_FUNCTION_INFO_V1(pg_laplace_gaps);

#define TAX_WALK_CAP 2048
#define REASON_LAT_N 11

/* shared datum/hash/SPI-read helpers live in spi_common.h */

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
        /* the nulls string must track the actual array: a constant "  n" here
         * silently nulled $3, making context re-ranking a no-op (2026-06-10) */
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
        elog(ERROR, "converse_reads: senses query failed");

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
        elog(ERROR, "converse_reads: define query failed");

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

Datum
pg_laplace_expansion(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum subjects, type_filter;
    int32 lim;
    Oid   argtypes[3] = { BYTEAARRAYOID, BYTEAOID, INT4OID };
    Datum args[3];
    /* "  nn" lied twice: $2 was stuck NULL even when a filter was passed, and
     * $3 (LIMIT) was permanently NULL = unbounded (2026-06-10) */
    char  nulls[4] = "   ";
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("expansion: p_subjects must not be NULL")));
    subjects = PG_GETARG_DATUM(0);
    type_filter = PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1);
    lim = PG_ARGISNULL(2) ? 40 : PG_GETARG_INT32(2);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "expansion: SPI_connect failed");

    args[0] = subjects;
    args[1] = type_filter;
    args[2] = Int32GetDatum(lim);
    if (type_filter == (Datum) 0)
        nulls[1] = 'n';

    rc = SPI_execute_with_args(
        "SELECT c.object_id, "
        "       count(DISTINCT c.subject_id)::int, "
        "       round((sum(laplace.eff_mu(c.rating, c.rd)) / 1e9)::numeric, 3) "
        "FROM laplace.consensus c "
        "WHERE c.subject_id = ANY ($1) "
        "  AND c.object_id IS NOT NULL "
        "  AND NOT laplace.refuted(c.rating, c.rd) "
        "  AND ($2 IS NULL OR c.type_id = $2) "
        "GROUP BY c.object_id "
        "ORDER BY count(DISTINCT c.subject_id) DESC, sum(laplace.eff_mu(c.rating, c.rd)) DESC "
        "LIMIT $3",
        3, argtypes, args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "expansion: query failed");

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

static void
emit_related_rows(ReturnSetInfo *rsinfo, Datum word, Datum type_id, Datum lang, int lim, bool incoming)
{
    Oid     argtypes[4] = { BYTEAOID, BYTEAOID, BYTEAOID, INT4OID };
    Datum   args[4];
    char    nulls[5] = "    ";
    const char *sql_out =
        "WITH subj(id) AS ( "
        "  SELECT $1 UNION SELECT sn.synset_id FROM laplace.senses($1) sn "
        "), top AS ( "
        "  SELECT cc.object_id, cc.rating, cc.rd, cc.witness_count "
        "  FROM subj s CROSS JOIN LATERAL ( "
        "    SELECT c.object_id, c.rating, c.rd, c.witness_count "
        "    FROM laplace.consensus c "
        "    WHERE c.subject_id = s.id AND c.type_id = $2 "
        "      AND c.object_id IS NOT NULL AND NOT laplace.refuted(c.rating, c.rd) "
        "    ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT $4 "
        "  ) cc "
        ") "
        "SELECT laplace.realize(t.object_id, COALESCE($3, laplace.word_language($1))), "
        "       laplace.eff_mu_display(t.rating, t.rd), t.witness_count "
        "FROM top t ORDER BY laplace.eff_mu(t.rating, t.rd) DESC LIMIT $4";
    const char *sql_in =
        "WITH subj(id) AS ( "
        "  SELECT $1 UNION SELECT sn.synset_id FROM laplace.senses($1) sn "
        "), top AS ( "
        "  SELECT cc.subject_id, cc.rating, cc.rd, cc.witness_count "
        "  FROM subj s CROSS JOIN LATERAL ( "
        "    SELECT c.subject_id, c.rating, c.rd, c.witness_count "
        "    FROM laplace.consensus c "
        "    WHERE c.object_id = s.id AND c.type_id = $2 "
        "      AND NOT laplace.refuted(c.rating, c.rd) "
        "    ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT $4 "
        "  ) cc "
        ") "
        "SELECT laplace.realize(t.subject_id, COALESCE($3, laplace.word_language($1))), "
        "       laplace.eff_mu_display(t.rating, t.rd), t.witness_count "
        "FROM top t ORDER BY laplace.eff_mu(t.rating, t.rd) DESC LIMIT $4";
    int rc;

    args[0] = word;
    args[1] = type_id;
    args[2] = lang;
    args[3] = Int32GetDatum(lim);
    if (lang == (Datum) 0)
        nulls[2] = 'n';

    rc = SPI_execute_with_args(incoming ? sql_in : sql_out, 4, argtypes, args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "converse_reads: related query failed");

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
pg_laplace_related(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("related: p_word and p_type required")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "related: SPI_connect failed");
    emit_related_rows(rsinfo, PG_GETARG_DATUM(0), PG_GETARG_DATUM(1),
                      PG_ARGISNULL(2) ? (Datum) 0 : PG_GETARG_DATUM(2),
                      PG_ARGISNULL(3) ? 10 : PG_GETARG_INT32(3), false);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_related_in(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("related_in: p_word and p_type required")));
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "related_in: SPI_connect failed");
    emit_related_rows(rsinfo, PG_GETARG_DATUM(0), PG_GETARG_DATUM(1),
                      PG_ARGISNULL(2) ? (Datum) 0 : PG_GETARG_DATUM(2),
                      PG_ARGISNULL(3) ? 10 : PG_GETARG_INT32(3), true);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

Datum
pg_laplace_describe(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum word, lang;
    int32 lim;
    Oid   argtypes[3] = { BYTEAOID, BYTEAOID, INT4OID };
    Datum args[3];
    char  nulls[4] = "   ";
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("describe: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    lang = PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1);
    lim = PG_ARGISNULL(2) ? 24 : PG_GETARG_INT32(2);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "describe: SPI_connect failed");

    args[0] = word;
    args[1] = lang;
    args[2] = Int32GetDatum(lim);
    if (lang == (Datum) 0)
        nulls[1] = 'n';

    rc = SPI_execute_with_args(
        "WITH subj(id) AS ( "
        "  SELECT $1 UNION SELECT sn.synset_id FROM laplace.senses($1) sn "
        "), top AS ( "
        "  SELECT cc.type_id, cc.object_id, cc.rating, cc.rd, cc.witness_count "
        "  FROM subj s CROSS JOIN LATERAL ( "
        "    SELECT c.type_id, c.object_id, c.rating, c.rd, c.witness_count "
        "    FROM laplace.consensus c "
        "    WHERE c.subject_id = s.id AND c.object_id IS NOT NULL "
        "      AND NOT laplace.refuted(c.rating, c.rd) "
        "      AND laplace.render(c.type_id) LIKE 'substrate/type/%' "
        "      AND NOT laplace.relation_type_in_family(c.type_id, 'HAS_POS') "
        "      AND c.type_id NOT IN ( "
        "        laplace.relation_type_id('HAS_SENSE'), laplace.relation_type_id('IS_SENSE_OF'), "
        "        laplace.relation_type_id('HAS_LANGUAGE'), laplace.relation_type_id('PRECEDES'), "
        "        laplace.relation_type_id('FOLLOWS'), laplace.relation_type_id('CO_OCCURS_WITH'), "
        "        laplace.relation_type_id('OCCURS_IN_CONTEXT'), laplace.relation_type_id('HAS_LEX_CATEGORY'), "
        "        laplace.relation_type_id('HAS_FEATURE'), laplace.relation_type_id('HAS_VERB_FRAME'), "
        "        laplace.relation_type_id('IS_LEMMA_OF')) "
        "    ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT $3 * 3 "
        "  ) cc "
        ") "
        "SELECT d.type, d.fact, d.eff_mu, d.witnesses FROM ( "
        "  SELECT laplace.type_label(t.type_id) AS type, "
        "         laplace.realize(t.object_id, COALESCE($2, laplace.word_language($1))) AS fact, "
        "         laplace.eff_mu_display(t.rating, t.rd) AS eff_mu, t.witness_count AS witnesses, "
        "         laplace.eff_mu(t.rating, t.rd) AS sort_mu "
        "  FROM top t "
        ") d "
        "WHERE d.fact IS NOT NULL AND d.fact <> '' "
        "  AND right(d.fact, 1) <> U&'\\2026' AND right(d.type, 1) <> U&'\\2026' "
        "ORDER BY d.sort_mu DESC LIMIT $3",
        3, argtypes, args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "describe: query failed");

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
pg_laplace_usage_overlap(PG_FUNCTION_ARGS)
{
    Datum x, y;
    Oid   argtypes[2] = { BYTEAOID, BYTEAOID };
    Datum args[2];
    int   rc;
    int64 count = 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("usage_overlap: endpoints required")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "usage_overlap: SPI_connect failed");
    args[0] = x;
    args[1] = y;
    rc = SPI_execute_with_args(
        "WITH xn(n) AS ( "
        "  SELECT object_id FROM laplace.consensus "
        "   WHERE subject_id = $1 AND type_id = laplace.relation_type_id('PRECEDES') "
        "  UNION SELECT subject_id FROM laplace.consensus "
        "   WHERE object_id = $1 AND type_id = laplace.relation_type_id('PRECEDES')), "
        "yn(n) AS ( "
        "  SELECT object_id FROM laplace.consensus "
        "   WHERE subject_id = $2 AND type_id = laplace.relation_type_id('PRECEDES') "
        "  UNION SELECT subject_id FROM laplace.consensus "
        "   WHERE object_id = $2 AND type_id = laplace.relation_type_id('PRECEDES')) "
        "SELECT count(*)::bigint FROM xn JOIN yn USING (n)",
        2, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        elog(ERROR, "usage_overlap: query failed");
    {
        bool isnull;
        count = DatumGetInt64(
            SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
    }
    laplace_spi_finish(spi_top);
    PG_RETURN_INT64(count);
}

Datum
pg_laplace_reason(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum x, y;
    int32 depth;
    Oid   argtypes[3] = { BYTEAOID, BYTEAOID, INT4OID };
    Datum args[3];
    int   rc;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("reason: endpoints required")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    depth = PG_ARGISNULL(2) ? 7 : PG_GETARG_INT32(2);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "reason: SPI_connect failed");

    args[0] = x;
    args[1] = y;
    args[2] = Int32GetDatum(depth);
    rc = SPI_execute_with_args(
        "WITH RECURSIVE "
        "lat(k) AS (SELECT laplace.relation_type_id(n) FROM unnest(ARRAY[ "
        "  'IS_SYNONYM_OF','IS_SIMILAR_TO','IS_ANTONYM_OF','HAS_PART','HAS_MEMBER', "
        "  'HAS_SUBSTANCE','DERIVATIONALLY_RELATED','PERTAINS_TO','ALSO_SEE', "
        "  'IN_VERB_GROUP_WITH','HAS_ATTRIBUTE']) n), "
        "up_k(k) AS (SELECT laplace.relation_type_id(n) FROM unnest(ARRAY['IS_A','IS_INSTANCE_OF']) n), "
        "x_top AS (SELECT sn.synset_id FROM laplace.senses($1) sn "
        "          ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1), "
        "y_top AS (SELECT sn.synset_id FROM laplace.senses($2) sn "
        "          ORDER BY sn.eff_mu DESC NULLS LAST LIMIT 1), "
        "xset(id) AS (SELECT $1 UNION SELECT synset_id FROM x_top), "
        "yset(id) AS (SELECT $2 UNION SELECT synset_id FROM y_top), "
        "direct AS ( "
        "  SELECT laplace.realize_path(ARRAY[x.id, c.object_id], ARRAY[c.type_id], ARRAY[1], "
        "                              COALESCE(laplace.word_language($1), laplace.word_language($2))) AS chain, "
        "         laplace.eff_mu_display(c.rating, c.rd) AS mu, 1 AS len, "
        "         laplace.type_label(c.type_id) AS plane "
        "  FROM xset x JOIN laplace.consensus c ON c.subject_id = x.id "
        "    AND c.object_id IN (SELECT id FROM yset) "
        "    AND c.type_id IN (SELECT k FROM lat) AND NOT laplace.refuted(c.rating, c.rd) "
        "  UNION ALL "
        "  SELECT laplace.realize_path(ARRAY[x.id, c.subject_id], ARRAY[c.type_id], ARRAY[-1], "
        "                              COALESCE(laplace.word_language($1), laplace.word_language($2))), "
        "         laplace.eff_mu_display(c.rating, c.rd), 1, laplace.type_label(c.type_id) "
        "  FROM xset x JOIN laplace.consensus c ON c.object_id = x.id "
        "    AND c.subject_id IN (SELECT id FROM yset) "
        "    AND c.type_id IN (SELECT k FROM lat) AND NOT laplace.refuted(c.rating, c.rd) "
        "), "
        "ux(node, path, types, mu, d) AS ( "
        "  SELECT id, ARRAY[id], ARRAY[]::bytea[], NULL::numeric, 0 FROM xset "
        "  UNION ALL "
        "  SELECT c.object_id, u.path || c.object_id, u.types || c.type_id, "
        "         LEAST(COALESCE(u.mu, laplace.eff_mu_display(c.rating, c.rd)), "
        "               laplace.eff_mu_display(c.rating, c.rd)), u.d + 1 "
        "  FROM ux u JOIN laplace.consensus c ON c.subject_id = u.node "
        "    AND c.type_id IN (SELECT k FROM up_k) AND c.object_id IS NOT NULL "
        "    AND NOT laplace.refuted(c.rating, c.rd) AND NOT (c.object_id = ANY (u.path)) "
        "  WHERE u.d < $3 "
        "), "
        "uy(node, path, types, mu, d) AS ( "
        "  SELECT id, ARRAY[id], ARRAY[]::bytea[], NULL::numeric, 0 FROM yset "
        "  UNION ALL "
        "  SELECT c.object_id, u.path || c.object_id, u.types || c.type_id, "
        "         LEAST(COALESCE(u.mu, laplace.eff_mu_display(c.rating, c.rd)), "
        "               laplace.eff_mu_display(c.rating, c.rd)), u.d + 1 "
        "  FROM uy u JOIN laplace.consensus c ON c.subject_id = u.node "
        "    AND c.type_id IN (SELECT k FROM up_k) AND c.object_id IS NOT NULL "
        "    AND NOT laplace.refuted(c.rating, c.rd) AND NOT (c.object_id = ANY (u.path)) "
        "  WHERE u.d < $3 "
        "), "
        "lca_pick AS ( "
        "  SELECT ux.path AS xp, ux.types AS xk, uy.path AS yp, uy.types AS yk, "
        "         LEAST(ux.mu, uy.mu) AS mu, ux.d + uy.d AS len "
        "  FROM ux JOIN uy ON ux.node = uy.node "
        "  WHERE ux.d > 0 OR uy.d > 0 "
        "  ORDER BY ux.d + uy.d, LEAST(ux.mu, uy.mu) DESC NULLS LAST LIMIT 1 "
        "), "
        "lca AS ( "
        "  SELECT laplace.realize_path( "
        "             xp || array_reverse(yp[1:cardinality(yp)-1]), "
        "             xk || array_reverse(yk), "
        "             (SELECT COALESCE(array_agg(1), ARRAY[]::int[]) FROM generate_subscripts(xk, 1)) "
        "                 || (SELECT COALESCE(array_agg(-1), ARRAY[]::int[]) FROM generate_subscripts(yk, 1)), "
        "             COALESCE(laplace.word_language($1), laplace.word_language($2))) AS chain, "
        "         mu, len, 'taxonomy'::text AS plane FROM lca_pick "
        ") "
        "SELECT a.chain, a.mu, a.plane FROM ( "
        "  SELECT chain, mu, len, plane FROM direct "
        "  UNION ALL SELECT chain, mu, len, plane FROM lca "
        ") a ORDER BY a.len, a.mu DESC NULLS LAST LIMIT 1",
        3, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "reason: query failed");

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
pg_laplace_relatedness(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum x, y;
    Oid   argtypes[2] = { BYTEAOID, BYTEAOID };
    Datum args[2];
    int   rc;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("relatedness: endpoints required")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    InitMaterializedSRF(fcinfo, 0);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "relatedness: SPI_connect failed");

    args[0] = x;
    args[1] = y;
    rc = SPI_execute_with_args(
        "SELECT r.chain, r.plane, r.path_mu, COALESCE(u.n, 0), g.geo, "
        "       concat_ws('', "
        "         CASE WHEN r.chain IS NOT NULL THEN 'related via ' || r.plane "
        "              ELSE 'no witnessed conceptual path' END, "
        "         CASE WHEN COALESCE(u.n, 0) >= 10 THEN '; strong shared usage (' || u.n || ')' "
        "              WHEN COALESCE(u.n, 0) > 0 THEN '; some shared usage (' || u.n || ')' "
        "              ELSE '' END, "
        "         CASE WHEN g.geo IS NOT NULL AND g.geo < 0.4 THEN '; structurally near' "
        "              ELSE '' END) "
        "FROM (SELECT 1) one "
        "LEFT JOIN LATERAL (SELECT * FROM laplace.reason($1, $2) LIMIT 1) r ON true "
        "LEFT JOIN LATERAL (SELECT laplace.usage_overlap($1, $2) AS n) u ON true "
        "LEFT JOIN LATERAL ( "
        "  SELECT public.laplace_angular_distance_4d( "
        "    (SELECT coord FROM laplace.physicalities WHERE entity_id = $1 AND type = 1 "
        "     AND coord IS NOT NULL LIMIT 1), "
        "    (SELECT coord FROM laplace.physicalities WHERE entity_id = $2 AND type = 1 "
        "     AND coord IS NOT NULL LIMIT 1)) AS geo) g ON true",
        2, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "relatedness: query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[6];
        bool      nulls_out[6] = { false, false, false, false, false, false };
        bool      isnull;
        for (int c = 0; c < 6; c++)
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
pg_laplace_gaps(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum word;
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1];
    int   rc;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("gaps: p_word must not be NULL")));
    word = PG_GETARG_DATUM(0);
    /* RETURNS TABLE with ONE column is SETOF scalar to PG, not a rowtype —
     * the default InitMaterializedSRF path then errors "return type must be
     * a row type". Use the caller's expected descriptor instead. */
    InitMaterializedSRF(fcinfo, MAT_SRF_USE_EXPECTED_DESC);
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "gaps: SPI_connect failed");

    args[0] = word;
    rc = SPI_execute_with_args(
        "WITH subj(id) AS ( "
        "  SELECT $1 UNION SELECT sn.synset_id FROM laplace.senses($1) sn "
        "), expected(name) AS (SELECT unnest(ARRAY[ "
        "  'IS_A','HAS_PART','HAS_MEMBER','CAUSES','USED_FOR','IS_ANTONYM_OF', "
        "  'IS_SIMILAR_TO','HAS_ATTRIBUTE','DERIVATIONALLY_RELATED'])) "
        "SELECT e.name FROM expected e "
        "WHERE NOT EXISTS ( "
        "  SELECT 1 FROM laplace.consensus c JOIN subj s ON c.subject_id = s.id "
        "  WHERE c.type_id = laplace.relation_type_id(e.name) "
        "    AND NOT laplace.refuted(c.rating, c.rd))",
        1, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "gaps: query failed");

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        Datum     values[1];
        bool      nulls_out[1] = { false };
        bool      isnull;
        values[0] = SPI_getbinval(tup, td, 1, &isnull);
        nulls_out[0] = isnull;
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls_out);
    }
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
