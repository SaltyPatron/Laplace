/*
 * spi_common.h -- shared native-substrate helpers (single source of truth).
 *
 * Before 2026-06-10 every native file rolled its own copies of these:
 * copy_bytea_datum existed FIVE times, spi_realize twice (both carrying the
 * same nulls-string bug). Definitions are static inline so call sites keep
 * their historical names and nothing is added to the module symbol surface.
 *
 * SPI nulls-string LAW: a nulls string for SPI_execute_with_args must start
 * ALL-PRESENT ("   ") and mark 'n' per-parameter conditionally. Initializing
 * a slot to 'n' for a parameter that receives a real value silently NULLs it
 * (context arrays, session context, realize language and a LIMIT all shipped
 * broken that way once).
 *
 * All spi_* readers assume the caller is already SPI-connected and return
 * (Datum) 0 on miss.
 */
#ifndef LAPLACE_SPI_COMMON_H
#define LAPLACE_SPI_COMMON_H

#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/fmgrprotos.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"

/* ---- datum / hash utilities ------------------------------------------- */

static inline Datum
copy_bytea_datum(Datum d)
{
    bytea *src = DatumGetByteaPP(d);
    Size   len = VARSIZE_ANY(src);
    bytea *dst = (bytea *) palloc(len);

    memcpy(dst, src, len);
    return PointerGetDatum(dst);
}

static inline bool
bytea_eq(Datum a, Datum b)
{
    bytea *ba = DatumGetByteaPP(a);
    bytea *bb = DatumGetByteaPP(b);
    Size   la = VARSIZE_ANY_EXHDR(ba);
    Size   lb = VARSIZE_ANY_EXHDR(bb);

    if (la != lb)
        return false;
    return memcmp(VARDATA_ANY(ba), VARDATA_ANY(bb), la) == 0;
}

static inline hash128_t
datum_to_hash128(Datum d)
{
    bytea    *b = DatumGetByteaPP(d);
    hash128_t h;

    if (VARSIZE_ANY_EXHDR(b) < (int) sizeof(hash128_t))
        ereport(ERROR, (errmsg("laplace_substrate: expected 16-byte entity id")));
    memcpy(&h, VARDATA_ANY(b), sizeof(hash128_t));
    return h;
}

static inline Datum
hash128_to_datum(const hash128_t *h)
{
    bytea *b = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));

    SET_VARSIZE(b, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(b), h, sizeof(hash128_t));
    return PointerGetDatum(b);
}

static inline bool
hash128_eq(const hash128_t *a, const hash128_t *b)
{
    return a->hi == b->hi && a->lo == b->lo;
}

static inline Datum
eff_mu_display_numeric(int64 rating, int64 rd)
{
    /* the conservative-μ law has ONE compiled source (engine) — never re-spell
     * `rating - 2*rd` here; it drifted into five native copies once already */
    int64 eff = laplace_effective_mu_fp(rating, rd);
    Datum n = DirectFunctionCall1(int8_numeric, Int64GetDatum(eff));
    Datum b = DirectFunctionCall1(int8_numeric, Int64GetDatum(INT64CONST(1000000000)));
    Datum d = DirectFunctionCall2(numeric_div, n, b);

    return DirectFunctionCall2(numeric_round, d, Int32GetDatum(3));
}

static inline hash128_t
rel_type_id(const char *name)
{
    hash128_t id;

    if (laplace_relation_type_id(name, &id) < 0)
        ereport(ERROR, (errmsg("laplace_substrate: unknown relation type %s", name)));
    return id;
}

/* ---- single-value SPI reads ------------------------------------------- */

static inline Datum
spi_realize(Datum id, Datum lang)
{
    Oid     argtypes[2] = { BYTEAOID, BYTEAOID };
    Datum   args[2] = { id, lang };
    char    nulls[3] = "  ";
    bool    isnull;
    int     rc;

    if (lang == (Datum) 0)
        nulls[1] = 'n';
    rc = SPI_execute_with_args(
        "SELECT laplace.realize($1, $2)", 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static inline Datum
spi_label(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.label($1)", 1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static inline Datum
spi_type_label(Datum type_id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { type_id };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.type_label($1)", 1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static inline Datum
spi_word_language(Datum word)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { word };
    bool  isnull;
    int   rc;
    Datum d;

    rc = SPI_execute_with_args(
        "SELECT laplace.word_language($1)", 1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    /* word_language is a non-SRF: one row ALWAYS comes back, NULL-valued when
     * the word has no HAS_LANGUAGE consensus. Copying without the isnull check
     * detoasted Datum 0 — a backend AV (postmaster reset) on every is_a prompt
     * whose topic lacked a language (found via crashdump 2026-06-12). */
    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        return (Datum) 0;
    return copy_bytea_datum(d);
}

static inline Datum
spi_render_text(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.render_text($1)", 1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

/* synset ids for a word, copied out of the tuptable; returns count */
static inline int
spi_fetch_synset_ids(Datum word, Datum **out_ids, int *out_n)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { word };
    int   rc;

    *out_ids = NULL;
    *out_n = 0;
    rc = SPI_execute_with_args(
        "SELECT sn.synset_id FROM laplace.senses($1) sn",
        1, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "laplace_substrate: senses query failed: %s",
             SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return 0;
    *out_ids = (Datum *) palloc(sizeof(Datum) * SPI_processed);
    for (uint64 r = 0; r < SPI_processed; r++)
    {
        bool isnull;

        (*out_ids)[r] = copy_bytea_datum(
            SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 1, &isnull));
    }
    *out_n = (int) SPI_processed;
    return *out_n;
}

#endif                          /* LAPLACE_SPI_COMMON_H */
