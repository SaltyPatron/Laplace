






#include "postgres.h"

#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

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
    uint32_t *off;   /* tree-text (post-NFC) offsets — see #1039 note below */
    uint32_t *len;
    int       n;
    int       cap;
} phrase_ctx;

/* Word spans collected from the TREE, in the tree's own post-NFC offset
 * space (#1039). The previous collector derived offsets by pointer
 * arithmetic against the CALLER's buffer (word_utf8 - base) — valid only
 * while segmentation spans aliased the input, which stopped being true when
 * the tier tree took ownership of its normalized text; on any NFC-changed
 * input the offsets were garbage even before that. Same word set as
 * laplace_content_word_segment: tier-2 nodes, non-empty, not all-whitespace,
 * ascending by offset. */
static int
phrase_collect_from_tree(const tier_tree_t *tree,
                         const uint8_t *norm, size_t norm_len,
                         phrase_ctx *ctx)
{
    size_t nc = tier_tree_node_count(tree);

    for (uint32_t idx = 0; idx < (uint32_t) nc; ++idx)
    {
        tier_node_view_t node;

        if (tier_tree_get_node(tree, idx, &node) != 0)
            continue;
        if (node.tier != 2 || node.text_range_len == 0)
            continue;
        if ((size_t) node.text_range_off + node.text_range_len > norm_len)
            continue;
        if (laplace_text_is_all_whitespace(norm + node.text_range_off,
                                           node.text_range_len))
            continue;
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
        ctx->off[ctx->n] = node.text_range_off;
        ctx->len[ctx->n] = node.text_range_len;
        ctx->n++;
    }

    /* The decomposer appends words in ascending offset order; keep the
     * contract explicit with an insertion check rather than assuming it. */
    for (int i = 1; i < ctx->n; i++)
        if (ctx->off[i] < ctx->off[i - 1])
            return -1;
    return 0;
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

    /* Build the tier tree ONCE and collect spans in ITS offset space; the
     * tree's text is the post-NFC bytes those offsets index (#1039). The
     * tree is freed as soon as pass 1 has computed the sub-span root ids —
     * before SPI connects — so no native allocation crosses an elog. */
    tier_tree_t   *tree = NULL;
    const uint8_t *norm = NULL;
    size_t         norm_len = 0;

    rc = laplace_content_tree_build_public(base, (size_t) VARSIZE_ANY_EXHDR(t), &tree);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("resolve_phrase: segmentation failed (rc=%d)", rc)));
    norm = tier_tree_text(tree, &norm_len);
    if (norm == NULL || phrase_collect_from_tree(tree, norm, norm_len, &ctx) != 0)
    {
        tier_tree_free(tree);
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("resolve_phrase: span collection failed")));
    }
    if (ctx.n == 0)
    {
        tier_tree_free(tree);
        PG_RETURN_NULL();
    }

    /*
     * The candidate span set is fixed and content_root_id is native (no SPI),
     * so compute every span's root id in C up front, then answer "which of
     * these ids are stored entities" in ONE batch query instead of an O(n^2)
     * storm of single-row EXISTS round-trips. The winning span is then chosen
     * in C using the IDENTICAL nested-loop order (L = n..1 outer, i ascending
     * inner, first match wins), so the selected id is bit-identical to the old
     * per-span probe.
     *
     * The membership query targets the laplace.entities table directly and is
     * deliberately NOT entity_exists(): that helper also answers true for any
     * valid codepoint via the perfcache axiom, and under the tier-blind
     * content law (same content = same hash; tier is a floor) a single-letter
     * word IS its codepoint — the axiom would let stopwords like 'a' hijack
     * phrase resolution ahead of real lexical matches. "Is this segment known
     * content" is the stored-row question.
     */
    {
        int         n_span = ctx.n * (ctx.n + 1) / 2;
        hash128_t  *span_id = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
        bool       *span_ok = (bool *) palloc(sizeof(bool) * n_span);
        Datum      *elems = (Datum *) palloc(sizeof(Datum) * n_span);
        int         n_elems = 0;
        int         s;

        /* Pass 1: compute each span's content_root_id in canonical order.
         * Sub-span bytes slice the tree's normalized text — contiguous runs
         * across words INCLUDING the inter-word bytes, exactly as before,
         * just in the correct (post-NFC) space. */
        s = 0;
        for (int L = ctx.n; L >= 1; L--)
        {
            for (int i = 0; i + L <= ctx.n; i++, s++)
            {
                const uint8_t *sp = norm + ctx.off[i];
                size_t  splen = (size_t) ((ctx.off[i + L - 1] + ctx.len[i + L - 1])
                                          - ctx.off[i]);

                if (laplace_content_root_id(sp, splen, &span_id[s]) == 0)
                {
                    span_ok[s] = true;
                    elems[n_elems++] = hash128_to_datum(&span_id[s]);
                }
                else
                {
                    span_ok[s] = false;
                }
            }
        }

        /* Root ids are computed; nothing below reads the tree's text. */
        tier_tree_free(tree);
        tree = NULL;

        if (n_elems > 0)
        {
            HTAB      *present;
            HASHCTL    hctl;
            ArrayType *arr;
            Oid        argtypes[1] = { BYTEAARRAYOID };
            Datum      args[1];
            int        qrc;

            if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
                elog(ERROR, "resolve_phrase: SPI_connect failed");

            arr = construct_array(elems, n_elems, BYTEAOID, -1, false, TYPALIGN_INT);
            args[0] = PointerGetDatum(arr);
            qrc = SPI_execute_with_args(
                "SELECT e.id FROM laplace.entities e WHERE e.id = ANY($1::bytea[])",
                1, argtypes, args, NULL, true, 0);
            if (qrc != SPI_OK_SELECT)
                elog(ERROR, "resolve_phrase: entity membership query failed: %s",
                     SPI_result_code_string(qrc));

            memset(&hctl, 0, sizeof(hctl));
            hctl.keysize = sizeof(hash128_t);
            hctl.entrysize = sizeof(hash128_t);
            present = hash_create("resolve_phrase present",
                                  (SPI_processed > 0 ? (long) SPI_processed : 16),
                                  &hctl, HASH_ELEM | HASH_BLOBS);
            for (uint64 r = 0; r < SPI_processed; r++)
            {
                bool      isnull;
                hash128_t h = datum_to_hash128(
                    SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc,
                                  1, &isnull));
                bool      pfound;

                if (!isnull)
                    hash_search(present, &h, HASH_ENTER, &pfound);
            }

            /*
             * Pass 2 runs while SPI is still connected: `present` lives in the
             * SPI procedure memory context and would be freed by an early
             * finish. It performs no SPI itself — just the same nested-loop
             * order (first present span wins).
             */
            s = 0;
            for (int L = ctx.n; L >= 1 && !found; L--)
            {
                for (int i = 0; i + L <= ctx.n && !found; i++, s++)
                {
                    bool pfound;

                    if (!span_ok[s])
                        continue;
                    hash_search(present, &span_id[s], HASH_FIND, &pfound);
                    if (pfound)
                    {
                        found_id = span_id[s];
                        found = true;
                    }
                }
            }

            laplace_spi_finish(spi_top);
        }
    }

    if (!found)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&found_id));
}
