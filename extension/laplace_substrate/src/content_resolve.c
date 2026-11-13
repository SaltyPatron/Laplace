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

#include "content_resolve_internal.h"
#include "perfcache_native.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "prompt_input.h"
#include "consensus_scan.h"
#include "laplace/core/sql_catalog.h"

void
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

/* Word occurrences come from Native's UAX segment cut, independently of DAG
 * altitude. A word can be a scalar, a one-level composition or a deeper tree;
 * a sentence can itself be tier 2. Keep source order and repeated occurrences. */
int
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

