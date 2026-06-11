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
#include "fmgr.h"
#include "funcapi.h"
#include "utils/fmgrprotos.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "lib/stringinfo.h"
#include "common/pg_prng.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_generate_tokens);
PG_FUNCTION_INFO_V1(pg_laplace_generation_cache_reset);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_peer);
PG_FUNCTION_INFO_V1(pg_laplace_variant_walk);
PG_FUNCTION_INFO_V1(pg_laplace_generate_variant);

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
pg_laplace_generate_variant(PG_FUNCTION_ARGS)
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
        elog(ERROR, "generate_variant: SPI_connect failed");

    rc = SPI_execute_with_args(seed_sql, 2, argtypes, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "generate_variant: seed lookup failed: %s", SPI_result_code_string(rc));
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
