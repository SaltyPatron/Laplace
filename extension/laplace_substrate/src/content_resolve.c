






#include "postgres.h"

#include "fmgr.h"
#include "funcapi.h"
#include "catalog/pg_collation.h"
#include "utils/builtins.h"
#include "utils/formatting.h"

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"

#include "perfcache_native.h"
#include "spi_common.h"
#include "spi_nested.h"









typedef struct
{
    ReturnSetInfo *rsinfo;
} word_seg_ctx;

static void
word_seg_emit(void *ctx_, uint32_t ordinal,
              const uint8_t *word_utf8, uint32_t word_len,
              const hash128_t *id)
{
    word_seg_ctx *ctx = (word_seg_ctx *) ctx_;
    Datum         values[3];
    bool          nulls[3] = { false, false, false };

    values[0] = Int32GetDatum((int32) ordinal);
    values[1] = PointerGetDatum(
        cstring_to_text_with_len((const char *) word_utf8, (int) word_len));
    values[2] = hash128_to_datum(id);
    tuplestore_putvalues(ctx->rsinfo->setResult, ctx->rsinfo->setDesc, values, nulls);
}

PG_FUNCTION_INFO_V1(pg_laplace_word_segment);

Datum
pg_laplace_word_segment(PG_FUNCTION_ARGS)
{
    text         *t;
    word_seg_ctx  ctx;
    int           rc;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    t = PG_GETARG_TEXT_PP(0);
    if (VARSIZE_ANY_EXHDR(t) == 0)
        return (Datum) 0;
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("word_segment requires the T0 perfcache")));

    ctx.rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    rc = laplace_content_word_segment((const uint8_t *) VARDATA_ANY(t),
                                      (size_t) VARSIZE_ANY_EXHDR(t),
                                      word_seg_emit, &ctx);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("word_segment: segmentation failed (rc=%d)", rc)));
    return (Datum) 0;
}










typedef struct
{
    const uint8_t *base;
    uint32_t      *off;
    uint32_t      *len;
    int            n;
    int            cap;
} phrase_ctx;

static void
phrase_collect_emit(void *ctx_, uint32_t ordinal,
                    const uint8_t *word_utf8, uint32_t word_len,
                    const hash128_t *id)
{
    phrase_ctx *ctx = (phrase_ctx *) ctx_;

    (void) ordinal;
    (void) id;
    if (ctx->n == ctx->cap)
    {
        int newcap = ctx->cap ? ctx->cap * 2 : 16;

        if (ctx->off == NULL)
        {
            ctx->off = (uint32_t *) palloc(sizeof(uint32_t) * newcap);
            ctx->len = (uint32_t *) palloc(sizeof(uint32_t) * newcap);
        }
        else
        {
            ctx->off = (uint32_t *) repalloc(ctx->off, sizeof(uint32_t) * newcap);
            ctx->len = (uint32_t *) repalloc(ctx->len, sizeof(uint32_t) * newcap);
        }
        ctx->cap = newcap;
    }
    
    ctx->off[ctx->n] = (uint32_t) (word_utf8 - ctx->base);
    ctx->len[ctx->n] = word_len;
    ctx->n++;
}

static bool
phrase_id_is_entity(hash128_t *id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1];
    int   rc;

    args[0] = hash128_to_datum(id);
    rc = SPI_execute_with_args(
        "SELECT 1 FROM laplace.entities WHERE id = $1",
        1, argtypes, args, NULL, true, 1);
    return rc == SPI_OK_SELECT && SPI_processed > 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_resolve_phrase);

Datum
pg_laplace_resolve_phrase(PG_FUNCTION_ARGS)
{
    text          *t;
    const uint8_t *base;
    phrase_ctx     ctx;
    int            rc;
    bool           spi_top = false;
    bool           found = false;
    hash128_t      found_id = { 0, 0 };

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    t = PG_GETARG_TEXT_PP(0);
    if (VARSIZE_ANY_EXHDR(t) == 0)
        PG_RETURN_NULL();
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("resolve_phrase requires the T0 perfcache")));

    base = (const uint8_t *) VARDATA_ANY(t);
    memset(&ctx, 0, sizeof(ctx));
    ctx.base = base;
    rc = laplace_content_word_segment(base, (size_t) VARSIZE_ANY_EXHDR(t),
                                      phrase_collect_emit, &ctx);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("resolve_phrase: segmentation failed (rc=%d)", rc)));
    if (ctx.n == 0)
        PG_RETURN_NULL();

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "resolve_phrase: SPI_connect failed");

    

    for (int L = ctx.n; L >= 1 && !found; L--)
    {
        for (int i = 0; i + L <= ctx.n && !found; i++)
        {
            const uint8_t *sp = base + ctx.off[i];
            size_t  splen = (size_t) ((ctx.off[i + L - 1] + ctx.len[i + L - 1])
                                      - ctx.off[i]);
            hash128_t id;
            char     *lowered;

            if (laplace_content_root_id(sp, splen, &id) == 0
                && phrase_id_is_entity(&id))
            {
                found_id = id;
                found = true;
                break;
            }
            lowered = str_tolower((const char *) sp, splen, DEFAULT_COLLATION_OID);
            if (lowered != NULL
                && laplace_content_root_id((const uint8_t *) lowered,
                                           strlen(lowered), &id) == 0
                && phrase_id_is_entity(&id))
            {
                found_id = id;
                found = true;
                break;
            }
        }
    }

    laplace_spi_finish(spi_top);
    if (!found)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&found_id));
}







static text *
realize_branch(const char *sql, Datum id, Datum lang, int nargs)
{
    Oid   argtypes[2] = { BYTEAOID, BYTEAOID };
    Datum args[2] = { id, lang };
    char  nulls[3] = "   ";
    bool  isnull;
    int   rc;
    Datum d;
    text *src;
    text *dst;
    Size  sz;

    if (nargs == 2 && lang == (Datum) 0)
        nulls[1] = 'n';
    rc = SPI_execute_with_args(sql, nargs, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return NULL;
    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        return NULL;
    src = DatumGetTextPP(d);
    if (VARSIZE_ANY_EXHDR(src) == 0)
        return NULL;                       
    sz = VARSIZE_ANY(src);
    dst = (text *) SPI_palloc(sz);         
    memcpy(dst, src, sz);
    return dst;
}

PG_FUNCTION_INFO_V1(pg_laplace_realize);

Datum
pg_laplace_realize(PG_FUNCTION_ARGS)
{
    Datum id, lang;
    bool  spi_top = false;
    text *out;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    lang = PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "realize: SPI_connect failed");

    /*
     * A synset's own render_text is its bare WordNet offset (e.g. "i46360") — that IS its content,
     * so a render_text-first chain short-circuits and hides the real lemma. Resolve synset ->
     * representative lemma FIRST (synset -IS_SENSE_OF- sense -HAS_SENSE- word, language-preferred).
     * This branch returns NULL for non-synsets (a word is never the object of IS_SENSE_OF), so words
     * still fall through to render_text below and render directly.
     */
    out = realize_branch(
            "SELECT q.s FROM ("
            "  SELECT laplace.render_text(hs.subject_id) AS s, "
            "         (lang.object_id IS NOT NULL) AS lp, "
            "         laplace.eff_mu(hs.rating, hs.rd) AS mu "
            "  FROM laplace.consensus io "
            "  JOIN laplace.consensus hs ON hs.object_id = io.subject_id "
            "    AND hs.type_id = laplace.relation_type_id('HAS_SENSE') "
            "  LEFT JOIN laplace.consensus lang ON lang.subject_id = hs.subject_id "
            "    AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE') "
            "    AND lang.object_id = $2 "
            "  WHERE io.object_id = $1 "
            "    AND io.type_id = laplace.relation_type_id('IS_SENSE_OF') "
            "    AND NOT laplace.refuted(io.rating, io.rd)"
            ") q WHERE q.s IS NOT NULL AND q.s <> '' "
            "ORDER BY q.lp DESC, q.mu DESC LIMIT 1", id, lang, 2);
    if (out == NULL)
        out = realize_branch("SELECT NULLIF(laplace.render_text($1), '')", id, lang, 1);
    if (out == NULL)
        out = realize_branch(
            
            "SELECT q.s FROM ("
            "  SELECT laplace.render_text(m.object_id) AS s, "
            "         (lang.object_id IS NOT NULL) AS lp, "
            "         laplace.eff_mu(m.rating, m.rd) AS mu "
            "  FROM laplace.consensus m "
            "  LEFT JOIN laplace.consensus lang ON lang.subject_id = m.object_id "
            "    AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE') "
            "    AND lang.object_id = $2 "
            "  WHERE m.subject_id = $1 "
            "    AND m.type_id = laplace.relation_type_id('IS_TRANSLATION_OF') "
            "    AND NOT laplace.refuted(m.rating, m.rd)"
            ") q WHERE q.s IS NOT NULL AND q.s <> '' "
            "ORDER BY q.lp DESC, q.mu DESC LIMIT 1", id, lang, 2);
    if (out == NULL)
        out = realize_branch(
            "SELECT regexp_replace(n.name, '^substrate/[a-z_]+/(.+)/v1$', '\\1') "
            "FROM laplace.canonical_names n "
            "WHERE n.id = $1 AND n.name LIKE 'substrate/%'", id, lang, 1);
    if (out == NULL)
        out = realize_branch(
            "SELECT laplace.render_text(g.object_id) "
            "FROM laplace.consensus g "
            "WHERE g.subject_id = $1 AND g.type_id = laplace.relation_type_id('DEFINES') "
            "  AND NOT laplace.refuted(g.rating, g.rd) "
            "ORDER BY laplace.eff_mu(g.rating, g.rd) DESC "
            "LIMIT 1", id, lang, 1);
    if (out == NULL)
        out = realize_branch("SELECT laplace.label($1)", id, lang, 1);

    laplace_spi_finish(spi_top);

    if (out == NULL)
        PG_RETURN_NULL();
    PG_RETURN_TEXT_P(out);
}
