/*
 * trajectory_corpus.c - the per-backend generation index: trajectory stream +
 * suffix array from physicalities.trajectory, cache invalidation, and the
 * stream_stats/stream_reset control verbs. Accessors exported via
 * trajectory_corpus.h. Split out of trajectory_walk.c.
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
#include "utils/guc.h"
#include <limits.h>
#include "spi_common.h"

#include "trajectory_corpus.h"

PG_FUNCTION_INFO_V1(pg_laplace_stream_reset);

/* Cap the generation-corpus build to this many trajectory edges (0 = unbounded).
 * The full witnessed-sentence corpus (tens of millions of tier>2 trajectories) builds a
 * hundreds-of-millions-token stream + suffix array — too large to build interactively
 * (it OOM/segfaults). Bounding it keeps the build tractable and scopes generation to a
 * manageable witnessed set. Registered as laplace_substrate.corpus_max_rows. */
int laplace_corpus_max_rows = 0;

void
laplace_corpus_guc_init(void)
{
    DefineCustomIntVariable(
        "laplace_substrate.corpus_max_rows",
        "Cap the generation-corpus build to N source sentences (0 = unbounded).",
        NULL, &laplace_corpus_max_rows, 0, 0, INT_MAX,
        PGC_SUSET, 0, NULL, NULL, NULL);
}

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
int
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

int32
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
            /* repalloc_huge: distinct-entity id table exceeds the 1GB palloc cap on
             * a large substrate (the 96GB box holds it; the cap is the only limit). */
            c->ids = (char (*)[16]) repalloc_huge(c->ids, sizeof(char[16]) * (Size) c->vocab_cap);
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
    c->build_max_rows = laplace_corpus_max_rows;
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

    /* 1) edge scan (cursor: bounded SPI tuptable). Build the source query with an
     * optional sentence cap: the outer ORDER BY materializes the whole corpus before the
     * cursor yields a row, so the ONLY effective bound is a LIMIT on the DISTINCT tier>2
     * sentences BEFORE the dump+sort. corpus_max_rows = max source sentences. */
    {
        StringInfoData q;
        initStringInfo(&q);
        /* Pick the sentence ids FIRST (cheap seq-scan + LIMIT), then probe their
         * trajectories via the entity_id index. Joining entities AFTER (tier>2 hash over
         * 41M) before the LIMIT was the killer — startup-blocked the whole corpus. */
        appendStringInfoString(&q,
            "WITH s AS (SELECT id FROM laplace.entities WHERE tier > 2");
        if (laplace_corpus_max_rows > 0)
            appendStringInfo(&q, " LIMIT %d", laplace_corpus_max_rows);
        appendStringInfoString(&q,
            ") "
            "SELECT p.entity_id, u.entity_id, GREATEST(u.run_length, 1)::int "
            "FROM (SELECT DISTINCT ON (pp.entity_id) pp.entity_id, pp.trajectory "
            "      FROM laplace.physicalities pp JOIN s ON s.id = pp.entity_id "
            "      WHERE pp.type = 1 AND pp.trajectory IS NOT NULL "
            "      ORDER BY pp.entity_id, pp.source_id) p, "
            "LATERAL public.ST_DumpPoints(p.trajectory) dp, "
            "LATERAL public.laplace_mantissa_unpack(dp.geom) u "
            "ORDER BY p.entity_id, u.ordinal");
        portal = SPI_cursor_open_with_args(
            "corpus_edges", q.data, 0, NULL, NULL, NULL, true, 0);
        pfree(q.data);
    }
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
                    meta = (NodeMeta *) repalloc_huge(meta, sizeof(NodeMeta) * (Size) node_cap);
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
                    meta = (NodeMeta *) repalloc_huge(meta, sizeof(NodeMeta) * (Size) node_cap);
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
                edges = (CorpusEdge *) repalloc_huge(edges, sizeof(CorpusEdge) * (Size) edge_cap);
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

        node_ids = (char (*)[16]) MemoryContextAllocHuge(walk_cxt, sizeof(char[16]) * (Size) Max(n_nodes, 1));
        old = MemoryContextSwitchTo(walk_cxt);
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
                                raw = (int32 *) repalloc_huge(raw, sizeof(int32) * (Size) raw_cap);
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
                raw = (int32 *) repalloc_huge(raw, sizeof(int32) * (Size) raw_cap);
                MemoryContextSwitchTo(old);
            }
            raw[raw_len++] = GEN_SENTINEL;
            c->sequences++;
            CHECK_FOR_INTERRUPTS();
        }
    }

    /* 3) separator classification (engine Unicode White_Space law via is_all_whitespace,
     * NOT ASCII): which vocab ids are witnessed whitespace separators. Build-time only. */
    old = MemoryContextSwitchTo(walk_cxt);
    is_sep = (uint8 *) palloc0((Size) Max(c->n_vocab, 1));
    MemoryContextSwitchTo(old);
    /* CHUNKED so the bytea[] passed to SQL never approaches the 1GB array cap on a
     * large distinct-entity set: classify SEP_CHUNK ids per SPI call, freeing the
     * per-chunk byteas between batches; the query's ordinal is chunk-local, so the
     * chunk base maps it back to the global vocab index. */
    if (c->n_vocab > 0)
    {
        const int32   SEP_CHUNK = 2 * 1024 * 1024;
        Oid           argtypes[1] = { BYTEAARRAYOID };
        MemoryContext sep_cxt = AllocSetContextCreate(walk_cxt,
                                    "laplace corpus separator chunk",
                                    ALLOCSET_DEFAULT_SIZES);

        for (int32 base = 0; base < c->n_vocab; base += SEP_CHUNK)
        {
            int32      len = Min(SEP_CHUNK, c->n_vocab - base);
            Datum     *elems;
            ArrayType *arr;
            Datum      args[1];
            int        rc;
            MemoryContext old2 = MemoryContextSwitchTo(sep_cxt);

            elems = (Datum *) palloc(sizeof(Datum) * len);
            for (int32 i = 0; i < len; i++)
            {
                bytea *b = (bytea *) palloc(VARHDRSZ + 16);

                SET_VARSIZE(b, VARHDRSZ + 16);
                memcpy(VARDATA(b), c->ids[base + i], 16);
                elems[i] = PointerGetDatum(b);
            }
            arr = construct_array(elems, len, BYTEAOID, -1, false, TYPALIGN_INT);
            args[0] = PointerGetDatum(arr);
            MemoryContextSwitchTo(old2);

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

                if (ord >= 0 && ord < len)
                {
                    is_sep[base + ord] = 1;
                    c->separators++;
                }
            }
            SPI_freetuptable(SPI_tuptable);
            MemoryContextReset(sep_cxt);
        }
        MemoryContextDelete(sep_cxt);
    }

    /* 4) compact: WORD-only stream (separators dropped from the match stream so n-gram
     * order stays over words), but record sep_after[pos] = the witnessed separator that
     * followed each word, so generation replays the exact witnessed spacing omniglottally
     * (no detok rules). collapse empty sequences. suffix array built lazily. */
    c->stream     = (int32 *) MemoryContextAllocHuge(cxt, sizeof(int32) * (Size) (raw_len + 2));
    c->sep_after  = (int32 *) MemoryContextAllocHuge(cxt, sizeof(int32) * (Size) (raw_len + 2));
    {
        bool at_boundary = true;

        for (int64 i = 0; i < raw_len; i++)
        {
            int32 t = raw[i];

            if (t == GEN_SENTINEL)
            {
                if (!at_boundary)
                {
                    c->sep_after[c->stream_len] = -1;
                    c->stream[c->stream_len++] = GEN_SENTINEL;
                    at_boundary = true;
                }
                continue;
            }
            if (is_sep[t])
                continue;                       /* not a word: stays out of the match stream */
            /* the witnessed separator immediately following this word, if any */
            c->sep_after[c->stream_len] =
                (i + 1 < raw_len && raw[i + 1] != GEN_SENTINEL && is_sep[raw[i + 1]])
                    ? raw[i + 1] : -1;
            c->stream[c->stream_len++] = t;
            at_boundary = false;
        }
        if (!at_boundary)
        {
            c->sep_after[c->stream_len] = -1;
            c->stream[c->stream_len++] = GEN_SENTINEL;
        }
    }

    /* The suffix array is the GENERATION index (continuations binary-search), an
     * O(n·depth) sort over hundreds of millions of suffixes. The trajectory-pair
     * scans (cooccurrence_scan, the foundry's order ladder) need only c->stream, so
     * the index is built LAZILY on first generation call — a pour never pays for it. */
    c->suffix = NULL;
    c->n_suffix = 0;

    MemoryContextDelete(walk_cxt);
    gen_corpus = c;
}

/* Build the generation suffix index on demand (idempotent): only walk_continuations
 * needs it; cooccurrence/foundry scans skip it entirely. */
void
corpus_ensure_suffix(GenCorpus *c)
{
    if (c->suffix != NULL)
        return;
    c->suffix = (int32 *) MemoryContextAllocHuge(c->cxt, sizeof(int32) * (Size) Max(c->stream_len, 1));
    c->n_suffix = 0;
    for (int32 i = 0; i < c->stream_len; i++)
        if (c->stream[i] != GEN_SENTINEL)
            c->suffix[c->n_suffix++] = i;
    cmp_stream = c->stream;
    cmp_len    = c->stream_len;
    qsort(c->suffix, c->n_suffix, sizeof(int32), suffix_cmp);
}

GenCorpus *
corpus_ensure(void)
{
    int64 rows, max_us;

    corpus_probe(&rows, &max_us);
    if (rows == 0)
        ereport(ERROR, (errmsg(
            "trajectory_stream: no witnessed trajectories — deposit content first")));
    if (gen_corpus == NULL
        || gen_corpus->probe_rows != rows
        || gen_corpus->probe_max_us != max_us
        || gen_corpus->build_max_rows != laplace_corpus_max_rows)
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
    corpus_ensure_suffix(c);   /* the warm verb fully warms: positions = n_suffix */
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


Datum
pg_laplace_stream_reset(PG_FUNCTION_ARGS)
{
    gen_corpus_free();
    PG_RETURN_BOOL(true);
}
