/*
 * trajectory_walk.c — native stride-continuation generation over the witnessed
 * trajectories. SINGLE SOURCE: physicalities.trajectory (each vertex mantissa-
 * encodes its entity id) — there is no content_index table, no constituency_edge
 * table, no content_pairs cache. The trajectory stream is built per backend
 * straight from the trajectory geometries: one edge scan (ST_DumpPoints +
 * mantissa_unpack, tier > 2), an explicit-stack walk from the roots down to the
 * token floor, then a suffix array over the resulting stream.
 *
 * The separator rule (word stride): whitespace tokens are witnessed content
 * (round-trip law) but are NOT units of order — they are excluded from the
 * stream, so stride contexts, trajectory pairs, and gap distances all count in
 * content-bearing strides. The display layer re-inserts separators
 * (SubstrateClient.cs) and is the single separator authority on output.
 *
 * Stride descent floor: when no witnessed continuation exists at any order, the
 * COMPLETES_TO consensus supplies candidates (eff-μ weighted) — emitted with
 * stride_used = 0, the audit marker for a consensus-backed step.
 *
 * Weighted selection is seeded (splitmix64), so a (trajectory stream, prompt,
 * seed) triple reproduces its output exactly — generation is
 * testimony-deterministic.
 *
 * Cache: TopMemoryContext child, invalidated when (count, max(observed_at)) of
 * trajectory-bearing physicalities changes.
 */
#include "postgres.h"

#include <math.h>

#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/fmgrprotos.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/timestamp.h"
#include "utils/tuplestore.h"
#include "lib/stringinfo.h"
#include "common/pg_prng.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);
PG_FUNCTION_INFO_V1(pg_laplace_stream_reset);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_peer);
PG_FUNCTION_INFO_V1(pg_laplace_variant_walk);
PG_FUNCTION_INFO_V1(pg_laplace_respell_variant);

#define GEN_COMPARE_CAP   64      /* suffix order depth; >= max usable context */
#define GEN_MAX_ORDER     16
#define GEN_MAX_STEPS     2048
#define GEN_SENTINEL      (-1)

typedef struct VocabEntry
{
    char  key[16];
    int32 id;
} VocabEntry;

typedef struct GenCorpus
{
    MemoryContext cxt;
    int32   *stream;        /* token ids with GEN_SENTINEL between sequences */
    int32    stream_len;
    int32   *suffix;        /* sorted suffix start positions (token positions only) */
    int32    n_suffix;
    char   (*ids)[16];      /* vocab id -> 16-byte entity id */
    int32    n_vocab;
    int32    vocab_cap;
    HTAB    *vocab;         /* 16-byte entity id -> vocab id */
    int64    sequences;     /* witnessed roots walked                  */
    int64    separators;    /* whitespace tokens excluded from order   */
    int64    probe_rows;    /* invalidation: trajectory physicalities  */
    int64    probe_max_us;  /* invalidation: max(observed_at) epoch µs */
} GenCorpus;

static GenCorpus *gen_corpus = NULL;

static void
gen_corpus_free(void)
{
    if (gen_corpus != NULL)
    {
        MemoryContextDelete(gen_corpus->cxt);
        gen_corpus = NULL;
    }
}

/* ── suffix order ──────────────────────────────────────────────────────────── */

static const int32 *cmp_stream;
static int32        cmp_len;

static int
suffix_cmp(const void *pa, const void *pb)
{
    int32 a = *(const int32 *) pa;
    int32 b = *(const int32 *) pb;

    for (int k = 0; k < GEN_COMPARE_CAP; k++)
    {
        int32 ta = (a + k < cmp_len) ? cmp_stream[a + k] : GEN_SENTINEL;
        int32 tb = (b + k < cmp_len) ? cmp_stream[b + k] : GEN_SENTINEL;

        if (ta != tb)
            return (ta < tb) ? -1 : 1;
        if (ta == GEN_SENTINEL)
            return 0;
    }
    return 0;
}

/* compare the suffix at stream position s against a context of k tokens */
static int
prefix_cmp(const GenCorpus *c, int32 s, const int32 *ctx, int k)
{
    for (int i = 0; i < k; i++)
    {
        int32 t = (s + i < c->stream_len) ? c->stream[s + i] : GEN_SENTINEL;

        if (t != ctx[i])
            return (t < ctx[i]) ? -1 : 1;
    }
    return 0;
}

/* ── trajectory-stream build: physicalities.trajectory is the single source ──── */

/* the witnessed constituency, one ordered edge scan: parent → (child, run),
 * parents tier > 2, the per-entity trajectory chosen deterministically
 * (lowest source_id — the same canon as constituents()) */
static const char *CORPUS_EDGE_QUERY =
    "SELECT p.entity_id, u.entity_id, GREATEST(u.run_length, 1)::int "
    "FROM (SELECT DISTINCT ON (entity_id) entity_id, trajectory "
    "      FROM laplace.physicalities "
    "      WHERE type = 1 AND trajectory IS NOT NULL "
    "      ORDER BY entity_id, source_id) p "
    "JOIN laplace.entities e ON e.id = p.entity_id AND e.tier > 2, "
    "LATERAL public.ST_DumpPoints(p.trajectory) dp, "
    "LATERAL public.laplace_mantissa_unpack(dp.geom) u "
    "ORDER BY p.entity_id, u.ordinal";

static const char *CORPUS_PROBE =
    "SELECT count(*)::int8, "
    "       COALESCE((extract(epoch FROM max(observed_at)) * 1000000)::int8, 0) "
    "FROM laplace.physicalities WHERE type = 1 AND trajectory IS NOT NULL";

/* whitespace-only renders are separators: witnessed in content, excluded from
 * order. One batch query per trajectory-stream build. The predicate is the engine's
 * Unicode White_Space law (is_all_whitespace), NOT an ASCII [[:space:]] regex
 * — U+3000, NBSP, U+2000..200A are separators in every script, not word units. */
static const char *CORPUS_SEPARATOR_QUERY =
    "SELECT v.ord::int4 - 1 "
    "FROM unnest($1::bytea[]) WITH ORDINALITY v(id, ord) "
    "WHERE laplace.is_all_whitespace(laplace.render_text(v.id, 8))";

#define CORPUS_WALK_DEPTH_CAP 64

typedef struct CorpusNode
{
    char  id[16];                  /* dynahash key (must be first)            */
    int32 idx;                     /* node ordinal                            */
} CorpusNode;

typedef struct NodeMeta
{
    int32 first_edge;              /* into the edge arena; -1 = leaf          */
    int32 n_edges;
    bool  has_parent;
} NodeMeta;

typedef struct CorpusEdge
{
    int32 child;                   /* node idx                                */
    int32 run;
} CorpusEdge;

typedef struct WalkFrame
{
    int32 node;                    /* node idx                                */
    int32 edge_i;                  /* next edge to take                       */
    int32 rep_left;                /* repetitions left for the current edge   */
} WalkFrame;

static void
corpus_probe(int64 *rows, int64 *max_us)
{
    bool isnull;

    if (SPI_execute(CORPUS_PROBE, true, 1) != SPI_OK_SELECT || SPI_processed == 0)
        elog(ERROR, "trajectory_stream: trajectory probe failed");
    *rows = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                        SPI_tuptable->tupdesc, 1, &isnull));
    *max_us = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 2, &isnull));
}

static int32
corpus_vocab_intern(GenCorpus *c, const char key[16])
{
    bool        found;
    VocabEntry *ve = (VocabEntry *) hash_search(c->vocab, key, HASH_ENTER, &found);

    if (!found)
    {
        if (c->n_vocab == c->vocab_cap)
        {
            MemoryContext old = MemoryContextSwitchTo(c->cxt);

            c->vocab_cap *= 2;
            c->ids = (char (*)[16]) repalloc(c->ids, sizeof(char[16]) * c->vocab_cap);
            MemoryContextSwitchTo(old);
        }
        ve->id = c->n_vocab;
        memcpy(c->ids[c->n_vocab], key, 16);
        c->n_vocab++;
    }
    return ve->id;
}

static void
corpus_build(int64 probe_rows, int64 probe_max_us)
{
    MemoryContext cxt, walk_cxt, old;
    GenCorpus *c;
    HASHCTL    ctl;
    HTAB      *node_hash;
    NodeMeta  *meta;
    int32      n_nodes = 0, node_cap = 8192;
    CorpusEdge *edges;
    int64      n_edges = 0, edge_cap = 65536;
    int32     *raw = NULL;          /* leaf vocab ids incl. separators        */
    int64      raw_len = 0, raw_cap = 65536;
    uint8     *is_sep;
    Portal     portal;
    int32      cur_parent = -1;

    gen_corpus_free();

    cxt = AllocSetContextCreate(TopMemoryContext, "laplace generation corpus",
                                ALLOCSET_DEFAULT_SIZES);
    /* scratch (edges, node metadata, raw stream) dies at the end of the build */
    walk_cxt = AllocSetContextCreate(CurrentMemoryContext,
                                     "laplace corpus walk scratch",
                                     ALLOCSET_DEFAULT_SIZES);

    old = MemoryContextSwitchTo(cxt);
    c = (GenCorpus *) palloc0(sizeof(GenCorpus));
    c->cxt = cxt;
    c->probe_rows = probe_rows;
    c->probe_max_us = probe_max_us;
    c->vocab_cap = 4096;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = 16;
    ctl.entrysize = sizeof(VocabEntry);
    ctl.hcxt      = cxt;
    c->vocab = hash_create("generation vocab", 8192, &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    c->ids = (char (*)[16]) palloc(sizeof(char[16]) * c->vocab_cap);
    MemoryContextSwitchTo(old);

    old = MemoryContextSwitchTo(walk_cxt);
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = 16;
    ctl.entrysize = sizeof(CorpusNode);
    ctl.hcxt      = walk_cxt;
    node_hash = hash_create("corpus nodes", 65536, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    meta  = (NodeMeta *) palloc(sizeof(NodeMeta) * node_cap);
    edges = (CorpusEdge *) palloc(sizeof(CorpusEdge) * edge_cap);
    raw   = (int32 *) palloc(sizeof(int32) * raw_cap);
    MemoryContextSwitchTo(old);

    /* 1) edge scan (cursor: bounded SPI tuptable) */
    portal = SPI_cursor_open_with_args(
        "corpus_edges", CORPUS_EDGE_QUERY, 0, NULL, NULL, NULL, true, 0);
    for (;;)
    {
        SPI_cursor_fetch(portal, true, 65536);
        if (SPI_processed == 0)
            break;

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool      isnull;
            bytea    *pb  = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
            bytea    *cb  = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
            int32     run = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
            char      key[16];
            bool      found;
            CorpusNode *pn, *cn;
            int32     pidx, cidx;

            if (VARSIZE_ANY_EXHDR(pb) != 16 || VARSIZE_ANY_EXHDR(cb) != 16)
                ereport(ERROR, (errmsg("trajectory_stream: trajectory ids must be 16 bytes")));

            old = MemoryContextSwitchTo(walk_cxt);
            memcpy(key, VARDATA_ANY(pb), 16);
            pn = (CorpusNode *) hash_search(node_hash, key, HASH_ENTER, &found);
            if (!found)
            {
                if (n_nodes == node_cap)
                {
                    node_cap *= 2;
                    meta = (NodeMeta *) repalloc(meta, sizeof(NodeMeta) * node_cap);
                }
                pn->idx = n_nodes;
                meta[n_nodes].first_edge = -1;
                meta[n_nodes].n_edges = 0;
                meta[n_nodes].has_parent = false;
                n_nodes++;
            }
            pidx = pn->idx;

            memcpy(key, VARDATA_ANY(cb), 16);
            cn = (CorpusNode *) hash_search(node_hash, key, HASH_ENTER, &found);
            if (!found)
            {
                if (n_nodes == node_cap)
                {
                    node_cap *= 2;
                    meta = (NodeMeta *) repalloc(meta, sizeof(NodeMeta) * node_cap);
                    /* pn may have moved with meta only; node hash entries are stable */
                }
                cn->idx = n_nodes;
                meta[n_nodes].first_edge = -1;
                meta[n_nodes].n_edges = 0;
                meta[n_nodes].has_parent = true;
                n_nodes++;
            }
            else
                meta[cn->idx].has_parent = true;
            cidx = cn->idx;

            /* parent-grouped input: a parent's edges land contiguously */
            if (pidx != cur_parent)
            {
                if (meta[pidx].n_edges != 0)
                    ereport(ERROR, (errmsg(
                        "trajectory_stream: trajectory edge scan not parent-grouped")));
                meta[pidx].first_edge = (int32) n_edges;
                cur_parent = pidx;
            }
            if (n_edges == edge_cap)
            {
                edge_cap *= 2;
                edges = (CorpusEdge *) repalloc(edges, sizeof(CorpusEdge) * edge_cap);
            }
            edges[n_edges].child = cidx;
            edges[n_edges].run = (run < 1) ? 1 : run;
            n_edges++;
            meta[pidx].n_edges++;
            MemoryContextSwitchTo(old);
        }
        SPI_freetuptable(SPI_tuptable);
        CHECK_FOR_INTERRUPTS();
    }
    SPI_cursor_close(portal);

    /* the distinct-entities set carries every LEAF; node ids live in the node hash. To map a
     * leaf node idx back to its 16-byte id during the walk, keep a parallel
     * id table (filled lazily from hash entries). */
    {
        char (*node_ids)[16];
        HASH_SEQ_STATUS hs;
        CorpusNode *n;
        WalkFrame  *stack;
        int         depth;
        int32       root;

        old = MemoryContextSwitchTo(walk_cxt);
        node_ids = (char (*)[16]) palloc(sizeof(char[16]) * Max(n_nodes, 1));
        stack = (WalkFrame *) palloc(sizeof(WalkFrame) * CORPUS_WALK_DEPTH_CAP);
        MemoryContextSwitchTo(old);

        hash_seq_init(&hs, node_hash);
        while ((n = (CorpusNode *) hash_seq_search(&hs)) != NULL)
            memcpy(node_ids[n->idx], n->id, 16);

        /* 2) walk every root (a parent no one contains), node-intern order */
        for (root = 0; root < n_nodes; root++)
        {
            if (meta[root].n_edges == 0 || meta[root].has_parent)
                continue;

            depth = 0;
            stack[depth].node = root;
            stack[depth].edge_i = 0;
            stack[depth].rep_left = 0;

            while (depth >= 0)
            {
                WalkFrame *f = &stack[depth];

                if (f->edge_i >= meta[f->node].n_edges)
                {
                    depth--;
                    continue;
                }

                {
                    CorpusEdge *e = &edges[meta[f->node].first_edge + f->edge_i];

                    if (meta[e->child].n_edges == 0)
                    {
                        /* leaf: emit run repetitions into the raw stream */
                        int32 vid = corpus_vocab_intern(c, node_ids[e->child]);

                        for (int32 k = 0; k < e->run; k++)
                        {
                            if (raw_len + 2 > raw_cap)
                            {
                                old = MemoryContextSwitchTo(walk_cxt);
                                raw_cap *= 2;
                                raw = (int32 *) repalloc(raw, sizeof(int32) * raw_cap);
                                MemoryContextSwitchTo(old);
                            }
                            raw[raw_len++] = vid;
                        }
                        f->edge_i++;
                        f->rep_left = 0;
                        continue;
                    }

                    /* composite child: descend run times */
                    if (f->rep_left == 0)
                        f->rep_left = e->run;
                    f->rep_left--;
                    if (f->rep_left == 0)
                        f->edge_i++;

                    if (depth + 1 >= CORPUS_WALK_DEPTH_CAP)
                        ereport(ERROR, (errmsg(
                            "trajectory_stream: constituency deeper than %d (cycle?)",
                            CORPUS_WALK_DEPTH_CAP)));
                    depth++;
                    stack[depth].node = e->child;
                    /* re-read meta for the child frame */
                    stack[depth].edge_i = 0;
                    stack[depth].rep_left = 0;
                }
            }

            /* sequence boundary */
            if (raw_len + 2 > raw_cap)
            {
                old = MemoryContextSwitchTo(walk_cxt);
                raw_cap *= 2;
                raw = (int32 *) repalloc(raw, sizeof(int32) * raw_cap);
                MemoryContextSwitchTo(old);
            }
            raw[raw_len++] = GEN_SENTINEL;
            c->sequences++;
            CHECK_FOR_INTERRUPTS();
        }
    }

    /* 3) separator classification, one batch query over the distinct entities */
    old = MemoryContextSwitchTo(walk_cxt);
    is_sep = (uint8 *) palloc0((Size) Max(c->n_vocab, 1));
    MemoryContextSwitchTo(old);
    if (c->n_vocab > 0)
    {
        Datum     *elems = (Datum *) palloc(sizeof(Datum) * c->n_vocab);
        ArrayType *arr;
        Oid        argtypes[1] = { BYTEAARRAYOID };
        Datum      args[1];
        int        rc;

        for (int32 i = 0; i < c->n_vocab; i++)
        {
            bytea *b = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(b, VARHDRSZ + 16);
            memcpy(VARDATA(b), c->ids[i], 16);
            elems[i] = PointerGetDatum(b);
        }
        arr = construct_array(elems, c->n_vocab, BYTEAOID, -1, false, TYPALIGN_INT);
        args[0] = PointerGetDatum(arr);

        rc = SPI_execute_with_args(CORPUS_SEPARATOR_QUERY, 1, argtypes, args,
                                   NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "trajectory_stream: separator classification failed: %s",
                 SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            bool  isnull;
            int32 ord = DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[r],
                                                    SPI_tuptable->tupdesc, 1, &isnull));

            if (ord >= 0 && ord < c->n_vocab)
            {
                is_sep[ord] = 1;
                c->separators++;
            }
        }
    }

    /* 4) compact (drop separators, collapse empty sequences) + suffix array */
    old = MemoryContextSwitchTo(cxt);
    c->stream = (int32 *) palloc(sizeof(int32) * (Size) (raw_len + 2));
    MemoryContextSwitchTo(old);
    {
        bool at_boundary = true;

        for (int64 i = 0; i < raw_len; i++)
        {
            int32 t = raw[i];

            if (t == GEN_SENTINEL)
            {
                if (!at_boundary)
                {
                    c->stream[c->stream_len++] = GEN_SENTINEL;
                    at_boundary = true;
                }
                continue;
            }
            if (is_sep[t])
                continue;
            c->stream[c->stream_len++] = t;
            at_boundary = false;
        }
        if (!at_boundary)
            c->stream[c->stream_len++] = GEN_SENTINEL;
    }

    old = MemoryContextSwitchTo(cxt);
    c->suffix = (int32 *) palloc(sizeof(int32) * (Size) Max(c->stream_len, 1));
    MemoryContextSwitchTo(old);
    for (int32 i = 0; i < c->stream_len; i++)
        if (c->stream[i] != GEN_SENTINEL)
            c->suffix[c->n_suffix++] = i;

    cmp_stream = c->stream;
    cmp_len    = c->stream_len;
    qsort(c->suffix, c->n_suffix, sizeof(int32), suffix_cmp);

    MemoryContextDelete(walk_cxt);
    gen_corpus = c;
}

static GenCorpus *
corpus_ensure(void)
{
    int64 rows, max_us;

    corpus_probe(&rows, &max_us);
    if (rows == 0)
        ereport(ERROR, (errmsg(
            "trajectory_stream: no witnessed trajectories — deposit content first")));
    if (gen_corpus == NULL
        || gen_corpus->probe_rows != rows
        || gen_corpus->probe_max_us != max_us)
        corpus_build(rows, max_us);
    return gen_corpus;
}

/* ── trajectory-stream observability (the warm verb) ────────────────────────── */

PG_FUNCTION_INFO_V1(pg_laplace_stream_stats);

Datum
pg_laplace_stream_stats(PG_FUNCTION_ARGS)
{
    GenCorpus *c;
    TupleDesc  tupdesc;
    Datum      values[6];
    bool       nulls[6] = { false, false, false, false, false, false };
    HeapTuple  tuple;
    TimestampTz max_ts;

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR, (errmsg("stream_stats: return type must be a row type")));
    BlessTupleDesc(tupdesc);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "stream_stats: SPI_connect failed");
    c = corpus_ensure();
    SPI_finish();

    max_ts = (TimestampTz) c->probe_max_us
        - (POSTGRES_EPOCH_JDATE - UNIX_EPOCH_JDATE) * USECS_PER_DAY;

    values[0] = Int64GetDatum(c->sequences);
    values[1] = Int64GetDatum((int64) c->n_suffix);
    values[2] = Int32GetDatum(c->n_vocab);
    values[3] = Int64GetDatum(c->separators);
    values[4] = Int64GetDatum(c->probe_rows);
    values[5] = TimestampTzGetDatum(max_ts);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

/* ── trajectory pairs from the trajectory stream (word-stride by construction) ─ */

PG_FUNCTION_INFO_V1(pg_laplace_cooccurrence_scan);

typedef struct StreamPairKey
{
    int32 subject;
    int32 object;
    int32 gap;
} StreamPairKey;

typedef struct StreamPairEntry
{
    StreamPairKey key;             /* dynahash requires key first */
    int64         cnt;
} StreamPairEntry;

Datum
pg_laplace_cooccurrence_scan(PG_FUNCTION_ARGS)
{
    int32            max_gap = PG_GETARG_INT32(0);
    ReturnSetInfo   *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    TupleDesc        tupdesc;
    Tuplestorestate *tupstore;
    MemoryContext    per_query, oldctx;
    HASHCTL          hctl;
    HTAB            *pairs;
    GenCorpus       *c;
    int32            win[64];
    int              win_len = 0;
    HASH_SEQ_STATUS  seq;
    StreamPairEntry *e;

    if (max_gap < 1 || max_gap > 64)
        ereport(ERROR, (errmsg("cooccurrence_scan: max_gap must be 1..64 (got %d)", max_gap)));
    if (rsinfo == NULL || !IsA(rsinfo, ReturnSetInfo) ||
        (rsinfo->allowedModes & SFRM_Materialize) == 0)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("cooccurrence_scan: set-valued function called in context "
                    "that cannot accept a set")));

    per_query = rsinfo->econtext->ecxt_per_query_memory;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR, (errmsg("cooccurrence_scan: return type must be a row type")));

    oldctx = MemoryContextSwitchTo(per_query);
    tupstore = tuplestore_begin_heap(false, false, work_mem);
    rsinfo->returnMode = SFRM_Materialize;
    rsinfo->setResult = tupstore;
    rsinfo->setDesc = CreateTupleDescCopy(tupdesc);
    MemoryContextSwitchTo(oldctx);

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize = sizeof(StreamPairKey);
    hctl.entrysize = sizeof(StreamPairEntry);
    hctl.hcxt = CurrentMemoryContext;
    pairs = hash_create("cooccurrence_scan", 4 * 1024 * 1024, &hctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "cooccurrence_scan: SPI_connect failed");
    c = corpus_ensure();
    SPI_finish();

    for (int32 i = 0; i < c->stream_len; i++)
    {
        int32 tok = c->stream[i];

        if (tok == GEN_SENTINEL)
        {
            win_len = 0;
            continue;
        }

        for (int d = 1; d <= win_len; d++)
        {
            StreamPairKey    key;
            StreamPairEntry *ent;
            bool             found;

            memset(&key, 0, sizeof(key));
            key.subject = win[win_len - d];
            key.object = tok;
            key.gap = d;
            ent = (StreamPairEntry *) hash_search(pairs, &key, HASH_ENTER, &found);
            if (!found)
                ent->cnt = 0;
            ent->cnt++;
        }

        if (win_len < max_gap)
            win[win_len++] = tok;
        else
        {
            memmove(win, win + 1, sizeof(int32) * (max_gap - 1));
            win[max_gap - 1] = tok;
        }

        if ((i & 0xFFFFF) == 0)
            CHECK_FOR_INTERRUPTS();
    }

    hash_seq_init(&seq, pairs);
    while ((e = (StreamPairEntry *) hash_seq_search(&seq)) != NULL)
    {
        Datum  values[4];
        bool   nulls[4] = { false, false, false, false };
        bytea *s = (bytea *) palloc(VARHDRSZ + 16);
        bytea *o = (bytea *) palloc(VARHDRSZ + 16);

        SET_VARSIZE(s, VARHDRSZ + 16);
        memcpy(VARDATA(s), c->ids[e->key.subject], 16);
        SET_VARSIZE(o, VARHDRSZ + 16);
        memcpy(VARDATA(o), c->ids[e->key.object], 16);

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

/* ── seeded weighted selection (splitmix64) ────────────────────────────────── */

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

static double
rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) * (1.0 / 9007199254740992.0);
}

/* ── continuation distribution for an exact context ────────────────────────── */

typedef struct Continuation
{
    int32 token;
    int64 weight;
} Continuation;

/* binary-search the suffix range matching ctx[0..k), then count next tokens */
static int
continuations_collect(const GenCorpus *c, const int32 *ctx, int k,
                      Continuation *out, int out_cap)
{
    int32 lo = 0, hi = c->n_suffix, first, last;
    int   n = 0;

    while (lo < hi)                     /* lower bound */
    {
        int32 mid = lo + (hi - lo) / 2;
        if (prefix_cmp(c, c->suffix[mid], ctx, k) < 0) lo = mid + 1;
        else hi = mid;
    }
    first = lo;
    hi = c->n_suffix;
    while (lo < hi)                     /* upper bound */
    {
        int32 mid = lo + (hi - lo) / 2;
        if (prefix_cmp(c, c->suffix[mid], ctx, k) <= 0) lo = mid + 1;
        else hi = mid;
    }
    last = lo;

    for (int32 i = first; i < last; i++)
    {
        int32 next_pos = c->suffix[i] + k;
        int32 tok;
        int   j;

        if (next_pos >= c->stream_len)
            continue;
        tok = c->stream[next_pos];
        if (tok == GEN_SENTINEL)
            continue;

        for (j = 0; j < n; j++)
            if (out[j].token == tok) { out[j].weight++; break; }
        if (j == n)
        {
            if (n == out_cap)
            {
                /* keep the heaviest seen so far; evict the lightest */
                int lightest = 0;
                for (int m = 1; m < n; m++)
                    if (out[m].weight < out[lightest].weight) lightest = m;
                if (out[lightest].weight > 1)
                    continue;
                out[lightest].token = tok;
                out[lightest].weight = 1;
                continue;
            }
            out[n].token = tok;
            out[n].weight = 1;
            n++;
        }
    }
    return n;
}

/* ── generation ────────────────────────────────────────────────────────────── */

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *ctx_arr;
    int32      steps, max_order, topk;
    float8     temp;
    uint64     rng;
    GenCorpus *c;
    Datum     *elems;
    bool      *nulls;
    int        n_in;
    int32     *ctx;
    int        ctx_len = 0, ctx_cap;
    Continuation *cand;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("walk_continuations: context must not be NULL")));
    ctx_arr   = PG_GETARG_ARRAYTYPE_P(0);
    steps     = PG_ARGISNULL(1) ? 24  : PG_GETARG_INT32(1);
    max_order = PG_ARGISNULL(2) ? 5   : PG_GETARG_INT32(2);
    temp      = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    topk      = PG_ARGISNULL(4) ? 10  : PG_GETARG_INT32(4);
    rng       = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D)
                                : (uint64) PG_GETARG_INT64(5);

    if (steps < 1 || steps > GEN_MAX_STEPS)
        ereport(ERROR, (errmsg("walk_continuations: steps must be in [1,%d]", GEN_MAX_STEPS)));
    if (max_order < 1 || max_order > GEN_MAX_ORDER)
        ereport(ERROR, (errmsg("walk_continuations: max_order must be in [1,%d]", GEN_MAX_ORDER)));
    if (topk < 1 || topk > 256)
        ereport(ERROR, (errmsg("walk_continuations: topk must be in [1,256]")));
    if (ARR_NDIM(ctx_arr) != 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: context must be a 1-D bytea array")));

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_continuations: SPI_connect failed");
    c = corpus_ensure();

    deconstruct_array(ctx_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n_in);

    ctx_cap = n_in + steps;
    ctx = (int32 *) palloc(sizeof(int32) * (ctx_cap > 8 ? ctx_cap : 8));
    for (int i = 0; i < n_in; i++)
    {
        bytea *b;
        char   key[16];
        VocabEntry *ve;
        bool   found;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            continue;
        memcpy(key, VARDATA_ANY(b), 16);
        ve = (VocabEntry *) hash_search(c->vocab, key, HASH_FIND, &found);
        if (found)
            ctx[ctx_len++] = ve->id;
    }

    cand = (Continuation *) palloc(sizeof(Continuation) * 256);

    for (int32 step = 1; step <= steps; step++)
    {
        int   n_cand = 0, used = 0;
        int32 pick = GEN_SENTINEL;

        for (int k = (ctx_len < max_order ? ctx_len : max_order); k >= 1; k--)
        {
            n_cand = continuations_collect(c, ctx + ctx_len - k, k, cand, 256);
            if (n_cand > 0) { used = k; break; }
        }
        if (n_cand == 0 && ctx_len > 0)
        {
            /* The witnessed stream is silent at every order: the COMPLETES_TO
             * consensus is the floor. eff-μ in whole-μ units as the weight;
             * stride_used = 0 marks the step as consensus-backed (the precedent
             * walk is walk_strongest, recall.c). */
            static const char *FLOOR_SQL =
                "SELECT c.object_id, "
                "       GREATEST(laplace.eff_mu(c.rating, c.rd) / 1000000000, 1)::int8 "
                "FROM laplace.consensus c "
                "WHERE c.subject_id = $1 AND c.object_id IS NOT NULL "
                "  AND c.type_id = laplace.relation_type_id('COMPLETES_TO') "
                "  AND NOT laplace.refuted(c.rating, c.rd) "
                "ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT $2";
            Oid    argtypes[2] = { BYTEAOID, INT4OID };
            Datum  args[2];
            bytea *subj = (bytea *) palloc(VARHDRSZ + 16);
            int    rc;

            SET_VARSIZE(subj, VARHDRSZ + 16);
            memcpy(VARDATA(subj), c->ids[ctx[ctx_len - 1]], 16);
            args[0] = PointerGetDatum(subj);
            args[1] = Int32GetDatum(topk);
            rc = SPI_execute_with_args(FLOOR_SQL, 2, argtypes, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: consensus floor probe failed: %s",
                     SPI_result_code_string(rc));
            for (uint64 r = 0; r < SPI_processed && n_cand < 256; r++)
            {
                bool   isnull;
                bytea *ob = DatumGetByteaPP(SPI_getbinval(SPI_tuptable->vals[r],
                                                          SPI_tuptable->tupdesc, 1, &isnull));
                char   key[16];

                if (VARSIZE_ANY_EXHDR(ob) != 16)
                    continue;
                memcpy(key, VARDATA_ANY(ob), 16);
                cand[n_cand].token = corpus_vocab_intern(c, key);
                cand[n_cand].weight = DatumGetInt64(
                    SPI_getbinval(SPI_tuptable->vals[r], SPI_tuptable->tupdesc, 2, &isnull));
                n_cand++;
            }
            used = 0;
        }
        if (n_cand == 0)
            break;

        /* heaviest candidate cap, then weighted draw among them */
        for (int i = 0; i < n_cand; i++)        /* partial selection sort */
        {
            int best = i;
            for (int j = i + 1; j < n_cand; j++)
                if (cand[j].weight > cand[best].weight) best = j;
            if (best != i)
            {
                Continuation t = cand[i]; cand[i] = cand[best]; cand[best] = t;
            }
            if (i + 1 >= topk) break;
        }
        {
            int    limit = (n_cand < topk) ? n_cand : topk;
            double best_key = 0;
            for (int i = 0; i < limit; i++)
            {
                double u = rng_uniform(&rng);
                double key = -log(u) / pow((double) cand[i].weight,
                                           1.0 / (temp > 1e-6 ? temp : 1e-6));
                if (i == 0 || key < best_key) { best_key = key; pick = cand[i].token; }
            }
        }

        {
            bytea *out_tok = (bytea *) palloc(VARHDRSZ + 16);
            Datum  values[3];
            bool   rnulls[3] = { false, false, false };

            SET_VARSIZE(out_tok, VARHDRSZ + 16);
            memcpy(VARDATA(out_tok), c->ids[pick], 16);
            values[0] = Int32GetDatum(step);
            values[1] = PointerGetDatum(out_tok);
            values[2] = Int32GetDatum(used);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
        }

        ctx[ctx_len++] = pick;
    }

    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_stream_reset(PG_FUNCTION_ARGS)
{
    gen_corpus_free();
    PG_RETURN_BOOL(true);
}

/* ── variant synthesis over witnessed constituency ─────────────────────────── */

/* copy_bytea_datum lives in spi_common.h */

static Datum
consensus_peer_lookup(Datum id, int32 k)
{
    static const char *PEER_SQL =
        "WITH my_type AS ("
        "  SELECT type_id FROM laplace.entities WHERE id = $1"
        "), my_ctx AS ("
        "  SELECT c.type_id AS rel, "
        "         CASE WHEN c.subject_id = $1 THEN c.object_id ELSE c.subject_id END AS partner, "
        "         (c.subject_id = $1) AS as_subject, "
        "         laplace.eff_mu(c.rating, c.rd) AS mu "
        "  FROM laplace.consensus c "
        "  WHERE (c.subject_id = $1 OR c.object_id = $1) "
        "    AND c.object_id IS NOT NULL "
        "    AND NOT laplace.refuted(c.rating, c.rd)"
        "), elected AS ("
        "  SELECT x.id, sum(LEAST(m.mu, laplace.eff_mu(c2.rating, c2.rd))) AS score "
        "  FROM my_ctx m "
        "  JOIN laplace.consensus c2 ON c2.type_id = m.rel "
        "    AND NOT laplace.refuted(c2.rating, c2.rd) "
        "    AND ((m.as_subject AND c2.object_id = m.partner AND c2.subject_id <> $1) "
        "      OR (NOT m.as_subject AND c2.subject_id = m.partner AND c2.object_id <> $1)) "
        "  JOIN laplace.entities x "
        "    ON x.id = CASE WHEN m.as_subject THEN c2.subject_id ELSE c2.object_id END "
        "  JOIN my_type t ON x.type_id = t.type_id "
        "  GROUP BY x.id "
        "  ORDER BY score DESC "
        "  LIMIT $2"
        "), geometric AS ("
        "  SELECT near.id FROM ("
        "    SELECT e.type_id, p.coord, p.trajectory "
        "    FROM laplace.entities e "
        "    JOIN laplace.physicalities p ON p.entity_id = e.id AND p.type = 1 "
        "    WHERE e.id = $1 AND p.trajectory IS NOT NULL "
        "    LIMIT 1"
        "  ) me, "
        "  LATERAL ("
        "    SELECT knn.id, knn.t2 FROM ("
        "      SELECT e2.id, p2.trajectory AS t2 "
        "      FROM laplace.entities e2 "
        "      JOIN laplace.physicalities p2 ON p2.entity_id = e2.id AND p2.type = 1 "
        "      WHERE e2.type_id = me.type_id AND e2.id <> $1 AND p2.trajectory IS NOT NULL "
        "      ORDER BY p2.coord <<->> me.coord "
        "      LIMIT 48"
        "    ) knn "
        "    ORDER BY laplace.laplace_frechet_4d(knn.t2, me.trajectory) ASC "
        "    LIMIT $2"
        "  ) near"
        ") "
        "SELECT id FROM ("
        "  SELECT id FROM elected "
        "  UNION ALL "
        "  SELECT id FROM geometric "
        "  WHERE NOT EXISTS (SELECT 1 FROM elected)"
        ") z "
        "ORDER BY random() LIMIT 1";

    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;
    bool  isnull;

    args[0] = id;
    args[1] = Int32GetDatum(k);

    rc = SPI_execute_with_args(PEER_SQL, 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "consensus_peer: query failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return (Datum) 0;

    return copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
}

static char *
render_entity_text(Datum id, int32 max_depth)
{
    Oid   argtypes[2] = { BYTEAOID, INT4OID };
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;
    bool  isnull;
    text *t;

    args[0] = id;
    args[1] = Int32GetDatum(max_depth);
    nulls[1] = ' ';

    rc = SPI_execute_with_args(
        "SELECT laplace.render_text($1, $2)", 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "variant_walk: render_text failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
        return pstrdup("");

    t = DatumGetTextPP(SPI_getbinval(SPI_tuptable->vals[0],
                                      SPI_tuptable->tupdesc, 1, &isnull));
    if (isnull)
        return pstrdup("");
    return text_to_cstring(t);
}

typedef struct WalkPoint
{
    Datum  cid;
    int32  run;
    int32  ctier;
} WalkPoint;

static WalkPoint *
fetch_trajectory_points(Datum id, int *out_n)
{
    static const char *POINTS_SQL =
        "SELECT u.entity_id, GREATEST(u.run_length, 1)::int AS run, "
        "       (SELECT e.tier FROM laplace.entities e WHERE e.id = u.entity_id) AS ctier "
        "FROM laplace.physicalities p, "
        "LATERAL public.ST_DumpPoints(p.trajectory) dp, "
        "LATERAL public.laplace_mantissa_unpack(dp.geom) u "
        "WHERE p.entity_id = $1 AND p.type = 1 AND p.trajectory IS NOT NULL "
        "ORDER BY u.ordinal";

    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;
    WalkPoint *pts;

    rc = SPI_execute_with_args(POINTS_SQL, 1, argtypes, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "variant_walk: trajectory unpack failed: %s",
             SPI_result_code_string(rc));

    *out_n = (int) SPI_processed;
    if (*out_n == 0)
        return NULL;

    pts = (WalkPoint *) palloc(sizeof(WalkPoint) * (*out_n));
    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      isnull;

        pts[r].cid = copy_bytea_datum(SPI_getbinval(tup, td, 1, &isnull));
        pts[r].run = DatumGetInt32(SPI_getbinval(tup, td, 2, &isnull));
        pts[r].ctier = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
        if (isnull)
            pts[r].ctier = 0;
    }
    return pts;
}

static int32
entity_tier(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;
    bool  isnull;

    rc = SPI_execute_with_args(
        "SELECT tier FROM laplace.entities WHERE id = $1",
        1, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return -1;
    return DatumGetInt32(SPI_getbinval(SPI_tuptable->vals[0],
                                       SPI_tuptable->tupdesc, 1, &isnull));
}

static bool
has_trajectory(Datum id)
{
    Oid   argtypes[1] = { BYTEAOID };
    Datum args[1] = { id };
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT 1 FROM laplace.physicalities p "
        "WHERE p.entity_id = $1 AND p.type = 1 AND p.trajectory IS NOT NULL LIMIT 1",
        1, argtypes, args, NULL, true, 1);
    return (rc == SPI_OK_SELECT && SPI_processed > 0);
}

static char *
variant_walk_impl(Datum id, float8 swap, int32 k, int32 depth)
{
    int32      tier = entity_tier(id);
    StringInfoData out;
    WalkPoint *pts;
    int        n_pts;
    bool       first = true;

    if (tier < 0 || tier <= 2)
        return render_entity_text(id, 64);
    if (!has_trajectory(id))
        return render_entity_text(id, 64);

    pts = fetch_trajectory_points(id, &n_pts);
    if (pts == NULL || n_pts == 0)
        return render_entity_text(id, 64);

    initStringInfo(&out);
    for (int p = 0; p < n_pts; p++)
    {
        for (int i = 0; i < pts[p].run; i++)
        {
            Datum  cur = pts[p].cid;
            char  *piece;
            float8 rnd;

            if (depth > 0 && pts[p].ctier > 2)
            {
                rnd = pg_prng_double(&pg_global_prng_state);
                if (rnd < swap)
                {
                    Datum peer = consensus_peer_lookup(cur, k);
                    if (peer != (Datum) 0)
                        cur = peer;
                }
            }

            piece = variant_walk_impl(cur, swap, k, depth - 1);
            if (piece != NULL && piece[0] != '\0')
            {
                if (!first)
                    appendStringInfoChar(&out, ' ');
                appendStringInfoString(&out, piece);
                first = false;
            }
            if (piece != NULL)
                pfree(piece);
        }
    }
    pfree(pts);
    return out.data;
}

Datum
pg_laplace_consensus_peer(PG_FUNCTION_ARGS)
{
    Datum id;
    int32 k;
    Datum peer;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    k  = PG_ARGISNULL(1) ? 6 : PG_GETARG_INT32(1);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "consensus_peer: SPI_connect failed");
    peer = consensus_peer_lookup(id, k);
    SPI_finish();

    if (peer == (Datum) 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(peer);
}

Datum
pg_laplace_variant_walk(PG_FUNCTION_ARGS)
{
    Datum  id;
    float8 swap;
    int32  k, depth;
    char  *walk_out;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id    = PG_GETARG_DATUM(0);
    swap  = PG_ARGISNULL(1) ? 0.3 : PG_GETARG_FLOAT8(1);
    k     = PG_ARGISNULL(2) ? 6 : PG_GETARG_INT32(2);
    depth = PG_ARGISNULL(3) ? 4 : PG_GETARG_INT32(3);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "variant_walk: SPI_connect failed");
    walk_out = variant_walk_impl(id, swap, k, depth);
    SPI_finish();

    if (walk_out == NULL || walk_out[0] == '\0')
        PG_RETURN_TEXT_P(cstring_to_text(""));
    {
        MemoryContext old = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(walk_out);
        MemoryContextSwitchTo(old);
        pfree(walk_out);
        PG_RETURN_TEXT_P(result);
    }
}

Datum
pg_laplace_respell_variant(PG_FUNCTION_ARGS)
{
    text  *node_type;
    text  *modality;
    float8 swap;
    int32  k, depth;
    char  *seed_sql =
        "SELECT e.id FROM laplace.canonical_names n "
        "JOIN laplace.entities e ON e.type_id = n.id "
        "JOIN laplace.physicalities p ON p.entity_id = e.id AND p.type = 1 "
        "  AND p.trajectory IS NOT NULL "
        "WHERE n.name = 'substrate/type/grammar/' || $1 || '/' || $2 || '/v1' "
        "ORDER BY random() LIMIT 1";
    Oid    argtypes[2] = { TEXTOID, TEXTOID };
    Datum  args[2];
    char   nulls[3] = "  ";
    int    rc;
    bool   isnull;
    Datum  seed;
    char  *walk_text;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    node_type = PG_GETARG_TEXT_PP(0);
    modality  = PG_ARGISNULL(1) ? cstring_to_text("c-sharp") : PG_GETARG_TEXT_PP(1);
    swap      = PG_ARGISNULL(2) ? 0.3 : PG_GETARG_FLOAT8(2);
    k         = PG_ARGISNULL(3) ? 6 : PG_GETARG_INT32(3);
    depth     = PG_ARGISNULL(4) ? 4 : PG_GETARG_INT32(4);

    args[0] = PointerGetDatum(modality);
    args[1] = PointerGetDatum(node_type);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "respell_variant: SPI_connect failed");

    rc = SPI_execute_with_args(seed_sql, 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "respell_variant: seed lookup failed: %s", SPI_result_code_string(rc));
    if (SPI_processed == 0)
    {
        SPI_finish();
        PG_RETURN_NULL();
    }

    seed = copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull));
    walk_text = variant_walk_impl(seed, swap, k, depth);
    SPI_finish();

    if (walk_text == NULL)
        PG_RETURN_NULL();
    {
        MemoryContext old = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(walk_text);
        MemoryContextSwitchTo(old);
        pfree(walk_text);
        PG_RETURN_TEXT_P(result);
    }
}
