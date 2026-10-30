






#include "postgres.h"
#include "common/int.h"

#include "fmgr.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

#include "perfcache_native.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "prompt_input.h"
#include "consensus_scan.h"
#include "laplace/core/sql_catalog.h"

typedef struct
{
    ReturnSetInfo *rsinfo;
} word_seg_ctx;

#include "utils/array.h"

/* Joint evidence for one candidate span: witness count and rating summed, and
 * minimum rd, over consensus cells whose both endpoints are candidates of the
 * same observation. `degree` counts shared trajectory containers and is filled
 * only when no such cell exists. Key first for HASH_BLOBS. */
typedef struct
{
    hash128_t key;
    int64     witnesses;
    int64     rating;
    int64     rd;
    int64     degree;
} joint_degree_entry;

/* Container -> how many distinct candidates its trajectory contains.
 * first_candidate is the last candidate counted, so one candidate's repeated
 * rows count once; n_candidates > 1 makes the container joint evidence. */
typedef struct
{
    hash128_t key;
    int       first_candidate;
    int       n_candidates;
} container_owner_entry;

/* Containers read per candidate. */
#define RESOLVE_CONTAINER_PROBE_LIMIT 64

/* One coarse holder read for the complete candidate set. Native owns GIN,
 * exact membership and hub classification; no per-candidate SQL executor. */
static SPIPlanPtr resolve_container_plan;
static void ensure_resolve_container_plan(void)
{
    Oid types[]={BYTEAARRAYOID,INT4OID};
    spi_common_ensure_plan(&resolve_container_plan,laplace_sql_query_text("native.resolve_containers"),2,types);
}
typedef struct { HTAB *degree;bool any; } JointRead;
static void resolve_joint_cell(const LaplaceConsensusRow *row,void *context)
{
    JointRead *read=context;if(row->object_is_null)return;
    const hash128_t *ends[]={&row->subject,&row->object};
    for(int i=0;i<2;i++){
        bool found;joint_degree_entry *entry=hash_search(read->degree,ends[i],HASH_ENTER,&found);
        if(!found)*entry=(joint_degree_entry){.key=*ends[i],.rd=PG_INT64_MAX};
        if(pg_add_s64_overflow(entry->witnesses,row->witnesses,&entry->witnesses)
           || pg_add_s64_overflow(entry->rating,row->rating,&entry->rating))
            ereport(ERROR,(errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),errmsg("Joint evidence exceeds integer capacity")));
        entry->rd=Min(entry->rd,row->rd);
    }
    read->any=true;
}

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

/* RESOLVE: the canonical composition tree of one observation, every node
 * (whitespace, punctuation and unknown content included) with its parent, tier,
 * id and span. Offsets address the tree's normalized text. */
PG_FUNCTION_INFO_V1(pg_laplace_prompt_tree);

typedef struct PromptOperand
{
    hash128_t id;
    uint32_t node;
    uint32_t offset;
} PromptOperand;

static int
prompt_operand_order(const void *a, const void *b)
{
    const PromptOperand *left = a, *right = b;
    if (left->offset != right->offset)
        return left->offset < right->offset ? -1 : 1;
    return left->node < right->node ? -1 : left->node > right->node;
}

static int
prompt_seed_order(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

/* Operands of the observation are read straight from the native tree arrays.
 * The ordered occurrence cut keeps repeated occurrences as repeated operands;
 * only the probe seed set (root plus every node at or above the cut) is
 * deduplicated. */

static void
prompt_input_release(void *argument)
{
    LaplacePromptInput *input = argument;
    tier_tree_free(input->tree);
    input->tree = NULL;
}

ArrayType *
laplace_prompt_input_cut(const LaplacePromptInput *input, uint8 tier)
{
    size_t count = tier_tree_node_count(input->tree);
    const uint8_t *tiers = tier_tree_tier_array(input->tree);
    const uint32_t *parents = tier_tree_parent_idx_array(input->tree);
    const uint32_t *offsets = tier_tree_text_off_array(input->tree);
    const hash128_t *ids = tier_tree_id_array(input->tree);
    PromptOperand *cut = palloc(sizeof(*cut) * Max(count, 1));
    hash128_t *ordered = palloc(sizeof(*ordered) * Max(count, 1));
    int length = 0;
    for (uint32_t i = 0; i < count; ++i)
    {
        if (tiers[i] <= tier &&
            (parents[i] == TIER_TREE_INVALID || tiers[parents[i]] > tier))
            cut[length++] = (PromptOperand){ids[i], i, offsets[i]};
        if ((i & 4095) == 0) CHECK_FOR_INTERRUPTS();
    }
    qsort(cut, length, sizeof(*cut), prompt_operand_order);
    for (int i = 0; i < length; ++i) ordered[i] = cut[i].id;
    ArrayType *result = hash128_array_from_ids(ordered, length);
    pfree(ordered);
    pfree(cut);
    return result;
}

LaplacePromptInput *
laplace_prompt_input(text *input)
{
    LaplacePromptInput *prepared;
    tier_tree_t *tree;
    int rc;
    if (VARSIZE_ANY_EXHDR(input) == 0) return NULL;
    /* Composition needs the Tier-0 perfcache mapped in this backend. */
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errmsg("prompt composition requires the T0 perfcache"),
                 errhint("Configure laplace_substrate.perfcache_path with the installed Unicode cache.")));
    prepared = palloc0(sizeof(*prepared));
    prepared->cleanup.func = prompt_input_release;
    prepared->cleanup.arg = prepared;
    MemoryContextRegisterResetCallback(CurrentMemoryContext, &prepared->cleanup);
    rc = laplace_content_tree_build_public(
        (const uint8_t *) VARDATA_ANY(input), VARSIZE_ANY_EXHDR(input), &prepared->tree);
    if (rc != 0)
        ereport(ERROR, (errmsg("prompt_operands: canonical composition failed (%d)", rc)));
    tree = prepared->tree;
    {
        size_t count = tier_tree_node_count(tree);
        const uint32_t *parents = tier_tree_parent_idx_array(tree);
        const uint32_t *offsets = tier_tree_text_off_array(tree);
        const hash128_t *ids = tier_tree_id_array(tree);
        hash128_t root;
        PromptOperand *operands;
        hash128_t *context_ids, *seed_ids;
        int n_operands = 0, n_seeds = 1;
        ArrayBuildState *nodes = NULL;
        HASHCTL ctl = {0};
        HTAB *seen;
        size_t segment_count;
        const uint32_t *segments = tier_tree_word_segments(tree, &segment_count);
        if (count > INT_MAX || count > MaxAllocSize / sizeof(PromptOperand) ||
            content_witness_tree_root_id(tree, &root) != 0)
            ereport(ERROR, (errmsg("prompt_operands: invalid canonical composition")));
        operands = palloc(sizeof(PromptOperand) * Max(count, 1));
        context_ids = palloc(sizeof(hash128_t) * Max(count, 1));
        seed_ids = palloc(sizeof(hash128_t) * (count + 1));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(hash128_t);
        seen = hash_create("prompt probe identities", 64, &ctl, HASH_ELEM | HASH_BLOBS);
        hash_search(seen, &root, HASH_ENTER, NULL);
        seed_ids[0] = root;
        bool *ancestors = palloc0(sizeof(bool) * Max(count, 1));
        for (size_t word = 0; word < segment_count; ++word)
        {
            uint32_t i = segments[word];
            if (i >= count) ereport(ERROR, (errmsg("prompt_operands: invalid segment index")));
            operands[n_operands++] = (PromptOperand) {ids[i], i, offsets[i]};
            for (uint32_t node = i; node != TIER_TREE_INVALID; node = parents[node])
            {
                if (node >= count) ereport(ERROR, (errmsg("prompt_operands: invalid parent index")));
                if (ancestors[node]) break;
                ancestors[node] = true;
            }
            if ((word & 4095) == 0) CHECK_FOR_INTERRUPTS();
        }
        for (uint32_t i = 0; i < count; ++i) if (ancestors[i]) {
            bool found;
            hash_search(seen, &ids[i], HASH_ENTER, &found);
            if (!found) seed_ids[n_seeds++] = ids[i];
        }
        qsort(operands, n_operands, sizeof(PromptOperand), prompt_operand_order);
        for (int i = 0; i < n_operands; ++i)
        {
            context_ids[i] = operands[i].id;
            nodes = accumArrayResult(nodes, Int32GetDatum(operands[i].node),
                                     false, INT4OID, CurrentMemoryContext);
        }
        prepared->root = root;
        prepared->context = hash128_array_from_ids(context_ids, n_operands);
        prepared->nodes = nodes ? DatumGetArrayTypeP(makeArrayResult(nodes, CurrentMemoryContext)) :
                                 construct_empty_array(INT4OID);
        qsort(seed_ids, n_seeds, sizeof(hash128_t), prompt_seed_order);
        prepared->seeds = hash128_array_from_ids(seed_ids, n_seeds);
        hash_destroy(seen);
        pfree(operands);
        pfree(context_ids);
        pfree(seed_ids);
        pfree(ancestors);
    }
    return prepared;
}

Datum
pg_laplace_prompt_tree(PG_FUNCTION_ARGS)
{
    tier_tree_t *tree = NULL;
    hash128_t root;
    text *input;
    int rc;
    bool include_surfaces = PG_NARGS() < 2 ||
        (!PG_ARGISNULL(1) && PG_GETARG_BOOL(1));
    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0)) return (Datum) 0;
    input = PG_GETARG_TEXT_PP(0);
    if (VARSIZE_ANY_EXHDR(input) == 0) return (Datum) 0;
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("prompt_tree requires the T0 perfcache"),
                 errhint("Configure laplace_substrate.perfcache_path with the installed Unicode cache.")));
    rc = laplace_content_tree_build_public(
        (const uint8_t *) VARDATA_ANY(input), VARSIZE_ANY_EXHDR(input), &tree);
    if (rc != 0)
        ereport(ERROR, (errmsg("prompt_tree: canonical composition failed (%d)", rc)));
    PG_TRY();
    {
        size_t length;
        const uint8_t *bytes = tier_tree_text(tree, &length);
        size_t count = tier_tree_node_count(tree);
        ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
        if (bytes == NULL || count > INT_MAX ||
            content_witness_tree_root_id(tree, &root) != 0)
            ereport(ERROR, (errmsg("prompt_tree: invalid canonical composition")));
        for (uint32_t i = 0; i < count; ++i)
        {
            tier_node_view_t node;
            Datum values[8];
            bool nulls[8] = {false};
            if (tier_tree_get_node(tree, i, &node) != 0 ||
                (uint64) node.text_range_off + node.text_range_len > length)
                ereport(ERROR, (errmsg("prompt_tree: invalid constituent span")));
            values[0] = hash128_to_datum(&root);
            values[1] = Int32GetDatum(i);
            values[2] = Int32GetDatum(node.parent_idx);
            nulls[2] = node.parent_idx == TIER_TREE_INVALID;
            values[3] = Int16GetDatum(node.tier);
            values[4] = Int32GetDatum(node.text_range_off);
            values[5] = Int32GetDatum(node.text_range_len);
            values[6] = hash128_to_datum(&node.id);
            nulls[7] = !include_surfaces;
            values[7] = include_surfaces
                ? PointerGetDatum(cstring_to_text_with_len(
                    (const char *) bytes + node.text_range_off, node.text_range_len))
                : (Datum) 0;
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            pfree(DatumGetPointer(values[0]));
            pfree(DatumGetPointer(values[6]));
            if (include_surfaces) pfree(DatumGetPointer(values[7]));
            if ((i & 4095) == 0) CHECK_FOR_INTERRUPTS();
        }
    }
    PG_CATCH();
    {
        tier_tree_free(tree);
        PG_RE_THROW();
    }
    PG_END_TRY();
    tier_tree_free(tree);
    return (Datum) 0;
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
    uint32_t *off;   /* offsets into the tree's normalized text */
    uint32_t *len;
    int       n;
    int       cap;
} phrase_ctx;

/* Word occurrences come from Native's UAX segment cut, independently of DAG
 * altitude. A word can be a scalar, a one-level composition or a deeper tree;
 * a sentence can itself be tier 2. Keep source order and repeated occurrences. */
static int
phrase_collect_from_tree(const tier_tree_t *tree,
                         const uint8_t *norm, size_t norm_len,
                         phrase_ctx *ctx)
{
    size_t nc = tier_tree_node_count(tree);
    size_t segment_count = 0;
    const uint32_t *segments = tier_tree_word_segments(tree, &segment_count);

    for (size_t occurrence = 0; occurrence < segment_count; ++occurrence)
    {
        tier_node_view_t node;
        uint32_t idx = segments[occurrence];

        if (idx >= nc || tier_tree_get_node(tree, idx, &node) != 0)
            return -1;
        if (node.text_range_len == 0)
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

    /* Callers slice contiguous runs by offset; reject an unordered tree. */
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

    /* The tree is freed once every sub-span root id is computed, before SPI
     * connects, so no native allocation crosses an elog. */
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
     * Every contiguous span of words is a candidate; root ids are computed
     * natively and presence is one set read through the canonical Native identity reader.
     * Candidates enumerate by length descending, then position ascending.
     *
     * Presence reads stored entities, not entity_exists(): that helper is true
     * for every Tier-0 codepoint, so a one-codepoint word would always count
     * as known content.
     */
    {
        int         n_span = ctx.n * (ctx.n + 1) / 2;
        hash128_t  *span_id = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
        bool       *span_ok = (bool *) palloc(sizeof(bool) * n_span);
        Datum      *elems = (Datum *) palloc(sizeof(Datum) * n_span);
        int         n_elems = 0;
        int         s;

        /* A span's bytes are the normalized text from its first word's start
         * to its last word's end, inter-word bytes included. */
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
                laplace_sql_query_text("native.stored_ids"),
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
             * SELECT among present spans by joint evidence within this
             * observation: consensus shared with the other candidates decides,
             * not any span's standing alone. Length and position only break
             * ties.
             */
            {
                Datum     *pres_elems = (Datum *) palloc(sizeof(Datum) * n_span);
                hash128_t *pres_ids = (hash128_t *) palloc(sizeof(hash128_t) * n_span);
                int        n_pres = 0;
                HTAB      *degree = NULL;
                HASHCTL    dctl;

                s = 0;
                for (int L = ctx.n; L >= 1; L--)
                    for (int i = 0; i + L <= ctx.n; i++, s++)
                    {
                        bool pfound;
                        if (!span_ok[s])
                            continue;
                        hash_search(present, &span_id[s], HASH_FIND, &pfound);
                        if (pfound)
                        {
                            pres_ids[n_pres]     = span_id[s];
                            pres_elems[n_pres++] = hash128_to_datum(&span_id[s]);
                        }
                    }

                memset(&dctl, 0, sizeof(dctl));
                dctl.keysize   = sizeof(hash128_t);
                dctl.entrysize = sizeof(joint_degree_entry);
                degree = hash_create("resolve_phrase joint degree",
                                     (n_pres > 0 ? (long) n_pres : 16),
                                     &dctl, HASH_ELEM | HASH_BLOBS);

                if (n_pres > 1)
                {
                    ArrayType *cand_arr = construct_array(
                        pres_elems, n_pres, BYTEAOID, -1, false, TYPALIGN_INT);
                    JointRead joint={degree,false};
                    laplace_consensus_scan(cand_arr,cand_arr,NULL,resolve_joint_cell,&joint,NULL);
                    bool joint_from_consensus=joint.any;

                    /* With no consensus among the candidates, joint evidence
                     * is trajectory co-occurrence. */
                    if (!joint_from_consensus)
                    {
                    /*
                     * A candidate's degree is how many of its containers also
                     * contain another candidate: containment and co-occurrence
                     * are read from trajectories, not attestations. One kept
                     * single-key GIN probe per candidate; a multi-key `&&` probe
                     * makes the planner abandon the index.
                     */
                    HTAB      *owners;
                    HASHCTL    octl;
                    Datum      cargs[2];
                    hash128_t *cont = (hash128_t *) palloc(
                        sizeof(hash128_t) * n_pres * RESOLVE_CONTAINER_PROBE_LIMIT);
                    int       *cont_n = (int *) palloc0(sizeof(int) * n_pres);

                    memset(&octl, 0, sizeof(octl));
                    octl.keysize   = sizeof(hash128_t);
                    octl.entrysize = sizeof(container_owner_entry);
                    owners = hash_create("resolve_phrase containers",
                                         (long) n_pres * RESOLVE_CONTAINER_PROBE_LIMIT,
                                         &octl, HASH_ELEM | HASH_BLOBS);

                    ensure_resolve_container_plan();

                    /* Complete non-hub holders for every candidate, in one
                     * read. A hub contributes no arbitrary heap-order subset. */
                    cargs[0]=PointerGetDatum(cand_arr);
                    cargs[1]=Int32GetDatum(RESOLVE_CONTAINER_PROBE_LIMIT);
                    int qrc2=SPI_execute_plan(resolve_container_plan,cargs,NULL,true,0);
                    if(qrc2!=SPI_OK_SELECT)elog(ERROR,"resolve_phrase: container set failed: %s",SPI_result_code_string(qrc2));
                    for(uint64 r=0;r<SPI_processed;r++){
                        bool missing,found;
                        int64 ordinal=DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[r],SPI_tuptable->tupdesc,1,&missing));
                        if(missing || ordinal<1 || ordinal>n_pres)elog(ERROR,"resolve_phrase: invalid holder ordinal");
                        int c=(int)ordinal-1;
                        Datum value=SPI_getbinval(SPI_tuptable->vals[r],SPI_tuptable->tupdesc,2,&missing);
                        if(missing || cont_n[c]>=RESOLVE_CONTAINER_PROBE_LIMIT)elog(ERROR,"resolve_phrase: invalid holder set");
                        hash128_t cid=datum_to_hash128(value);
                        cont[c*RESOLVE_CONTAINER_PROBE_LIMIT+cont_n[c]++]=cid;
                        container_owner_entry *owner=hash_search(owners,&cid,HASH_ENTER,&found);
                        if(!found)*owner=(container_owner_entry){cid,c,1};
                        else if(owner->first_candidate!=c){owner->n_candidates++;owner->first_candidate=c;}
                    }

                    /* A container holding two or more candidates is joint
                     * evidence; credit every candidate inside it. */
                    for (int c = 0; c < n_pres; c++)
                    {
                        int64 shared = 0;
                        bool  dfound;
                        joint_degree_entry *ent;

                        for (int k = 0; k < cont_n[c]; k++)
                        {
                            bool ofound;
                            container_owner_entry *oe = (container_owner_entry *)
                                hash_search(owners,
                                            &cont[c * RESOLVE_CONTAINER_PROBE_LIMIT + k],
                                            HASH_FIND, &ofound);
                            if (ofound && oe->n_candidates > 1)
                                shared++;
                        }
                        if (shared == 0)
                            continue;
                        ent = (joint_degree_entry *)
                            hash_search(degree, &pres_ids[c], HASH_ENTER, &dfound);
                        if (!dfound)
                        {
                            ent->witnesses = 0;
                            ent->rating    = 0;
                            ent->rd        = PG_INT64_MAX;
                            ent->degree    = 0;
                        }
                        ent->degree += shared;
                    }
                    }
                }

                /*
                 * Order: summed witnesses, summed rating, lower minimum rd,
                 * co-occurrence degree; the first span in length-descending,
                 * position-ascending order wins ties.
                 */
                {
                    int64 best_w = 0, best_r = 0, best_rd = 0, best_d = 0;
                    bool  have_best = false;

                    s = 0;
                    for (int L = ctx.n; L >= 1; L--)
                        for (int i = 0; i + L <= ctx.n; i++, s++)
                        {
                            bool  pfound, dfound, better;
                            int64 w = 0, rt = 0, rd = PG_INT64_MAX, d = 0;
                            joint_degree_entry *ent;

                            if (!span_ok[s])
                                continue;
                            hash_search(present, &span_id[s], HASH_FIND, &pfound);
                            if (!pfound)
                                continue;
                            ent = (joint_degree_entry *)
                                hash_search(degree, &span_id[s], HASH_FIND, &dfound);
                            if (dfound)
                            {
                                w  = ent->witnesses;
                                rt = ent->rating;
                                rd = ent->rd;
                                d  = ent->degree;
                            }

                            if (!have_best)          better = true;
                            else if (w  != best_w)   better = w  > best_w;
                            else if (rt != best_r)   better = rt > best_r;
                            else if (rd != best_rd)  better = rd < best_rd;
                            else                     better = d  > best_d;

                            if (better)
                            {
                                best_w = w; best_r = rt; best_rd = rd; best_d = d;
                                have_best = true;
                                found_id  = span_id[s];
                                found     = true;
                            }
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

/*
 * pg_laplace_word_segment_resolved: word breaks, then stored entities decide
 * where words end inside a run the text gave no boundary for.
 *
 * UAX #29 leaves scripts without spaces (and without dictionary segmentation)
 * as one word segment per character. A maximal byte-contiguous run of segments
 * nodes is covered by the longest, then leftmost, sub-spans that are stored
 * entities; uncovered nodes are emitted as themselves. Whitespace is excluded
 * from the spans, so it always separates runs. The rule names no script.
 *
 * Stores nothing. Presence reads stored entities, not entity_exists(), which
 * is true for every Tier-0 codepoint.
 */
typedef struct
{
    int       first;   /* index into phrase_ctx of the run's first node */
    int       count;   /* nodes in the run                             */
} run_t;

PG_FUNCTION_INFO_V1(pg_laplace_word_segment_resolved);

Datum
pg_laplace_word_segment_resolved(PG_FUNCTION_ARGS)
{
    text          *t;
    const uint8_t *base;
    phrase_ctx     ctx;
    tier_tree_t   *tree = NULL;
    const uint8_t *tree_text = NULL;
    uint8_t       *norm = NULL;
    size_t         norm_len = 0;
    run_t         *runs = NULL;
    int            n_runs = 0;
    hash128_t     *cand_id = NULL;
    int           *cand_run = NULL;
    int           *cand_i = NULL;
    int           *cand_len = NULL;
    bool          *cand_ok = NULL;
    int            n_cand = 0;
    int            n_cand_max;
    bool          *chosen = NULL;   /* per candidate: part of the covering */
    bool           spi_top = false;
    int            rc;
    int            r;
    int            s;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;
    t = PG_GETARG_TEXT_PP(0);
    if (VARSIZE_ANY_EXHDR(t) == 0)
        return (Datum) 0;
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("word_segment_resolved requires the T0 perfcache")));

    base = (const uint8_t *) VARDATA_ANY(t);
    memset(&ctx, 0, sizeof(ctx));

    rc = laplace_content_tree_build_public(base, (size_t) VARSIZE_ANY_EXHDR(t), &tree);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("word_segment_resolved: segmentation failed (rc=%d)", rc)));

    tree_text = tier_tree_text(tree, &norm_len);
    if (tree_text == NULL || phrase_collect_from_tree(tree, tree_text, norm_len, &ctx) != 0)
    {
        tier_tree_free(tree);
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("word_segment_resolved: span collection failed")));
    }
    if (ctx.n == 0)
    {
        tier_tree_free(tree);
        return (Datum) 0;
    }

    /* Emitted surfaces slice the normalized text; copy it so the tree can be
     * freed before SPI and no native allocation crosses an elog. */
    norm = (uint8_t *) palloc(norm_len > 0 ? norm_len : 1);
    memcpy(norm, tree_text, norm_len);

    /* Maximal runs of byte-contiguous word segments; a byte gap is a boundary
     * the text supplied. */
    runs = (run_t *) palloc(sizeof(run_t) * ctx.n);
    {
        int i = 0;

        while (i < ctx.n)
        {
            int j = i;

            while (j + 1 < ctx.n &&
                   ctx.off[j] + ctx.len[j] == ctx.off[j + 1])
                j++;
            runs[n_runs].first = i;
            runs[n_runs].count = j - i + 1;
            n_runs++;
            i = j + 1;
        }
    }

    /* Candidates: every sub-span of length >= 2 of every run. A node no chosen
     * span covers is emitted alone without a probe. */
    n_cand_max = 0;
    for (r = 0; r < n_runs; r++)
    {
        int m = runs[r].count;

        if (m >= 2)
            n_cand_max += m * (m + 1) / 2;
    }

    if (n_cand_max == 0)
    {
        /* No run to resolve: output equals converse.word_segment. */
        tier_tree_free(tree);
        goto emit;
    }

    cand_id = (hash128_t *) palloc(sizeof(hash128_t) * n_cand_max);
    cand_run = (int *) palloc(sizeof(int) * n_cand_max);
    cand_i = (int *) palloc(sizeof(int) * n_cand_max);
    cand_len = (int *) palloc(sizeof(int) * n_cand_max);
    cand_ok = (bool *) palloc(sizeof(bool) * n_cand_max);
    chosen = (bool *) palloc0(sizeof(bool) * n_cand_max);

    for (r = 0; r < n_runs; r++)
    {
        int m = runs[r].count;
        int f = runs[r].first;
        int L;

        if (m < 2)
            continue;
        for (L = m; L >= 2; L--)
        {
            int i;

            for (i = 0; i + L <= m; i++)
            {
                const uint8_t *sp = norm + ctx.off[f + i];
                size_t         splen = (size_t) ((ctx.off[f + i + L - 1] +
                                                  ctx.len[f + i + L - 1]) -
                                                 ctx.off[f + i]);

                cand_run[n_cand] = r;
                cand_i[n_cand] = i;
                cand_len[n_cand] = L;
                cand_ok[n_cand] =
                    (laplace_content_root_id(sp, splen, &cand_id[n_cand]) == 0);
                n_cand++;
            }
        }
    }

    tier_tree_free(tree);
    tree = NULL;

    /* One set read for the presence of every candidate. */
    {
        HTAB      *present;
        HASHCTL    hctl;
        ArrayType *arr;
        Datum     *elems;
        int        n_elems = 0;
        Oid        argtypes[1] = { BYTEAARRAYOID };
        Datum      args[1];
        int        qrc;

        elems = (Datum *) palloc(sizeof(Datum) * n_cand);
        for (s = 0; s < n_cand; s++)
            if (cand_ok[s])
                elems[n_elems++] = hash128_to_datum(&cand_id[s]);

        if (n_elems == 0)
            goto emit;

        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "word_segment_resolved: SPI_connect failed");

        arr = construct_array(elems, n_elems, BYTEAOID, -1, false, TYPALIGN_INT);
        args[0] = PointerGetDatum(arr);
        qrc = SPI_execute_with_args(
            laplace_sql_query_text("native.stored_ids"),
            1, argtypes, args, NULL, true, 0);
        if (qrc != SPI_OK_SELECT)
            elog(ERROR, "word_segment_resolved: entity membership query failed: %s",
                 SPI_result_code_string(qrc));

        memset(&hctl, 0, sizeof(hctl));
        hctl.keysize = sizeof(hash128_t);
        hctl.entrysize = sizeof(hash128_t);
        present = hash_create("word_segment_resolved present",
                              (SPI_processed > 0 ? (long) SPI_processed : 16),
                              &hctl, HASH_ELEM | HASH_BLOBS);
        for (uint64 row = 0; row < SPI_processed; row++)
        {
            bool      isnull;
            hash128_t h = datum_to_hash128(
                SPI_getbinval(SPI_tuptable->vals[row], SPI_tuptable->tupdesc,
                              1, &isnull));
            bool      pfound;

            if (!isnull)
                hash_search(present, &h, HASH_ENTER, &pfound);
        }

        /*
         * Covering per run: longest span first, leftmost wins, repeated on the
         * uncovered remainder. Candidates are enumerated in that order, so one
         * forward pass skipping overlaps computes it. Fewest constituents means
         * the highest stored composition.
         */
        for (s = 0; s < n_cand; s++)
        {
            bool pfound;
            int  k;
            bool clash = false;

            if (!cand_ok[s])
                continue;
            hash_search(present, &cand_id[s], HASH_FIND, &pfound);
            if (!pfound)
                continue;

            for (k = 0; k < s; k++)
            {
                if (!chosen[k] || cand_run[k] != cand_run[s])
                    continue;
                if (cand_i[k] < cand_i[s] + cand_len[s] &&
                    cand_i[s] < cand_i[k] + cand_len[k])
                {
                    clash = true;
                    break;
                }
            }
            if (!clash)
                chosen[s] = true;
        }

        laplace_spi_finish(spi_top);
    }

emit:
    {
        word_seg_ctx  out;
        uint32_t      ordinal = 0;
        int           i;

        out.rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

        for (r = 0; r < n_runs; r++)
        {
            int m = runs[r].count;
            int f = runs[r].first;

            i = 0;
            while (i < m)
            {
                int span = -1;

                for (s = 0; s < n_cand; s++)
                {
                    if (chosen != NULL && chosen[s] &&
                        cand_run[s] == r && cand_i[s] == i)
                    {
                        span = s;
                        break;
                    }
                }

                if (span >= 0)
                {
                    const uint8_t *sp = norm + ctx.off[f + i];
                    size_t         splen =
                        (size_t) ((ctx.off[f + i + cand_len[span] - 1] +
                                   ctx.len[f + i + cand_len[span] - 1]) -
                                  ctx.off[f + i]);

                    word_seg_emit(&out, ordinal++, sp, (uint32_t) splen,
                                  &cand_id[span]);
                    i += cand_len[span];
                }
                else
                {
                    const uint8_t *sp = norm + ctx.off[f + i];
                    hash128_t      id;

                    if (laplace_content_root_id(sp, (size_t) ctx.len[f + i], &id) != 0)
                        elog(ERROR, "word_segment_resolved: root id failed");
                    word_seg_emit(&out, ordinal++, sp, ctx.len[f + i], &id);
                    i++;
                }
            }
        }
    }

    return (Datum) 0;
}
