




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

#include "trajectory_corpus.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);

PG_FUNCTION_INFO_V1(pg_laplace_cooccurrence_scan);

typedef struct StreamPairKey
{
    int32 subject;
    int32 object;
    int32 gap;
} StreamPairKey;

typedef struct StreamPairEntry
{
    StreamPairKey key;             
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
    



    bool            *in_vocab = NULL;

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

    
    if (PG_NARGS() >= 2 && !PG_ARGISNULL(1))
    {
        ArrayType *va = PG_GETARG_ARRAYTYPE_P(1);
        Datum     *elems;
        bool      *elnulls;
        int        nelems;

        in_vocab = (bool *) palloc0(sizeof(bool) * (c->n_vocab > 0 ? c->n_vocab : 1));
        deconstruct_array(va, BYTEAOID, -1, false, 'i', &elems, &elnulls, &nelems);
        for (int k = 0; k < nelems; k++)
        {
            bytea      *b;
            VocabEntry *ve;

            if (elnulls[k]) continue;
            b = DatumGetByteaPP(elems[k]);
            if (VARSIZE_ANY_EXHDR(b) != 16) continue;
            ve = (VocabEntry *) hash_search(c->vocab, VARDATA_ANY(b), HASH_FIND, NULL);
            if (ve != NULL && ve->id < c->n_vocab)
                in_vocab[ve->id] = true;
        }
    }

    for (int32 i = 0; i < c->stream_len; i++)
    {
        int32 tok = c->stream[i];

        if (tok == GEN_SENTINEL)
        {
            win_len = 0;
            continue;
        }

        

        if (in_vocab == NULL || in_vocab[tok])
        for (int d = 1; d <= win_len; d++)
        {
            StreamPairKey    key;
            StreamPairEntry *ent;
            bool             found;
            int32            subj = win[win_len - d];

            if (in_vocab != NULL && !in_vocab[subj])
                continue;

            memset(&key, 0, sizeof(key));
            key.subject = subj;
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



typedef struct Continuation
{
    int32 token;
    int64 weight;
    int32 sep;      
} Continuation;


static int
continuations_collect(const GenCorpus *c, const int32 *ctx, int k,
                      Continuation *out, int out_cap)
{
    int32 lo = 0, hi = c->n_suffix, first, last;
    int   n = 0;

    while (lo < hi)                     
    {
        int32 mid = lo + (hi - lo) / 2;
        if (prefix_cmp(c, c->suffix[mid], ctx, k) < 0) lo = mid + 1;
        else hi = mid;
    }
    first = lo;
    hi = c->n_suffix;
    while (lo < hi)                     
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
            
            int32 sep = c->sep_after ? c->sep_after[next_pos] : -1;
            if (n == out_cap)
            {
                
                int lightest = 0;
                for (int m = 1; m < n; m++)
                    if (out[m].weight < out[lightest].weight) lightest = m;
                if (out[lightest].weight > 1)
                    continue;
                out[lightest].token = tok;
                out[lightest].weight = 1;
                out[lightest].sep = sep;
                continue;
            }
            out[n].token = tok;
            out[n].weight = 1;
            out[n].sep = sep;
            n++;
        }
    }
    return n;
}



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
    corpus_ensure_suffix(c);   




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
        int32 pick_sep = -1;

        for (int k = (ctx_len < max_order ? ctx_len : max_order); k >= 1; k--)
        {
            n_cand = continuations_collect(c, ctx + ctx_len - k, k, cand, 256);
            if (n_cand > 0) { used = k; break; }
        }
        if (n_cand == 0 && ctx_len > 0)
        {
            



            Oid    argtypes[2] = { BYTEAOID, INT4OID };
            Datum  args[2];
            bytea *subj = (bytea *) palloc(VARHDRSZ + 16);
            int    rc;

            SET_VARSIZE(subj, VARHDRSZ + 16);
            memcpy(VARDATA(subj), c->ids[ctx[ctx_len - 1]], 16);
            args[0] = PointerGetDatum(subj);
            args[1] = Int32GetDatum(topk);
            rc = SPI_execute_with_args(
                "SELECT object_id, weight "
                "FROM laplace.walk_completes_floor($1, $2)",
                2, argtypes, args, NULL, true, 0);
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
                cand[n_cand].sep = -1;   
                n_cand++;
            }
            used = 0;
        }
        if (n_cand == 0)
            break;

        
        for (int i = 0; i < n_cand; i++)        
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
                if (i == 0 || key < best_key)
                    { best_key = key; pick = cand[i].token; pick_sep = cand[i].sep; }
            }
        }

        {
            bytea *out_tok = (bytea *) palloc(VARHDRSZ + 16);
            Datum  values[4];
            bool   rnulls[4] = { false, false, false, false };

            SET_VARSIZE(out_tok, VARHDRSZ + 16);
            memcpy(VARDATA(out_tok), c->ids[pick], 16);
            values[0] = Int32GetDatum(step);
            values[1] = PointerGetDatum(out_tok);
            values[2] = Int32GetDatum(used);
            

            if (pick_sep >= 0)
            {
                bytea *out_sep = (bytea *) palloc(VARHDRSZ + 16);
                SET_VARSIZE(out_sep, VARHDRSZ + 16);
                memcpy(VARDATA(out_sep), c->ids[pick_sep], 16);
                values[3] = PointerGetDatum(out_sep);
            }
            else
                rnulls[3] = true;
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
        }

        ctx[ctx_len++] = pick;
    }

    SPI_finish();
    return (Datum) 0;
}
