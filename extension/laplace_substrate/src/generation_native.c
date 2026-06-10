/*
 * generation_native.c — native autoregressive generation over the content index.
 *
 * The plpgsql path issued a dynamic k-way self-join per emitted token (RBAR at
 * the step level). Here the witnessed token stream is loaded ONCE per backend
 * via SPI into a suffix array; longest-context back-off is then binary search
 * over that array and every generation step is pure in-memory work. Sampling is
 * seeded (splitmix64), so a (corpus, prompt, seed) triple reproduces its output
 * exactly — generation is testimony-deterministic, not vibes.
 *
 * Cache: TopMemoryContext child, invalidated when count(*) of content_index
 * changes (rebuild procedures replace the table wholesale).
 */
#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

PG_FUNCTION_INFO_V1(pg_laplace_generate_tokens);
PG_FUNCTION_INFO_V1(pg_laplace_generation_cache_reset);

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
    HTAB    *vocab;         /* 16-byte entity id -> vocab id */
    int64    source_rows;   /* invalidation probe */
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

/* ── corpus build (one SPI scan) ───────────────────────────────────────────── */

static const char *CORPUS_QUERY =
    "SELECT seq_id, token FROM laplace.content_index ORDER BY seq_id, pos";
static const char *CORPUS_PROBE =
    "SELECT count(*) FROM laplace.content_index";

static int64
corpus_probe_rows(void)
{
    bool isnull;

    if (SPI_execute(CORPUS_PROBE, true, 1) != SPI_OK_SELECT || SPI_processed == 0)
        elog(ERROR, "generate_tokens: content_index probe failed (run rebuild_content_index first)");
    return DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                       SPI_tuptable->tupdesc, 1, &isnull));
}

static void
corpus_build(int64 rows)
{
    MemoryContext cxt;
    MemoryContext old;
    GenCorpus *c;
    HASHCTL    ctl;
    int        rc;
    char       prev_seq[16];
    bool       have_prev = false;
    int32      vocab_cap = 4096;

    gen_corpus_free();

    cxt = AllocSetContextCreate(TopMemoryContext, "laplace generation corpus",
                                ALLOCSET_DEFAULT_SIZES);
    old = MemoryContextSwitchTo(cxt);

    c = (GenCorpus *) palloc0(sizeof(GenCorpus));
    c->cxt = cxt;
    c->source_rows = rows;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = 16;
    ctl.entrysize = sizeof(VocabEntry);
    ctl.hcxt      = cxt;
    c->vocab = hash_create("generation vocab", 8192, &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    c->ids = (char (*)[16]) palloc(sizeof(char[16]) * vocab_cap);

    /* stream: rows tokens + at most rows sentinels */
    c->stream = (int32 *) palloc(sizeof(int32) * (rows * 2 + 2));
    c->suffix = (int32 *) palloc(sizeof(int32) * (rows + 1));

    MemoryContextSwitchTo(old);

    rc = SPI_execute(CORPUS_QUERY, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "generate_tokens: corpus scan failed: %s",
             SPI_result_code_string(rc));

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      isnull;
        bytea    *seq = DatumGetByteaPP(SPI_getbinval(tup, td, 1, &isnull));
        bytea    *tok = DatumGetByteaPP(SPI_getbinval(tup, td, 2, &isnull));
        char      key[16];
        bool      found;
        VocabEntry *ve;

        if (VARSIZE_ANY_EXHDR(seq) != 16 || VARSIZE_ANY_EXHDR(tok) != 16)
            ereport(ERROR, (errmsg("generate_tokens: content_index ids must be 16 bytes")));

        if (have_prev && memcmp(prev_seq, VARDATA_ANY(seq), 16) != 0)
            c->stream[c->stream_len++] = GEN_SENTINEL;
        memcpy(prev_seq, VARDATA_ANY(seq), 16);
        have_prev = true;

        memcpy(key, VARDATA_ANY(tok), 16);
        ve = (VocabEntry *) hash_search(c->vocab, key, HASH_ENTER, &found);
        if (!found)
        {
            if (c->n_vocab == vocab_cap)
            {
                old = MemoryContextSwitchTo(cxt);
                vocab_cap *= 2;
                c->ids = (char (*)[16]) repalloc(c->ids, sizeof(char[16]) * vocab_cap);
                MemoryContextSwitchTo(old);
            }
            ve->id = c->n_vocab;
            memcpy(c->ids[c->n_vocab], key, 16);
            c->n_vocab++;
        }

        c->suffix[c->n_suffix++] = c->stream_len;
        c->stream[c->stream_len++] = ve->id;
    }
    c->stream[c->stream_len++] = GEN_SENTINEL;

    cmp_stream = c->stream;
    cmp_len    = c->stream_len;
    qsort(c->suffix, c->n_suffix, sizeof(int32), suffix_cmp);

    gen_corpus = c;
}

static GenCorpus *
corpus_ensure(void)
{
    int64 rows = corpus_probe_rows();

    if (rows == 0)
        ereport(ERROR, (errmsg(
            "generate_tokens: content_index is empty — CALL rebuild_content_index_deep() first")));
    if (gen_corpus == NULL || gen_corpus->source_rows != rows)
        corpus_build(rows);
    return gen_corpus;
}

/* ── seeded sampling (splitmix64) ──────────────────────────────────────────── */

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
pg_laplace_generate_tokens(PG_FUNCTION_ARGS)
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
        ereport(ERROR, (errmsg("generate_tokens: context must not be NULL")));
    ctx_arr   = PG_GETARG_ARRAYTYPE_P(0);
    steps     = PG_ARGISNULL(1) ? 24  : PG_GETARG_INT32(1);
    max_order = PG_ARGISNULL(2) ? 5   : PG_GETARG_INT32(2);
    temp      = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    topk      = PG_ARGISNULL(4) ? 10  : PG_GETARG_INT32(4);
    rng       = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D)
                                : (uint64) PG_GETARG_INT64(5);

    if (steps < 1 || steps > GEN_MAX_STEPS)
        ereport(ERROR, (errmsg("generate_tokens: steps must be in [1,%d]", GEN_MAX_STEPS)));
    if (max_order < 1 || max_order > GEN_MAX_ORDER)
        ereport(ERROR, (errmsg("generate_tokens: max_order must be in [1,%d]", GEN_MAX_ORDER)));
    if (topk < 1 || topk > 256)
        ereport(ERROR, (errmsg("generate_tokens: topk must be in [1,256]")));
    if (ARR_NDIM(ctx_arr) != 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("generate_tokens: context must be a 1-D bytea array")));

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "generate_tokens: SPI_connect failed");
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
        if (n_cand == 0)
            break;

        /* heaviest topk, then Gumbel/temperature among them */
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
pg_laplace_generation_cache_reset(PG_FUNCTION_ARGS)
{
    gen_corpus_free();
    PG_RETURN_BOOL(true);
}
