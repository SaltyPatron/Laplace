/*
 * realize_batch.c — realize() for N ids in a fixed number of batched SPI
 * round-trips (6), positionally aligned to the input array.
 *
 * The scalar ladder it reproduces exactly (realize.sql.in, lang-only path —
 * resolve_name is called WITHOUT context, so only its null-context branch
 * matters):
 *
 *   COALESCE(
 *     _realize_has_name(id, lang),      -- arm 1: first NON-EMPTY render
 *     _realize_synset_lemma(id, lang),  -- arm 2: first NON-EMPTY render
 *     NULLIF(render_text(id, 24), ''),  -- arm 3: self render
 *     _realize_translation(id, lang),   -- arm 4: first NON-EMPTY render
 *     _realize_canonical(id),           -- arm 5: first row, text AS-IS
 *     _realize_defines(id))             -- arm 6: TOP-mu row, render AS-IS
 *
 * Parity notes (deliberate, match the scalar helpers byte-for-byte):
 *   - arms 1/2/4 filter candidates to non-empty renders BEFORE their LIMIT 1,
 *     so the batch walks each id's rank-ordered candidates and takes the first
 *     whose render is non-empty;
 *   - arms 5/6 have NO non-empty filter in the scalar: arm 5 returns the
 *     regexp-stripped canonical name as-is, arm 6 returns the render of the
 *     single top-mu definition as-is (possibly NULL → overall NULL, possibly
 *     '' → '' is the final answer);
 *   - arm 2 joins plain consensus for the HAS_SENSE hop (only the IS_SENSE_OF
 *     edge goes through the unrefuted view), exactly as _realize_synset_lemma;
 *   - a NULL lang makes every lp flag false (LEFT JOIN on object_id = NULL
 *     never matches), identical to the scalar helpers;
 *   - abstention: unresolvable ids yield SQL NULL, never hex.
 *
 * All candidate rendering funnels through ONE render_text_batch($ids, 24) call
 * (generate_walk.c) — one shared constituent closure + memo across every
 * candidate of every arm plus the inputs themselves. Depth 24 matches every
 * render_text(x, 24) in the scalar ladder.
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_realize_batch);

/* ---- prepared plans, one per arm, kept across calls ---- */

static SPIPlanPtr plan_has_name = NULL;
static SPIPlanPtr plan_synset_lemma = NULL;
static SPIPlanPtr plan_translation = NULL;
static SPIPlanPtr plan_canonical = NULL;
static SPIPlanPtr plan_defines = NULL;
static SPIPlanPtr plan_render = NULL;

static const char *Q_HAS_NAME =
    "SELECT nm.subject_id, nm.object_id,"
    "       (lang.object_id IS NOT NULL) AS lp,"
    "       (nm.type_id = laplace.relation_type_id('HAS_NAME')) AS prim,"
    "       laplace.eff_mu(nm.rating, nm.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted nm"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = nm.object_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE nm.subject_id = ANY($1)"
    "   AND nm.type_id IN (laplace.relation_type_id('HAS_NAME'),"
    "                      laplace.relation_type_id('HAS_NAME_ALIAS'))"
    " ORDER BY nm.subject_id, lp DESC, prim DESC, mu DESC";

static const char *Q_SYNSET_LEMMA =
    "SELECT io.object_id, hs.subject_id,"
    "       (lang.object_id IS NOT NULL) AS lp,"
    "       laplace.eff_mu(hs.rating, hs.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted io"
    " JOIN laplace.consensus hs ON hs.object_id = io.subject_id"
    "   AND hs.type_id = laplace.relation_type_id('HAS_SENSE')"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = hs.subject_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE io.object_id = ANY($1)"
    "   AND io.type_id = laplace.relation_type_id('IS_SENSE_OF')"
    " ORDER BY io.object_id, lp DESC, mu DESC";

static const char *Q_TRANSLATION =
    "SELECT m.subject_id, m.object_id,"
    "       (lang.object_id IS NOT NULL) AS lp,"
    "       laplace.eff_mu(m.rating, m.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted m"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = m.object_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE m.subject_id = ANY($1)"
    "   AND m.type_id = laplace.relation_type_id('IS_TRANSLATION_OF')"
    " ORDER BY m.subject_id, lp DESC, mu DESC";

static const char *Q_CANONICAL =
    "SELECT n.id, regexp_replace(n.name, '^substrate/[a-z_]+/(.+)/v1$', '\\1')"
    " FROM laplace.canonical_names n"
    " WHERE n.id = ANY($1) AND n.name LIKE 'substrate/%'"
    " ORDER BY n.id";

static const char *Q_DEFINES =
    "SELECT g.subject_id, g.object_id,"
    "       laplace.eff_mu(g.rating, g.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted g"
    " WHERE g.subject_id = ANY($1)"
    "   AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')"
    " ORDER BY g.subject_id, mu DESC";

static const char *Q_RENDER =
    "SELECT laplace.render_text_batch($1, 24)";

static SPIPlanPtr
ensure_plan(SPIPlanPtr *slot, const char *sql, int nargs, const Oid *argtypes)
{
    if (*slot == NULL)
    {
        SPIPlanPtr plan = SPI_prepare(sql, nargs, (Oid *) argtypes);

        if (plan == NULL)
            elog(ERROR, "realize_batch: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "realize_batch: SPI_keepplan failed");
        *slot = plan;
    }
    return *slot;
}

/* ---- per-call hash entries ---- */

typedef struct IdKey
{
    char bytes[16];
} IdKey;

/* Contiguous candidate run for one input id within one arm's row stream. */
typedef struct ArmEntry
{
    IdKey key;
    int32 start;
    int32 count;
} ArmEntry;

/* One arm's decoded result: candidates in rank order, grouped per input id. */
typedef struct ArmData
{
    HTAB  *by_id;               /* IdKey -> ArmEntry */
    Datum *cands;               /* candidate bytea datums, arrival order */
    int32  n;
    int32  cap;
} ArmData;

/* Union of every id that needs rendering: id -> slot in the render array. */
typedef struct RenderEntry
{
    IdKey key;
    int32 slot;
} RenderEntry;

/* Canonical-name arm: id -> stripped name text (first row per id). */
typedef struct CanonEntry
{
    IdKey key;
    Datum text;                 /* text datum in SPI proc context */
} CanonEntry;

static void
id_key(IdKey *key, Datum bytea_datum, const char *what)
{
    bytea *b = DatumGetByteaPP(bytea_datum);

    if (VARSIZE_ANY_EXHDR(b) != 16)
        ereport(ERROR,
                (errmsg("realize_batch: %s id must be 16 bytes (got %zu)",
                        what, (size_t) VARSIZE_ANY_EXHDR(b))));
    memcpy(key->bytes, VARDATA_ANY(b), 16);
}

static HTAB *
make_id_htab(const char *name, Size entrysize, long nelem)
{
    HASHCTL hctl;

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize = sizeof(IdKey);
    hctl.entrysize = entrysize;
    return hash_create(name, nelem, &hctl, HASH_ELEM | HASH_BLOBS);
}

static void
render_union_add(HTAB *render_ids, Datum **union_ids, int32 *n, int32 *cap,
                 Datum cand)
{
    IdKey        key;
    bool         found;
    RenderEntry *e;

    id_key(&key, cand, "candidate");
    e = (RenderEntry *) hash_search(render_ids, &key, HASH_ENTER, &found);
    if (found)
        return;
    if (*n == *cap)
    {
        *cap *= 2;
        *union_ids = (Datum *) repalloc(*union_ids, sizeof(Datum) * *cap);
    }
    e->slot = *n;
    (*union_ids)[(*n)++] = cand;
}

/* Run one candidate arm and decode it into ArmData + the render union. */
static void
run_arm(SPIPlanPtr plan, Datum ids_arr, Datum lang, bool lang_null,
        ArmData *arm, HTAB *render_ids, Datum **union_ids,
        int32 *un, int32 *ucap, const char *what)
{
    Datum args[2];
    char  nulls[3] = "  ";
    int   rc;

    args[0] = ids_arr;
    args[1] = lang;
    if (lang_null)
        nulls[1] = 'n';

    rc = SPI_execute_plan(plan, args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "realize_batch: %s arm failed: %s",
             what, SPI_result_code_string(rc));

    arm->by_id = make_id_htab(what, sizeof(ArmEntry), 256);
    arm->cap = Max(64, (int32) SPI_processed);
    arm->cands = (Datum *) palloc(sizeof(Datum) * arm->cap);
    arm->n = 0;

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple  tup = SPI_tuptable->vals[r];
        TupleDesc  td = SPI_tuptable->tupdesc;
        bool       isnull;
        Datum      in_id = SPI_getbinval(tup, td, 1, &isnull);
        Datum      cand;
        IdKey      key;
        bool       found;
        ArmEntry  *e;

        if (isnull)
            continue;
        cand = SPI_getbinval(tup, td, 2, &isnull);
        if (isnull)
            continue;
        cand = copy_bytea_datum(cand);

        id_key(&key, in_id, what);
        e = (ArmEntry *) hash_search(arm->by_id, &key, HASH_ENTER, &found);
        if (!found)
        {
            e->start = arm->n;
            e->count = 0;
        }
        /* rows arrive grouped by input id (ORDER BY input id first), so the
         * run stays contiguous; count extends it. */
        if (arm->n == arm->cap)
        {
            arm->cap *= 2;
            arm->cands = (Datum *) repalloc(arm->cands, sizeof(Datum) * arm->cap);
        }
        arm->cands[arm->n++] = cand;
        e->count++;

        render_union_add(render_ids, union_ids, un, ucap, cand);
    }
    SPI_freetuptable(SPI_tuptable);
}

/* First candidate in [start, start+count) whose render is NON-EMPTY. */
static const char *
first_nonempty(const ArmData *arm, const IdKey *key,
               char **rendered, HTAB *render_ids)
{
    ArmEntry *e = (ArmEntry *) hash_search(arm->by_id, key, HASH_FIND, NULL);

    if (e == NULL)
        return NULL;
    for (int32 i = e->start; i < e->start + e->count; i++)
    {
        IdKey        ck;
        RenderEntry *re;

        id_key(&ck, arm->cands[i], "render lookup");
        re = (RenderEntry *) hash_search(render_ids, &ck, HASH_FIND, NULL);
        if (re != NULL && rendered[re->slot] != NULL && rendered[re->slot][0] != '\0')
            return rendered[re->slot];
    }
    return NULL;
}

Datum
pg_laplace_realize_batch(PG_FUNCTION_ARGS)
{
    MemoryContext caller = CurrentMemoryContext;
    ArrayType    *in_arr;
    Datum        *in_elems;
    bool         *in_nulls;
    int           n;
    Datum         lang = (Datum) 0;
    bool          lang_null = true;
    bool          need_finish = false;

    HTAB         *render_ids;
    Datum        *union_ids;
    int32         un = 0, ucap;
    ArmData       arm_name, arm_lemma, arm_trans, arm_def;
    HTAB         *canon;
    char        **rendered;
    Datum        *out;
    bool         *out_nulls;
    ArrayType    *result;
    int           dims[1], lbs[1];

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    in_arr = PG_GETARG_ARRAYTYPE_P(0);
    if (!PG_ARGISNULL(1))
    {
        lang = PG_GETARG_DATUM(1);
        lang_null = false;
    }

    deconstruct_array(in_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &in_elems, &in_nulls, &n);
    if (n == 0)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));

    if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
        elog(ERROR, "realize_batch: SPI_connect failed");

    {
        Oid two[2] = { BYTEAARRAYOID, BYTEAOID };
        Oid one[1] = { BYTEAARRAYOID };

        ensure_plan(&plan_has_name, Q_HAS_NAME, 2, two);
        ensure_plan(&plan_synset_lemma, Q_SYNSET_LEMMA, 2, two);
        ensure_plan(&plan_translation, Q_TRANSLATION, 2, two);
        ensure_plan(&plan_canonical, Q_CANONICAL, 1, one);
        ensure_plan(&plan_defines, Q_DEFINES, 1, one);
        ensure_plan(&plan_render, Q_RENDER, 1, one);
    }

    /* Seed the render union with the inputs themselves (arm 3, self render). */
    render_ids = make_id_htab("realize_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    for (int i = 0; i < n; i++)
        if (!in_nulls[i])
            render_union_add(render_ids, &union_ids, &un, &ucap, in_elems[i]);

    /* ---- the four candidate arms (batched, grouped, rank-ordered) ---- */
    run_arm(plan_has_name, PointerGetDatum(in_arr), lang, lang_null,
            &arm_name, render_ids, &union_ids, &un, &ucap, "has_name");
    run_arm(plan_synset_lemma, PointerGetDatum(in_arr), lang, lang_null,
            &arm_lemma, render_ids, &union_ids, &un, &ucap, "synset_lemma");
    run_arm(plan_translation, PointerGetDatum(in_arr), lang, lang_null,
            &arm_trans, render_ids, &union_ids, &un, &ucap, "translation");
    /* defines takes no lang; reuse the runner with a one-arg plan. */
    {
        Datum args[1] = { PointerGetDatum(in_arr) };
        int   rc = SPI_execute_plan(plan_defines, args, NULL, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "realize_batch: defines arm failed: %s",
                 SPI_result_code_string(rc));
        arm_def.by_id = make_id_htab("defines", sizeof(ArmEntry), 256);
        arm_def.cap = Max(64, (int32) SPI_processed);
        arm_def.cands = (Datum *) palloc(sizeof(Datum) * arm_def.cap);
        arm_def.n = 0;
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td = SPI_tuptable->tupdesc;
            bool      isnull;
            Datum     in_id = SPI_getbinval(tup, td, 1, &isnull);
            Datum     cand;
            IdKey     key;
            bool      found;
            ArmEntry *e;

            if (isnull)
                continue;
            cand = SPI_getbinval(tup, td, 2, &isnull);
            if (isnull)
                continue;
            id_key(&key, in_id, "defines");
            e = (ArmEntry *) hash_search(arm_def.by_id, &key, HASH_ENTER, &found);
            if (found)
                continue;       /* only the TOP-mu row matters (scalar LIMIT 1) */
            cand = copy_bytea_datum(cand);
            if (arm_def.n == arm_def.cap)
            {
                arm_def.cap *= 2;
                arm_def.cands = (Datum *) repalloc(arm_def.cands,
                                                   sizeof(Datum) * arm_def.cap);
            }
            e->start = arm_def.n;
            e->count = 1;
            arm_def.cands[arm_def.n++] = cand;
            render_union_add(render_ids, &union_ids, &un, &ucap, cand);
        }
        SPI_freetuptable(SPI_tuptable);
    }

    /* ---- canonical-name arm (text result, no rendering) ---- */
    canon = make_id_htab("canonical", sizeof(CanonEntry), 256);
    {
        Datum args[1] = { PointerGetDatum(in_arr) };
        int   rc = SPI_execute_plan(plan_canonical, args, NULL, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "realize_batch: canonical arm failed: %s",
                 SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple   tup = SPI_tuptable->vals[r];
            TupleDesc   td = SPI_tuptable->tupdesc;
            bool        isnull;
            Datum       in_id = SPI_getbinval(tup, td, 1, &isnull);
            Datum       txt;
            IdKey       key;
            bool        found;
            CanonEntry *e;

            if (isnull)
                continue;
            txt = SPI_getbinval(tup, td, 2, &isnull);
            if (isnull)
                continue;
            id_key(&key, in_id, "canonical");
            e = (CanonEntry *) hash_search(canon, &key, HASH_ENTER, &found);
            if (!found)         /* first row per id (scalar returns first) */
                e->text = datumCopy(txt, false, -1);
        }
        SPI_freetuptable(SPI_tuptable);
    }

    /* ---- ONE shared render pass over every candidate + every input ---- */
    rendered = (char **) palloc0(sizeof(char *) * Max(un, 1));
    if (un > 0)
    {
        ArrayType *ids_arr = construct_array(union_ids, un, BYTEAOID, -1,
                                             false, TYPALIGN_INT);
        Datum      args[1] = { PointerGetDatum(ids_arr) };
        int        rc = SPI_execute_plan(plan_render, args, NULL, true, 1);
        bool       isnull;
        Datum      arr_datum;

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "realize_batch: render batch failed: %s",
                 SPI_result_code_string(rc));
        arr_datum = SPI_getbinval(SPI_tuptable->vals[0],
                                  SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            ArrayType *ra = DatumGetArrayTypeP(arr_datum);
            Datum     *relems;
            bool      *rnulls;
            int        rn;

            deconstruct_array(ra, TEXTOID, -1, false, TYPALIGN_INT,
                              &relems, &rnulls, &rn);
            if (rn != un)
                elog(ERROR, "realize_batch: render batch returned %d of %d", rn, un);
            for (int i = 0; i < un; i++)
                if (!rnulls[i])
                    rendered[i] = text_to_cstring(DatumGetTextPP(relems[i]));
        }
        SPI_freetuptable(SPI_tuptable);
    }

    /* ---- per-id COALESCE ladder, output aligned to the input ---- */
    out = (Datum *) palloc(sizeof(Datum) * n);
    out_nulls = (bool *) palloc(sizeof(bool) * n);
    for (int i = 0; i < n; i++)
    {
        IdKey       key;
        const char *label = NULL;
        bool        have = false;

        out_nulls[i] = true;
        out[i] = (Datum) 0;
        if (in_nulls[i])
            continue;
        id_key(&key, in_elems[i], "input");

        /* arms 1, 2: first non-empty render */
        label = first_nonempty(&arm_name, &key, rendered, render_ids);
        if (label == NULL)
            label = first_nonempty(&arm_lemma, &key, rendered, render_ids);
        /* arm 3: self render, NULLIF '' */
        if (label == NULL)
        {
            RenderEntry *re = (RenderEntry *) hash_search(render_ids, &key,
                                                          HASH_FIND, NULL);

            if (re != NULL && rendered[re->slot] != NULL
                && rendered[re->slot][0] != '\0')
                label = rendered[re->slot];
        }
        /* arm 4: translation, first non-empty render */
        if (label == NULL)
            label = first_nonempty(&arm_trans, &key, rendered, render_ids);
        /* arm 5: canonical name text AS-IS (no non-empty filter, per scalar) */
        if (label == NULL)
        {
            CanonEntry *ce = (CanonEntry *) hash_search(canon, &key,
                                                        HASH_FIND, NULL);

            if (ce != NULL)
            {
                MemoryContext old = MemoryContextSwitchTo(caller);

                out[i] = datumCopy(ce->text, false, -1);
                MemoryContextSwitchTo(old);
                out_nulls[i] = false;
                have = true;
            }
        }
        /* arm 6: top-mu definition's render AS-IS (may be NULL; '' is a result) */
        if (label == NULL && !have)
        {
            ArmEntry *e = (ArmEntry *) hash_search(arm_def.by_id, &key,
                                                   HASH_FIND, NULL);

            if (e != NULL)
            {
                IdKey        ck;
                RenderEntry *re;

                id_key(&ck, arm_def.cands[e->start], "defines render");
                re = (RenderEntry *) hash_search(render_ids, &ck,
                                                 HASH_FIND, NULL);
                if (re != NULL && rendered[re->slot] != NULL)
                    label = rendered[re->slot];
            }
        }

        if (label != NULL)
        {
            MemoryContext old = MemoryContextSwitchTo(caller);

            out[i] = CStringGetTextDatum(label);
            MemoryContextSwitchTo(old);
            out_nulls[i] = false;
        }
    }

    {
        MemoryContext old = MemoryContextSwitchTo(caller);

        dims[0] = n;
        lbs[0] = 1;
        result = construct_md_array(out, out_nulls, 1, dims, lbs,
                                    TEXTOID, -1, false, TYPALIGN_INT);
        MemoryContextSwitchTo(old);
    }

    laplace_spi_finish(need_finish);
    PG_RETURN_ARRAYTYPE_P(result);
}
