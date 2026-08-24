






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

#include "utils/array.h"

/* Joint-evidence hash entry: key must be first for HASH_BLOBS.
 *
 * INVENTION §7 rules out deciding a ranking on a single-token scalar, so the
 * election key is the rating tuple summed over the joint edges. `degree` holds
 * trajectory co-occurrence and is consulted only when the consensus graph
 * between the candidates is empty. */
typedef struct
{
    hash128_t key;
    int64     witnesses;
    int64     rating;
    int64     rd;
    int64     degree;
} joint_degree_entry;

/* Container -> which candidates sit inside it. first_candidate is the last
 * candidate seen, used only to avoid double-counting one candidate's own
 * repeated rows; n_candidates > 1 means the container is joint evidence. */
typedef struct
{
    hash128_t key;
    int       first_candidate;
    int       n_candidates;
} container_owner_entry;

/* Bounded per-candidate container fan-out. LIMIT must be a bound parameter:
 * the plan is kept process-wide, so a literal would pin the first caller's
 * value (containers_of.c, measured 3,245ms -> 40.4ms). */
#define RESOLVE_CONTAINER_PROBE_LIMIT 64

/*
 * MEASURED live 2026-08-14: this probe executes in 14ms on a hit and 25.6ms on
 * a miss, but PLANS in 33ms. SPI_execute_with_args re-plans on every call, so
 * planning dominated and scaled with the candidate count on a path every
 * surface calls through converse.resolve. Kept plan, same idiom as
 * generate_walk.c's ensure_edge_plan.
 *
 * LIMIT stays a bound parameter precisely BECAUSE the plan is kept -- and the
 * bound is what lets the Append short-circuit: on a hit, EXPLAIN shows
 * partitions h02..h63 "never executed". laplace.physicalities is HASH(id) over
 * 64 partitions and this joins by trajectory content, so nothing prunes; the
 * LIMIT is the only bound there is.
 */
static const char *RESOLVE_CONTAINER_QUERY =
    "SELECT w.id FROM laplace.v_word_points w "
    "WHERE public.laplace_trajectory_constituent_ids(w.trajectory) "
    "      @> ARRAY[$1]::bytea[] "
    "LIMIT $2";

static SPIPlanPtr resolve_container_plan = NULL;

static void
ensure_resolve_container_plan(void)
{
    if (resolve_container_plan == NULL)
    {
        Oid argtypes[2] = { BYTEAOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(RESOLVE_CONTAINER_QUERY, 2, argtypes);
        if (plan == NULL)
            elog(ERROR, "resolve_phrase: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "resolve_phrase: SPI_keepplan failed");
        resolve_container_plan = plan;
    }
}

/*
 * The election graph, as resolve_phrase's own spec states it: edges with BOTH
 * endpoints inside the candidate set, credited per endpoint, in ONE query over
 * the set rather than one probe per candidate.
 *
 * Per-candidate truncation cannot express an intersection. 64 containers drawn
 * from a 39,793,705-row posting list ('a') and 64 from a 3,207-row one ('wolf')
 * do not meet, so the joint term reads zero for the content word and the
 * leftmost-longest tie-break re-elects the interrogative — the exact defect §7
 * names. The projection is the rating tuple: §7 rules out a scalar key and §5
 * orders ranked reads by belief.
 */
static const char *RESOLVE_JOINT_EDGE_QUERY =
    "WITH cand AS (SELECT unnest($1::bytea[]) AS id), "
    "     e AS ("
    "       SELECT c.subject_id AS id, c.witness_count, c.rating, c.rd "
    "         FROM laplace.consensus c "
    "        WHERE c.subject_id IN (SELECT id FROM cand) "
    "          AND c.object_id  IN (SELECT id FROM cand) "
    "       UNION ALL "
    "       SELECT c.object_id AS id, c.witness_count, c.rating, c.rd "
    "         FROM laplace.consensus c "
    "        WHERE c.subject_id IN (SELECT id FROM cand) "
    "          AND c.object_id  IN (SELECT id FROM cand)) "
    "SELECT e.id, sum(e.witness_count)::bigint, sum(e.rating)::bigint, "
    "       min(e.rd)::bigint "
    "  FROM e GROUP BY e.id";

static SPIPlanPtr resolve_joint_edge_plan = NULL;

static void
ensure_resolve_joint_edge_plan(void)
{
    if (resolve_joint_edge_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(RESOLVE_JOINT_EDGE_QUERY, 1, argtypes);
        if (plan == NULL)
            elog(ERROR, "resolve_phrase: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "resolve_phrase: SPI_keepplan failed");
        resolve_joint_edge_plan = plan;
    }
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
             * Pass 2 elects jointly, per the election law: no ranking may be
             * decided on a single-token scalar, because the discriminating
             * information lives in the graph BETWEEN a prompt's tokens. A span
             * that shares consensus edges with the other candidate spans of the
             * same prompt is the topic; a span with no edges to any of them is
             * glue, however it ranks alone. Position and length are tie-breaks
             * only, never the criterion — leftmost-longest is what elected the
             * interrogative in "What is a wolf?".
             *
             * One SPI query over the candidate set, not per candidate: edges
             * with BOTH endpoints inside the set, counted per endpoint.
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
                    Datum      jarg[1];
                    bool       joint_from_consensus = false;
                    int        jrc;

                    ensure_resolve_joint_edge_plan();
                    jarg[0] = PointerGetDatum(cand_arr);
                    jrc = SPI_execute_plan(resolve_joint_edge_plan, jarg, NULL,
                                           true, 0);
                    if (jrc != SPI_OK_SELECT)
                        elog(ERROR, "resolve_phrase: joint edge query failed: %s",
                             SPI_result_code_string(jrc));

                    for (uint64 r = 0; r < SPI_processed; r++)
                    {
                        bool       isnull, dfound;
                        HeapTuple  tup = SPI_tuptable->vals[r];
                        TupleDesc  td  = SPI_tuptable->tupdesc;
                        hash128_t  eid;
                        joint_degree_entry *ent;
                        int64      rdv;

                        eid = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
                        if (isnull)
                            continue;
                        ent = (joint_degree_entry *)
                            hash_search(degree, &eid, HASH_ENTER, &dfound);
                        if (!dfound)
                        {
                            ent->witnesses = 0;
                            ent->rating    = 0;
                            ent->rd        = PG_INT64_MAX;
                            ent->degree    = 0;
                        }
                        ent->witnesses += DatumGetInt64(
                            SPI_getbinval(tup, td, 2, &isnull));
                        ent->rating += DatumGetInt64(
                            SPI_getbinval(tup, td, 3, &isnull));
                        rdv = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
                        if (!isnull && rdv < ent->rd)
                            ent->rd = rdv;
                        joint_from_consensus = true;
                    }

                    /* Trajectory co-occurrence is the fallback, never the
                     * primary: it is raw frequency, and raw frequency elects
                     * whatever appears everywhere. */
                    if (!joint_from_consensus)
                    {
                    /*
                     * Co-occurrence comes from the TRAJECTORY, not from
                     * attestations. Word-adjacency PRECEDES/CONTAINS were drained
                     * on purpose (13,497,079 rows, 34% of attestations, consumed by
                     * no read path) because the ordered constituent sequence already
                     * holds containment, co-occurrence and order losslessly. So the
                     * joint evidence for a candidate is: how many of ITS containers
                     * also contain another candidate from the same prompt.
                     *
                     * Single-key GIN probe per candidate against a kept plan, and
                     * LIMIT as a bound parameter — the multi-key `&&` form makes the
                     * planner abandon the index (850ms, 873,366 rows rechecked), and
                     * a fetch-count cap skips no bitmap work (3,245ms vs 40.4ms).
                     * See containers_of.c for both measurements.
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

                    /* One probe per candidate. Containers are kept so scoring
                     * reads memory instead of re-querying. */
                    for (int c = 0; c < n_pres; c++)
                    {
                        int qrc2;

                        cargs[0] = pres_elems[c];
                        cargs[1] = Int32GetDatum(RESOLVE_CONTAINER_PROBE_LIMIT);
                        qrc2 = SPI_execute_plan(resolve_container_plan,
                                                cargs, NULL, true, 0);
                        if (qrc2 != SPI_OK_SELECT)
                            elog(ERROR, "resolve_phrase: container probe failed: %s",
                                 SPI_result_code_string(qrc2));

                        for (uint64 r = 0; r < SPI_processed; r++)
                        {
                            bool      isnull, ofound;
                            hash128_t cid = datum_to_hash128(
                                SPI_getbinval(SPI_tuptable->vals[r],
                                              SPI_tuptable->tupdesc, 1, &isnull));
                            container_owner_entry *oe;

                            if (isnull)
                                continue;
                            cont[c * RESOLVE_CONTAINER_PROBE_LIMIT + cont_n[c]++] = cid;
                            oe = (container_owner_entry *)
                                hash_search(owners, &cid, HASH_ENTER, &ofound);
                            if (!ofound)
                            {
                                oe->first_candidate = c;
                                oe->n_candidates    = 1;
                            }
                            else if (oe->first_candidate != c)
                            {
                                oe->n_candidates++;
                                oe->first_candidate = c;
                            }
                        }
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
                 * Elect: highest joint degree wins. Length then position break
                 * ties, preserving the previous deterministic order for the
                 * degenerate case where the graph says nothing (single-token
                 * prompts, or a set with no edges between its members).
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
 * pg_laplace_word_segment_resolved — word_segment, but the substrate decides
 * where a word ends inside a run that has no boundary of its own.
 *
 * THE DEFECT. UAX#29 word break joins ALetter runs, so Latin, Cyrillic, Arabic
 * and Hangul words survive whole, while 4.1 puts dictionary segmentation for
 * Han, Hiragana, Katakana, Thai, Lao and Khmer explicitly out of scope. The
 * tier-2 nodes for those scripts are therefore single characters, and the word
 * a reader actually wrote is never addressed. Measured on the live substrate:
 * 自転車 (167 edges), 北京 (93), ある (81), สวัสดี (27) and 氷河 (21) all exist
 * and all carry rated evidence; converse.word_segment reaches none of them,
 * emitting 3, 2, 2, 4 and 2 fragments instead. No ranking downstream can
 * recover an address that was never formed.
 *
 * WHITESPACE IS A REAL BOUNDARY AND IS NOT CROSSED. Joining runs across a
 * space would make "hot dog" and "New York" into word tokens, and they are not
 * words -- they are tier-3 compositions OF two tier-2 words, and collapsing
 * them into the word rung is the tier confusion this is meant to end. So spans
 * are only considered inside a maximal run of tier-2 nodes that are byte
 * CONTIGUOUS in the normalized text: 氷|河 is contiguous and joins, hot|dog has
 * a gap and does not. The rule names no script and no language -- it says only
 * that where the text gave a boundary we keep it, and where it gave none, the
 * substrate is asked.
 *
 * NOTHING IS STORED. Adjacency stays a view over the trajectory (the 2026-07-25
 * ruling in relation_types.toml: PRECEDES and CONTAINS are "views derivable
 * from the trajectory rather than stored truth"). This mints no entity, writes
 * no attestation, and folds nothing; it computes candidate ids natively and
 * asks one batched membership question.
 *
 * The probe is deliberately tier-blind, exactly as resolve_phrase's is, and for
 * the same reason it must NOT be consensus.entity_exists(): that helper answers
 * true for any valid codepoint via the perfcache axiom, and under the tier-blind
 * content law a single-letter word IS its codepoint, so the axiom would let a
 * stopword hijack the join. "Is this a stored entity" is the stored-row
 * question.
 *
 * Degenerate case is the current behaviour: when no multi-node span resolves,
 * every run falls back to its individual tier-2 nodes and the output is
 * byte-identical to converse.word_segment. This is a strict superset.
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

    /* The emitted surfaces are slices of the tree's normalized text, and the
     * tree must be released before SPI so no native allocation crosses an
     * elog (same contract as resolve_phrase). Copy the bytes we still need. */
    norm = (uint8_t *) palloc(norm_len > 0 ? norm_len : 1);
    memcpy(norm, tree_text, norm_len);

    /* Maximal runs of byte-contiguous tier-2 nodes. A gap means the text
     * supplied a boundary -- whitespace, punctuation -- and it is kept. */
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

    /* Candidates: every sub-span of every run of length >= 2. Length-1 spans
     * need no probe -- they are the fallback and are always emitted if nothing
     * covers them. */
    n_cand_max = 0;
    for (r = 0; r < n_runs; r++)
    {
        int m = runs[r].count;

        if (m >= 2)
            n_cand_max += m * (m + 1) / 2;
    }

    if (n_cand_max == 0)
    {
        /* Nothing to resolve: identical to converse.word_segment. */
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

    /* One batched membership question for every candidate in the prompt. */
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
            "SELECT e.id FROM laplace.entities e WHERE e.id = ANY($1::bytea[])",
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
         * Covering, per run: longest span first, leftmost wins, then repeat on
         * what is still uncovered. Candidates are already enumerated L
         * descending then i ascending, so one forward pass is that order.
         * Fewest constituents is the tier-floor law doing the work a tuned
         * length penalty would otherwise do -- a longer resolved span is a
         * higher composition, which is what the ladder is for.
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
