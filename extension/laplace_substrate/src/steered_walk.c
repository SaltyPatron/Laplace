/*
 * steered_walk.c — the converse_walk WALK phase, natively.
 *
 * Replaces the plpgsql trigram->bigram backoff loop that re-scanned the whole
 * token stream with generate_subscripts per step (O(steps × n) plpgsql
 * evaluation, hex-string visited set). Here: intern tokens once, build
 * trigram/bigram postings once, then each step is a postings-list scan with a
 * hashed visited set. Pure computation over the arguments — no SPI.
 *
 * Arithmetic parity with the plpgsql it replaces (converse_walk):
 *   - LCG: rng = (rng * 1103515245 + 12345) % 2147483647, advanced once before
 *     the random seed pick and once per WALK step.
 *   - candidate score: weight[pos+k] * (|rng + pos*2654435761| % 100000) with
 *     pos the 1-BASED stream position; first maximum wins scanning positions
 *     ascending (matches the bounded top-1 sort keeping the incumbent on ties).
 *   - the trigram path excludes visited (a,b,c) value-triples; the bigram path
 *     excludes nothing (the plpgsql compared a pair-concat against triple
 *     concats — inert by construction; parity preserved deliberately).
 *   - a SENT successor past minlen ends the walk; before minlen it is marked
 *     visited and the step is consumed without advancing (a,b).
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

PG_FUNCTION_INFO_V1(pg_laplace_steered_walk);

/* Ids are 16-byte content hashes; the sentence sentinel is 1 byte. */
#define SW_KEY_MAX 19

typedef struct SwVocabKey
{
    uint8 len;
    uint8 bytes[SW_KEY_MAX];
} SwVocabKey;

typedef struct SwVocabEntry
{
    SwVocabKey key;
    int32      tok;
} SwVocabEntry;

typedef struct SwPairKey
{
    int32 a;
    int32 b;
} SwPairKey;

typedef struct SwPostings
{
    int32 *pos;   /* 1-based stream positions, ascending */
    int    n;
    int    cap;
} SwPostings;

typedef struct SwPairEntry
{
    SwPairKey  key;
    SwPostings postings;
} SwPairEntry;

typedef struct SwUniEntry
{
    int32      key;
    SwPostings postings;
} SwUniEntry;

typedef struct SwTriKey
{
    int32 a;
    int32 b;
    int32 c;
} SwTriKey;

typedef struct SwTriEntry
{
    SwTriKey key;
} SwTriEntry;

static void
sw_key_from_bytea(SwVocabKey *key, bytea *val)
{
    Size len = VARSIZE_ANY_EXHDR(val);

    if (len == 0 || len > SW_KEY_MAX)
        ereport(ERROR, (errmsg("steered_walk: token id length %zu outside 1..%d",
                               (size_t) len, SW_KEY_MAX)));
    memset(key, 0, sizeof(*key));
    key->len = (uint8) len;
    memcpy(key->bytes, VARDATA_ANY(val), len);
}

/* Intern a bytea into the vocab; remembers the first-seen datum per token so
 * the output array can be built from original values. */
static int32
sw_intern(HTAB *vocab, Datum **tok_datum, int32 *next_tok, int *datum_cap,
          Datum val)
{
    SwVocabKey    key;
    bool          found;
    SwVocabEntry *e;

    sw_key_from_bytea(&key, DatumGetByteaPP(val));
    e = (SwVocabEntry *) hash_search(vocab, &key, HASH_ENTER, &found);
    if (!found)
    {
        e->tok = (*next_tok)++;
        if (e->tok >= *datum_cap)
        {
            *datum_cap *= 2;
            *tok_datum = (Datum *) repalloc(*tok_datum, sizeof(Datum) * *datum_cap);
        }
        (*tok_datum)[e->tok] = val;
    }
    return e->tok;
}

static void
sw_postings_add(SwPostings *p, int32 pos1based)
{
    if (p->n == p->cap)
    {
        p->cap = p->cap == 0 ? 4 : p->cap * 2;
        p->pos = p->pos == NULL
            ? (int32 *) palloc(sizeof(int32) * p->cap)
            : (int32 *) repalloc(p->pos, sizeof(int32) * p->cap);
    }
    p->pos[p->n++] = pos1based;
}

static int64
sw_lcg(int64 rng)
{
    return (rng * 1103515245 + 12345) % INT64CONST(2147483647);
}

static int64
sw_score(int64 weight, int64 rng, int64 pos1based)
{
    int64 h = rng + pos1based * INT64CONST(2654435761);

    if (h < 0)
        h = -h;
    return weight * (h % 100000);
}

/*
 * steered_walk_raw(stream bytea[], weights int8[], core_seq bytea[],
 *                  starts bytea[], steps int, minlen int, rng int8)
 *   RETURNS bytea[]
 */
Datum
pg_laplace_steered_walk(PG_FUNCTION_ARGS)
{
    ArrayType *stream_arr  = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType *weights_arr = PG_GETARG_ARRAYTYPE_P(1);
    ArrayType *core_arr    = PG_GETARG_ARRAYTYPE_P(2);
    ArrayType *starts_arr  = PG_GETARG_ARRAYTYPE_P(3);
    int32      steps       = PG_GETARG_INT32(4);
    int32      minlen      = PG_GETARG_INT32(5);
    int64      rng         = PG_GETARG_INT64(6);

    Datum *stream_elems, *core_elems, *starts_elems, *weight_elems;
    bool  *nulls;
    int    n, core_n, starts_n, weights_n;

    HASHCTL     hctl;
    HTAB       *vocab, *tri, *bi, *visited;
    Datum      *tok_datum;
    int         datum_cap;
    int32       next_tok = 0;
    int32      *tokens;
    int64      *weights;
    int32       sent_tok;
    int32       a, b;
    bool        have_b = false;
    int32      *out;
    int         out_n = 0;
    int         emitted;

    deconstruct_array(stream_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &stream_elems, &nulls, &n);
    for (int i = 0; i < n; i++)
        if (nulls[i])
            ereport(ERROR, (errmsg("steered_walk: stream must not contain NULL")));
    deconstruct_array(weights_arr, INT8OID, 8, true, TYPALIGN_DOUBLE,
                      &weight_elems, &nulls, &weights_n);
    for (int i = 0; i < weights_n; i++)
        if (nulls[i])
            ereport(ERROR, (errmsg("steered_walk: weights must not contain NULL")));
    if (weights_n != n)
        ereport(ERROR, (errmsg("steered_walk: weights length %d != stream length %d",
                               weights_n, n)));
    deconstruct_array(core_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &core_elems, &nulls, &core_n);
    for (int i = 0; i < core_n; i++)
        if (nulls[i])
            ereport(ERROR, (errmsg("steered_walk: core_seq must not contain NULL")));
    deconstruct_array(starts_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &starts_elems, &nulls, &starts_n);
    for (int i = 0; i < starts_n; i++)
        if (nulls[i])
            ereport(ERROR, (errmsg("steered_walk: starts must not contain NULL")));

    if (n < 3)
        PG_RETURN_NULL();

    /* ---- intern the stream ---- */
    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(SwVocabKey);
    hctl.entrysize = sizeof(SwVocabEntry);
    vocab = hash_create("steered_walk vocab", 1024, &hctl,
                        HASH_ELEM | HASH_BLOBS);

    datum_cap = 1024;
    tok_datum = (Datum *) palloc(sizeof(Datum) * datum_cap);

    {
        /* The sentence sentinel: single 0x00 byte, interned unconditionally so
         * its token id exists even for streams that somehow lack it. */
        bytea *sent = (bytea *) palloc(VARHDRSZ + 1);

        SET_VARSIZE(sent, VARHDRSZ + 1);
        VARDATA(sent)[0] = 0;
        sent_tok = sw_intern(vocab, &tok_datum, &next_tok, &datum_cap,
                             PointerGetDatum(sent));
    }

    tokens  = (int32 *) palloc(sizeof(int32) * n);
    weights = (int64 *) palloc(sizeof(int64) * n);
    for (int i = 0; i < n; i++)
    {
        tokens[i]  = sw_intern(vocab, &tok_datum, &next_tok, &datum_cap,
                               stream_elems[i]);
        weights[i] = DatumGetInt64(weight_elems[i]);
    }

    /* ---- postings: trigram (a,b) -> positions, bigram b -> positions ---- */
    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(SwPairKey);
    hctl.entrysize = sizeof(SwPairEntry);
    tri = hash_create("steered_walk trigrams", 1024, &hctl,
                      HASH_ELEM | HASH_BLOBS);
    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(int32);
    hctl.entrysize = sizeof(SwUniEntry);
    bi = hash_create("steered_walk bigrams", 1024, &hctl,
                     HASH_ELEM | HASH_BLOBS);

    for (int i = 0; i < n - 1; i++)
    {
        bool        found;
        SwUniEntry *ue = (SwUniEntry *) hash_search(bi, &tokens[i],
                                                    HASH_ENTER, &found);

        if (!found)
            memset(&ue->postings, 0, sizeof(ue->postings));
        sw_postings_add(&ue->postings, i + 1);

        if (i < n - 2)
        {
            SwPairKey    pk = { tokens[i], tokens[i + 1] };
            SwPairEntry *pe = (SwPairEntry *) hash_search(tri, &pk,
                                                          HASH_ENTER, &found);

            if (!found)
                memset(&pe->postings, 0, sizeof(pe->postings));
            sw_postings_add(&pe->postings, i + 1);
        }
    }

    /* ---- SEED ---- */
    if (core_n >= 2)
    {
        a = sw_intern(vocab, &tok_datum, &next_tok, &datum_cap, core_elems[0]);
        b = sw_intern(vocab, &tok_datum, &next_tok, &datum_cap, core_elems[1]);
        have_b = true;
    }
    else
    {
        SwUniEntry *ue;
        int64       best = -1;
        int32       pick = -1;

        if (starts_n == 0)
            PG_RETURN_NULL();
        rng = sw_lcg(rng);
        a = sw_intern(vocab, &tok_datum, &next_tok, &datum_cap,
                      starts_elems[(rng < 0 ? -rng : rng) % starts_n]);
        ue = (SwUniEntry *) hash_search(bi, &a, HASH_FIND, NULL);
        if (ue != NULL)
        {
            for (int k = 0; k < ue->postings.n; k++)
            {
                int32 pos = ue->postings.pos[k];          /* 1-based */
                int32 c   = tokens[pos];                  /* stream[pos+1] */
                int64 s;

                if (c == sent_tok)
                    continue;
                s = sw_score(weights[pos], rng, pos);
                if (s > best)
                {
                    best = s;
                    pick = c;
                }
            }
        }
        if (pick >= 0)
        {
            b = pick;
            have_b = true;
        }
        else
            b = -1;
    }

    if (!have_b)
    {
        /* Single-token result: the caller renders it directly. */
        Datum d[1] = { tok_datum[a] };

        PG_RETURN_ARRAYTYPE_P(construct_array(d, 1, BYTEAOID, -1, false,
                                              TYPALIGN_INT));
    }

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(SwTriKey);
    hctl.entrysize = sizeof(SwTriEntry);
    visited = hash_create("steered_walk visited", 256, &hctl,
                          HASH_ELEM | HASH_BLOBS);

    out = (int32 *) palloc(sizeof(int32) * (2 + (steps > 0 ? steps : 0)));
    out[out_n++] = a;
    out[out_n++] = b;
    emitted = 2;

    /* ---- WALK: steered trigram->bigram backoff ---- */
    for (int step = 0; step < steps; step++)
    {
        int32 nxt = -1;
        int64 best = -1;

        rng = sw_lcg(rng);

        {
            SwPairKey    pk = { a, b };
            SwPairEntry *pe = (SwPairEntry *) hash_search(tri, &pk,
                                                          HASH_FIND, NULL);

            if (pe != NULL)
            {
                for (int k = 0; k < pe->postings.n; k++)
                {
                    int32    pos = pe->postings.pos[k];   /* 1-based */
                    int32    c   = tokens[pos + 1];       /* stream[pos+2] */
                    SwTriKey tk  = { a, b, c };
                    int64    s;

                    if (hash_search(visited, &tk, HASH_FIND, NULL) != NULL)
                        continue;
                    s = sw_score(weights[pos + 1], rng, pos);
                    if (s > best)
                    {
                        best = s;
                        nxt = c;
                    }
                }
            }
        }
        if (nxt < 0)
        {
            SwUniEntry *ue = (SwUniEntry *) hash_search(bi, &b,
                                                        HASH_FIND, NULL);

            best = -1;
            if (ue != NULL)
            {
                for (int k = 0; k < ue->postings.n; k++)
                {
                    int32 pos = ue->postings.pos[k];
                    int32 c   = tokens[pos];
                    int64 s   = sw_score(weights[pos], rng, pos);

                    if (s > best)
                    {
                        best = s;
                        nxt = c;
                    }
                }
            }
        }
        if (nxt < 0)
            break;

        if (nxt == sent_tok)
        {
            SwTriKey tk = { a, b, nxt };

            if (emitted >= minlen)
                break;
            hash_search(visited, &tk, HASH_ENTER, NULL);
            continue;
        }

        {
            SwTriKey tk = { a, b, nxt };

            hash_search(visited, &tk, HASH_ENTER, NULL);
        }
        out[out_n++] = nxt;
        emitted++;
        a = b;
        b = nxt;
    }

    {
        Datum *d = (Datum *) palloc(sizeof(Datum) * out_n);

        for (int i = 0; i < out_n; i++)
            d[i] = tok_datum[out[i]];
        PG_RETURN_ARRAYTYPE_P(construct_array(d, out_n, BYTEAOID, -1, false,
                                              TYPALIGN_INT));
    }
}
