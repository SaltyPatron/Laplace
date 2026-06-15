/*
 * foundry_crawl.c — the SEEDED VOCAB CRAWL (native heavy-lifter).
 *
 * Pick a few words ("Kung","Fu","Martial","Arts"); walk the consensus OUTWARD
 * from them along the strongest-affirmed edges to gather the content they
 * connect to — that connected neighborhood IS the token list (seed-focused,
 * self-defining, NOT a global top-N). This is the C/SPI heavy lifter the foundry
 * vocab is built on; SQL only orchestrates (resolve seed text -> id, render the
 * returned ids via render_text_batch, dedup by surface, limit).
 *
 * Engine: breadth-first over consensus. Per frontier node ONE index-driven probe
 * (consensus_subject_eff_mu_btree: (subject_id, (rating - 2*rd) DESC) WHERE
 * object_id IS NOT NULL) returns its top-`fanout` strongest neighbors directly —
 * no scan. Dedup is an HTAB (O(1)), not the O(n^2) linear scan tax_bfs_up uses,
 * so the crawl scales to a full vocab. Relevance = product of edge strengths
 * along the path (strength from eff_mu vs the Glicko neutral), so strongly-linked
 * seed-close words rank high and weak/distant ones decay — ranking by THIS keeps
 * the vocab on the seeds' topic instead of on global frequency.
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_foundry_crawl);

/* one discovered node: its best relevance + BFS depth + entity tier */
typedef struct {
    char    key[16];        /* hash128 entity id (HTAB key — must be first) */
    double  rel;
    int     depth;
    int16   tier;
} CrawlEntry;

/* a tier-2 word collected for the vocab, with its best path relevance */
typedef struct {
    hash128_t id;
    double    rel;
} WordCand;

static int
word_cmp_desc(const void *a, const void *b)
{
    double ra = ((const WordCand *) a)->rel;
    double rb = ((const WordCand *) b)->rel;
    if (ra < rb) return 1;
    if (ra > rb) return -1;
    return 0;
}

/* edge affirmation -> a (0.05 .. 1.0] multiplier; neutral maps to ~0.5 so depth
 * decays, strongly-affirmed edges (high rating, low rd) approach 1.0. */
static double
edge_strength(int64 rating, int64 rd)
{
    double eff  = (double) laplace_effective_mu_fp(rating, rd);
    double diff = (eff - (double) LAPLACE_GLICKO2_NEUTRAL_MU_FP) / 1.0e9; /* rating points */
    double s    = 0.5 + diff / 800.0;
    if (s < 0.05) s = 0.05;
    if (s > 1.0)  s = 1.0;
    return s;
}

Datum
pg_laplace_foundry_crawl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType  *seedarr;
    Datum      *seed_elems;
    bool       *seed_nulls;
    int         seed_n;
    int32       budget, hops, fanout;
    int         max_nodes, max_expand;
    bool        has_filter = false;
    Datum       type_arr_datum = (Datum) 0;

    HTAB       *seen;
    HASHCTL     ctl;
    hash128_t  *queue;
    int         qhead = 0, qtail = 0;
    int         expanded = 0;
    WordCand   *words;
    int         n_words = 0;

    SPIPlanPtr  plan;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("foundry_crawl: seed id array must not be NULL")));
    budget = PG_ARGISNULL(1) ? 32000 : PG_GETARG_INT32(1);
    hops   = PG_ARGISNULL(2) ? 3     : PG_GETARG_INT32(2);
    fanout = PG_ARGISNULL(3) ? 64    : PG_GETARG_INT32(3);
    if (budget < 1)   budget = 1;
    if (hops   < 0)   hops   = 0;
    if (fanout < 1)   fanout = 1;

    seedarr = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(seedarr) > 1)
        ereport(ERROR, (errmsg("foundry_crawl: seeds must be a 1-D bytea[]")));
    if (ARR_ELEMTYPE(seedarr) != BYTEAOID)
        ereport(ERROR, (errmsg("foundry_crawl: seeds must be bytea[]")));
    deconstruct_array(seedarr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &seed_elems, &seed_nulls, &seed_n);

    /* optional relation-type filter (arg 4): crawl only these edge types — the
     * SEMANTIC/definitional set, so the neighborhood is the seeds' meaning, not
     * their co-occurrence (PRECEDES would drag in every stop word). NULL/empty =
     * all types. */
    if (!PG_ARGISNULL(4))
    {
        ArrayType *tarr = PG_GETARG_ARRAYTYPE_P(4);
        if (ARR_NDIM(tarr) > 1 || ARR_ELEMTYPE(tarr) != BYTEAOID)
            ereport(ERROR, (errmsg("foundry_crawl: rel_types must be a 1-D bytea[]")));
        if (ArrayGetNItems(ARR_NDIM(tarr), ARR_DIMS(tarr)) > 0)
        {
            has_filter = true;
            type_arr_datum = PointerGetDatum(tarr);
        }
    }

    InitMaterializedSRF(fcinfo, 0);

    /* bound the walk so it runs in the seeds' time, not the whole graph's:
     * cap how many nodes we probe (query count) and how many we track. */
    max_expand = budget * 4;
    if (max_expand < 2000)   max_expand = 2000;
    if (max_expand > 80000)  max_expand = 80000;
    max_nodes = budget * 16;
    if (max_nodes < 8000)    max_nodes = 8000;
    if (max_nodes > 600000)  max_nodes = 600000;

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "foundry_crawl: SPI_connect failed");

    /* prepared once, executed per frontier node — index-driven top-`fanout`
     * strongest neighbors of $1, with each neighbor's tier joined in. With the
     * relation filter the (subject_id, type_id) index bounds it to the semantic
     * edges; without it, the (subject_id, eff_mu) index returns the strongest. */
    if (has_filter)
    {
        Oid pargs[3] = { BYTEAOID, INT4OID, BYTEAARRAYOID };
        plan = SPI_prepare(
            "SELECT c.object_id, e.tier, c.rating, c.rd "
            "FROM laplace.consensus c "
            "JOIN laplace.entities e ON e.id = c.object_id "
            "WHERE c.subject_id = $1 AND c.object_id IS NOT NULL "
            "  AND c.type_id = ANY ($3) "
            "ORDER BY (c.rating - 2 * c.rd) DESC "
            "LIMIT $2",
            3, pargs);
    }
    else
    {
        Oid pargs[2] = { BYTEAOID, INT4OID };
        plan = SPI_prepare(
            "SELECT c.object_id, e.tier, c.rating, c.rd "
            "FROM laplace.consensus c "
            "JOIN laplace.entities e ON e.id = c.object_id "
            "WHERE c.subject_id = $1 AND c.object_id IS NOT NULL "
            "ORDER BY (c.rating - 2 * c.rd) DESC "
            "LIMIT $2",
            2, pargs);
    }
    if (plan == NULL)
        elog(ERROR, "foundry_crawl: SPI_prepare failed");

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = 16;
    ctl.entrysize = sizeof(CrawlEntry);
    seen  = hash_create("foundry_crawl seen", max_nodes, &ctl, HASH_ELEM | HASH_BLOBS);
    queue = (hash128_t *) palloc(sizeof(hash128_t) * max_nodes);
    words = (WordCand *)  palloc(sizeof(WordCand)  * max_nodes);

    /* seed the frontier (relevance 1.0, depth 0). Seeds themselves are words. */
    for (int s = 0; s < seed_n; s++)
    {
        hash128_t  h;
        CrawlEntry *e;
        bool        found;

        if (seed_nulls[s])
            continue;
        h = datum_to_hash128(seed_elems[s]);
        e = (CrawlEntry *) hash_search(seen, &h, HASH_ENTER, &found);
        if (found)
            continue;
        e->rel   = 1.0;
        e->depth = 0;
        e->tier  = 2;                      /* a seed word is a word */
        words[n_words].id  = h;            /* keep the seeds in the vocab */
        words[n_words].rel = 1.0;
        n_words++;
        if (qtail < max_nodes)
            queue[qtail++] = h;
    }

    while (qhead < qtail && expanded < max_expand)
    {
        hash128_t   cur = queue[qhead++];
        CrawlEntry *u;
        bool        found;
        Datum       args[3];
        int         rc;
        double      u_rel;
        int         u_depth;

        u = (CrawlEntry *) hash_search(seen, &cur, HASH_FIND, &found);
        if (!found)
            continue;
        u_rel   = u->rel;
        u_depth = u->depth;
        if (u_depth >= hops)
            continue;

        expanded++;
        args[0] = hash128_to_datum(&cur);
        args[1] = Int32GetDatum(fanout);
        if (has_filter)
            args[2] = type_arr_datum;
        rc = SPI_execute_plan(plan, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "foundry_crawl: neighbor probe failed: %s",
                 SPI_result_code_string(rc));

        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple   tup = SPI_tuptable->vals[r];
            TupleDesc   td  = SPI_tuptable->tupdesc;
            bool        isnull;
            hash128_t   oh;
            int16       otier;
            int64       rating, rd;
            double      child_rel;
            CrawlEntry *oe;
            bool        ofound;

            oh = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
            if (isnull)
                continue;
            otier  = DatumGetInt16(SPI_getbinval(tup, td, 2, &isnull));
            rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
            rd     = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));

            child_rel = u_rel * edge_strength(rating, rd);

            oe = (CrawlEntry *) hash_search(seen, &oh, HASH_ENTER, &ofound);
            if (!ofound)
            {
                oe->rel   = child_rel;
                oe->depth = u_depth + 1;
                oe->tier  = otier;
                if (otier == 2 && n_words < max_nodes)
                {
                    words[n_words].id  = oh;
                    words[n_words].rel = child_rel;
                    n_words++;
                }
                if (oe->depth < hops && qtail < max_nodes)
                    queue[qtail++] = oh;
            }
            else if (child_rel > oe->rel)
            {
                /* a stronger path reached an already-seen node: keep the better
                 * relevance (the word's vocab rank tracks its best path). */
                oe->rel = child_rel;
            }
        }
    }

    /* rank the collected words by relevance and emit the top `budget`. The thin
     * SQL wrapper renders surfaces (render_text_batch) and dedups by surface, so
     * we hand back a little headroom above budget for surface collisions. */
    if (n_words > 1)
        qsort(words, n_words, sizeof(WordCand), word_cmp_desc);

    {
        int emit_cap = budget + budget / 4 + 16;   /* headroom for surface dedup */
        int emitted  = 0;

        for (int i = 0; i < n_words && emitted < emit_cap; i++)
        {
            Datum values[2];
            bool  nulls[2] = { false, false };
            int64 w = (int64) (words[i].rel * 1.0e6);

            if (w < 1) w = 1;
            values[0] = hash128_to_datum(&words[i].id);
            values[1] = Int64GetDatum(w);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            emitted++;
        }
    }

    SPI_freeplan(plan);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
