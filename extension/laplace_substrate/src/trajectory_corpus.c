





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






int laplace_corpus_max_rows = 0;
int laplace_corpus_max_orphan_sentences = 0;
static char *laplace_corpus_document_source = "UserPrompt";

void
laplace_corpus_guc_init(void)
{
    DefineCustomIntVariable(
        "laplace_substrate.corpus_max_rows",
        "Deprecated alias for corpus_max_orphan_sentences (0 = use corpus_max_orphan_sentences).",
        NULL, &laplace_corpus_max_rows, 0, 0, INT_MAX,
        PGC_SUSET, 0, NULL, NULL, NULL);
    DefineCustomIntVariable(
        "laplace_substrate.corpus_max_orphan_sentences",
        "Cap tier-3 orphan sentences in the generation corpus (tier-4 documents and their sentence constituents are always included; 0 = book-only corpus).",
        NULL, &laplace_corpus_max_orphan_sentences, 0, 0, INT_MAX,
        PGC_SUSET, 0, NULL, NULL, NULL);
    DefineCustomStringVariable(
        "laplace_substrate.corpus_document_source",
        "Decomposer source name for tier-4 document roots in the generation corpus (e.g. UserPrompt).",
        NULL, &laplace_corpus_document_source, "UserPrompt",
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






static const char *CORPUS_EDGE_QUERY =
    "SELECT p.entity_id, u.entity_id, GREATEST(u.run_length, 1)::int "
    "FROM (SELECT DISTINCT ON (entity_id) entity_id, trajectory "
    "      FROM laplace.physicalities "
    "      WHERE type = 1 AND trajectory IS NOT NULL "
    "      ORDER BY entity_id) p "
    "JOIN laplace.entities e ON e.id = p.entity_id AND e.tier > 2, "
    "LATERAL public.ST_DumpPoints(p.trajectory) dp, "
    "LATERAL public.laplace_mantissa_unpack(dp.geom) u "
    "ORDER BY p.entity_id, u.ordinal";

static const char *CORPUS_PROBE =
    "SELECT count(*)::int8, "
    "       COALESCE((extract(epoch FROM max(observed_at)) * 1000000)::int8, 0) "
    "FROM laplace.physicalities WHERE type = 1 AND trajectory IS NOT NULL";





static const char *CORPUS_SEPARATOR_QUERY =
    "SELECT v.ord::int4 - 1 "
    "FROM unnest($1::bytea[]) WITH ORDINALITY v(id, ord), "
    "LATERAL (SELECT laplace.render_text_fast(v.id, 8) AS s) r "
    "WHERE laplace.is_all_whitespace(r.s)";

#define CORPUS_WALK_DEPTH_CAP 256

typedef struct CorpusNode
{
    char  id[16];                  
    int32 idx;                     
} CorpusNode;

typedef struct NodeMeta
{
    int32 first_edge;              
    int32 n_edges;
    bool  has_parent;
} NodeMeta;

typedef struct CorpusEdge
{
    int32 child;                   
    int32 run;
} CorpusEdge;

typedef struct WalkFrame
{
    int32 node;                    
    int32 edge_i;                  
    int32 rep_left;                
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
            

            c->ids = (char (*)[16]) repalloc_huge(c->ids, sizeof(char[16]) * (Size) c->vocab_cap);
            MemoryContextSwitchTo(old);
        }
        ve->id = c->n_vocab;
        memcpy(c->ids[c->n_vocab], key, 16);
        c->n_vocab++;
    }
    return ve->id;
}

static int
corpus_orphan_cap(void)
{
    if (laplace_corpus_max_rows > 0)
        return laplace_corpus_max_rows;
    return laplace_corpus_max_orphan_sentences;
}

static void
corpus_build(int64 probe_rows, int64 probe_max_us)
{
    MemoryContext cxt, walk_cxt, old;
    GenCorpus *c;
    HASHCTL    ctl;
    int32     *raw = NULL;
    int64      raw_len = 0, raw_cap = 65536;
    uint8     *is_sep;
    Portal     portal;

    gen_corpus_free();

    cxt = AllocSetContextCreate(TopMemoryContext, "laplace generation corpus",
                                ALLOCSET_DEFAULT_SIZES);
    
    walk_cxt = AllocSetContextCreate(CurrentMemoryContext,
                                     "laplace corpus walk scratch",
                                     ALLOCSET_DEFAULT_SIZES);

    old = MemoryContextSwitchTo(cxt);
    c = (GenCorpus *) palloc0(sizeof(GenCorpus));
    c->cxt = cxt;
    c->probe_rows = probe_rows;
    c->probe_max_us = probe_max_us;
    c->build_max_rows = laplace_corpus_max_rows;
    c->build_max_orphans = corpus_orphan_cap();
    strlcpy(c->document_source, laplace_corpus_document_source,
            sizeof(c->document_source));
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
    raw   = (int32 *) palloc(sizeof(int32) * raw_cap);
    MemoryContextSwitchTo(old);

    {
        StringInfoData q;
        char           prev_parent[16];
        bool           have_parent = false;

        initStringInfo(&q);
        appendStringInfoString(&q,
            "WITH doc AS ("
            "  SELECT e.id FROM laplace.entities e "
            "  JOIN laplace.physicalities p "
            "    ON p.entity_id = e.id AND p.type = 1 AND p.trajectory IS NOT NULL "
            "  WHERE e.tier = 4");
        if (c->document_source[0] != '\0')
            appendStringInfo(&q,
                " AND p.source_id = laplace.source_id('%s')",
                c->document_source);
        appendStringInfoString(&q,
            "), book_sent AS ("
            "  SELECT DISTINCT tc.entity_id AS id "
            "  FROM doc d "
            "  JOIN laplace.physicalities pd "
            "    ON pd.entity_id = d.id AND pd.type = 1 AND pd.trajectory IS NOT NULL "
            "  CROSS JOIN LATERAL public.laplace_trajectory_constituents(pd.trajectory) tc"
            ")");
        if (c->build_max_orphans > 0)
        {
            appendStringInfoString(&q,
                ", orphan_sent AS ("
                "  SELECT e.id "
                "  FROM laplace.entities e "
                "  JOIN laplace.physicalities ps "
                "    ON ps.entity_id = e.id AND ps.type = 1 AND ps.trajectory IS NOT NULL "
                "  WHERE e.tier = 3 "
                "    AND NOT EXISTS ("
                "      SELECT 1 FROM book_sent bs WHERE bs.id = e.id"
                "    ) "
                "  ORDER BY ps.observed_at DESC");
            appendStringInfo(&q, " LIMIT %d", c->build_max_orphans);
            appendStringInfoString(&q, ")");
        }
        appendStringInfoString(&q,
            ", s AS ("
            "  SELECT id FROM book_sent");
        if (c->build_max_orphans > 0)
            appendStringInfoString(&q, " UNION SELECT id FROM orphan_sent");
        appendStringInfoString(&q,
            ") "
            "SELECT pp.entity_id, u.entity_id, GREATEST(u.run_length, 1)::int "
            "FROM (SELECT DISTINCT ON (pp.entity_id) pp.entity_id, pp.trajectory "
            "      FROM laplace.physicalities pp JOIN s ON s.id = pp.entity_id "
            "      WHERE pp.type = 1 AND pp.trajectory IS NOT NULL "
            "      ORDER BY pp.entity_id) pp "
            "CROSS JOIN LATERAL public.laplace_trajectory_constituents(pp.trajectory) u "
            /* Include words (tier 2), grapheme-segmented content (tier 1, e.g. CJK), AND the
               whitespace separators between them. Whitespace is classified by is_all_whitespace
               and folded into sep_after (skipped from the stream, emitted as the observed
               separator), so generation is language-agnostic and renders real separators.
               tier=2-only was English-word sabotage: it dropped every separator and every
               non-word-segmented language from the generation corpus. */
            "WHERE public.laplace_vertex_tier(u.flags) <= 2 "
            "ORDER BY pp.entity_id, u.ordinal");
        portal = SPI_cursor_open_with_args(
            "corpus_sentences", q.data, 0, NULL, NULL, NULL, true, 0);
        pfree(q.data);

        memset(prev_parent, 0, sizeof(prev_parent));
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
                int32     vid;

                if (VARSIZE_ANY_EXHDR(pb) != 16 || VARSIZE_ANY_EXHDR(cb) != 16)
                    ereport(ERROR, (errmsg(
                        "trajectory_stream: sentence ids must be 16 bytes")));

                if (have_parent && memcmp(VARDATA_ANY(pb), prev_parent, 16) != 0)
                {
                    if (raw_len + 2 > raw_cap)
                    {
                        old = MemoryContextSwitchTo(walk_cxt);
                        raw_cap *= 2;
                        raw = (int32 *) repalloc_huge(raw, sizeof(int32) * (Size) raw_cap);
                        MemoryContextSwitchTo(old);
                    }
                    raw[raw_len++] = GEN_SENTINEL;
                    c->sequences++;
                }
                memcpy(prev_parent, VARDATA_ANY(pb), 16);
                have_parent = true;

                memcpy(key, VARDATA_ANY(cb), 16);
                vid = corpus_vocab_intern(c, key);
                for (int32 k = 0; k < run; k++)
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
                CHECK_FOR_INTERRUPTS();
            }
            SPI_freetuptable(SPI_tuptable);
        }
        SPI_cursor_close(portal);
        if (have_parent)
        {
            if (raw_len + 2 > raw_cap)
            {
                old = MemoryContextSwitchTo(walk_cxt);
                raw_cap *= 2;
                raw = (int32 *) repalloc_huge(raw, sizeof(int32) * (Size) raw_cap);
                MemoryContextSwitchTo(old);
            }
            raw[raw_len++] = GEN_SENTINEL;
            c->sequences++;
        }
    }

    old = MemoryContextSwitchTo(walk_cxt);
    is_sep = (uint8 *) palloc0((Size) Max(c->n_vocab, 1));
    MemoryContextSwitchTo(old);
    



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
                continue;                       
            
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

    



    c->suffix = NULL;
    c->n_suffix = 0;

    MemoryContextDelete(walk_cxt);
    gen_corpus = c;
}



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
        || gen_corpus->build_max_rows != laplace_corpus_max_rows
        || gen_corpus->build_max_orphans != corpus_orphan_cap()
        || strcmp(gen_corpus->document_source, laplace_corpus_document_source) != 0)
        corpus_build(rows, max_us);
    return gen_corpus;
}



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
    corpus_ensure_suffix(c);   
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




Datum
pg_laplace_stream_reset(PG_FUNCTION_ARGS)
{
    gen_corpus_free();
    PG_RETURN_BOOL(true);
}
