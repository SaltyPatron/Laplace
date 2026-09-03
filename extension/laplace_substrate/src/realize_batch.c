/*
 * realize_batch.c — realize.realize() for N ids in a fixed number of batched SPI
 * round-trips (6), positionally aligned to the input array.
 *
 * The scalar ladder it reproduces exactly (realize.sql.in, lang-only path —
 * resolve_name is called WITHOUT context, so only its null-context branch
 * matters):
 *
 *   COALESCE(
 *     realize._has_name(id, lang),      -- arm 1: first NON-EMPTY render
 *     realize._synset_lemma(id, lang),  -- arm 2: first NON-EMPTY render
 *     NULLIF(realize.render_text(id), ''),  -- arm 3: exact self render
 *     realize._translation(id, lang),   -- arm 4: first NON-EMPTY render
 *     realize._canonical(id),           -- arm 5: first row, text AS-IS
 *     realize._defines(id))             -- arm 6: TOP-mu row, render AS-IS
 *
 * Parity notes (deliberate, match the scalar helpers byte-for-byte):
 *   - arms 1/2/4 filter candidates to non-empty renders BEFORE their LIMIT 1,
 *     so the batch walks each id's rank-ordered candidates and takes the first
 *     whose render is non-empty;
 *   - arms 5/6 have NO non-empty filter in the scalar: arm 5 returns the exact
 *     technical registry name as-is, arm 6 returns the render of the single
 *     top-mu definition as-is (possibly NULL → overall NULL, possibly '' → ''
 *     is the final answer);
 *   - arm 2 joins plain consensus for the HAS_SENSE hop (only the IS_SENSE_OF
 *     edge goes through the unrefuted view), exactly as _realize_synset_lemma;
 *   - a NULL lang makes every lp flag false (LEFT JOIN on object_id = NULL
 *     never matches), identical to the scalar helpers;
 *   - abstention: unresolvable ids yield SQL NULL, never hex.
 *
 * All candidate rendering funnels through ONE realize.render_text_batch($ids) call
 * (generate_walk.c) — one shared, complete, cycle-safe constituent closure + memo
 * across every candidate of every arm plus the inputs themselves.
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
PG_FUNCTION_INFO_V1(pg_laplace_resolve_name_batch);

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
    "       consensus.eff_mu(nm.rating, nm.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted nm"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = nm.object_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE nm.subject_id = ANY($1)"
    "   AND nm.type_id IN (laplace.relation_type_id('HAS_NAME'),"
    "                      laplace.relation_type_id('HAS_NAME_ALIAS'))"
    " ORDER BY nm.subject_id, lp DESC, prim DESC, mu DESC, nm.object_id";

static const char *Q_SYNSET_LEMMA =
    "SELECT io.object_id, hs.subject_id,"
    "       (lang.object_id IS NOT NULL) AS lp,"
    "       consensus.eff_mu(hs.rating, hs.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted io"
    " JOIN laplace.consensus hs ON hs.object_id = io.subject_id"
    "   AND hs.type_id = laplace.relation_type_id('HAS_SENSE')"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = hs.subject_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE io.object_id = ANY($1)"
    "   AND io.type_id = laplace.relation_type_id('IS_SENSE_OF')"
    " ORDER BY io.object_id, lp DESC, mu DESC, hs.subject_id";

static const char *Q_TRANSLATION =
    "SELECT m.subject_id, m.object_id,"
    "       (lang.object_id IS NOT NULL) AS lp,"
    "       consensus.eff_mu(m.rating, m.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted m"
    " LEFT JOIN laplace.consensus lang ON lang.subject_id = m.object_id"
    "   AND lang.type_id = laplace.relation_type_id('HAS_LANGUAGE')"
    "   AND lang.object_id = $2"
    " WHERE m.subject_id = ANY($1)"
    "   AND m.type_id = laplace.relation_type_id('IS_TRANSLATION_OF')"
    " ORDER BY m.subject_id, lp DESC, mu DESC, m.object_id";

/* Canonical registry strings are opaque technical identity metadata.  Return the
 * exact stored value; do not parse a source/path spelling to manufacture a label. */
static const char *Q_CANONICAL =
    "SELECT n.id, n.name"
    " FROM laplace.canonical_names n"
    " WHERE n.id = ANY($1)"
    " ORDER BY n.id";

static const char *Q_DEFINES =
    "SELECT g.subject_id, g.object_id,"
    "       consensus.eff_mu(g.rating, g.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted g"
    " WHERE g.subject_id = ANY($1)"
    "   AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')"
    " ORDER BY g.subject_id, mu DESC, g.object_id";   /* object_id CLOSES the order */

static const char *Q_RENDER =
    "SELECT realize.render_text_batch($1)";

static SPIPlanPtr ensure_plan(SPIPlanPtr *slot, const char *sql,
                              int nargs, const Oid *argtypes);

static void
ensure_name_plans(void)
{
    Oid two[2] = { BYTEAARRAYOID, BYTEAOID };
    Oid one[1] = { BYTEAARRAYOID };

    ensure_plan(&plan_has_name, Q_HAS_NAME, 2, two);
    ensure_plan(&plan_synset_lemma, Q_SYNSET_LEMMA, 2, two);
    ensure_plan(&plan_render, Q_RENDER, 1, one);
}

static SPIPlanPtr
ensure_plan(SPIPlanPtr *slot, const char *sql, int nargs, const Oid *argtypes)
{
    if (*slot == NULL)
    {
        SPIPlanPtr plan = SPI_prepare_cursor(sql, nargs, (Oid *) argtypes, CURSOR_OPT_PARALLEL_OK);

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

/* EVERY ARM CARRIES A TOTAL ORDER. Each of these took "the first row per id" off an
 * ORDER BY that ended on mu, so any tie was broken by whatever order the plan
 * happened to produce -- and the plan depends on the size of the input array. Running
 * the arms on the residual instead of on every input changed that array and therefore
 * changed which definition won for 4 of 314 ids ("dog" vs "a form of abuse" for the
 * same entity). The rows were equally valid; the choice was simply not reproducible.
 * Closing each ORDER BY on an id makes the winner a property of the data. */

/* Canonical-name arm: id -> exact technical registry text (first row per id). */
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

static void
run_name_arms(ArrayType *in_arr, Datum lang, bool lang_null,
              ArmData *arm_name, ArmData *arm_lemma,
              HTAB *render_ids, Datum **union_ids, int32 *un, int32 *ucap)
{
    run_arm(plan_has_name, PointerGetDatum(in_arr), lang, lang_null,
            arm_name, render_ids, union_ids, un, ucap, "has_name");
    run_arm(plan_synset_lemma, PointerGetDatum(in_arr), lang, lang_null,
            arm_lemma, render_ids, union_ids, un, ucap, "synset_lemma");
}

static bool
validate_id_array(ArrayType *arr, const char *operation)
{
    if (ARR_NDIM(arr) == 0)
        return false;
    if (ARR_NDIM(arr) != 1)
        ereport(ERROR,
                (errmsg("%s: ids must be 1-dimensional", operation)));
    if (ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR,
                (errmsg("%s: element type must be bytea", operation)));
    return true;
}

static char **
render_union(Datum *union_ids, int32 un, const char *operation)
{
    char **rendered = (char **) palloc0(sizeof(char *) * Max(un, 1));

    if (un > 0)
    {
        ArrayType *ids_arr = construct_array(union_ids, un, BYTEAOID, -1,
                                             false, TYPALIGN_INT);
        Datum      args[1] = { PointerGetDatum(ids_arr) };
        int        rc = SPI_execute_plan(plan_render, args, NULL, true, 1);
        bool       isnull;
        Datum      arr_datum;

        if (rc != SPI_OK_SELECT || SPI_processed != 1)
            elog(ERROR, "%s: render batch failed: %s", operation,
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
                elog(ERROR, "%s: render batch returned %d of %d",
                     operation, rn, un);
            for (int i = 0; i < un; i++)
                if (!rnulls[i])
                    rendered[i] = text_to_cstring(DatumGetTextPP(relems[i]));
        }
        SPI_freetuptable(SPI_tuptable);
    }
    return rendered;
}

Datum
pg_laplace_resolve_name_batch(PG_FUNCTION_ARGS)
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
    HTAB         *canon;
    Datum        *union_ids;
    int32         un = 0, ucap;
    ArmData       arm_name, arm_lemma;
    char        **rendered;
    Datum        *out;
    bool         *out_nulls;
    ArrayType    *result;
    int           dims[1], lbs[1];

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    in_arr = PG_GETARG_ARRAYTYPE_P(0);
    if (!validate_id_array(in_arr, "resolve_name_batch"))
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
    if (!PG_ARGISNULL(1))
    {
        lang = PG_GETARG_DATUM(1);
        lang_null = false;
    }
    deconstruct_array(in_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &in_elems, &in_nulls, &n);
    if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
        elog(ERROR, "resolve_name_batch: SPI_connect failed");
    ensure_name_plans();

    render_ids = make_id_htab("resolve_name_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    run_name_arms(in_arr, lang, lang_null, &arm_name, &arm_lemma,
                  render_ids, &union_ids, &un, &ucap);
    rendered = render_union(union_ids, un, "resolve_name_batch");

    /* THE CANONICAL ARM. realize.resolve_name's null-context branch is
     * COALESCE(_has_name, _synset_lemma, _canonical) -- THREE arms -- and this batch
     * carried only the first two, so it silently returned NULL where the scalar
     * returned a name. Found by comparing the two over 2,000 tier-2 entities: 19
     * non-null from the scalar, 18 from the batch, differing on one id that has no
     * render and resolves canonically to 'CONTENT'. Anyone swapping the scalar for
     * the batch to make a sweep affordable was losing canonical names.
     *
     * Same query and same "first row per id" rule realize_batch uses below. */
    canon = make_id_htab("resolve_name_batch canonical", sizeof(CanonEntry), 256);
    {
        Oid   one[1] = { BYTEAARRAYOID };
        Datum args[1] = { PointerGetDatum(in_arr) };
        int   rc;

        ensure_plan(&plan_canonical, Q_CANONICAL, 1, one);
        rc = SPI_execute_plan(plan_canonical, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "resolve_name_batch: canonical arm failed: %s",
                 SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple   tup = SPI_tuptable->vals[r];
            TupleDesc   td = SPI_tuptable->tupdesc;
            bool        isnull;
            Datum       in_id = SPI_getbinval(tup, td, 1, &isnull);
            Datum       txt;
            IdKey       ckey;
            bool        found;
            CanonEntry *ce;

            if (isnull)
                continue;
            txt = SPI_getbinval(tup, td, 2, &isnull);
            if (isnull)
                continue;
            id_key(&ckey, in_id, "canonical");
            ce = (CanonEntry *) hash_search(canon, &ckey, HASH_ENTER, &found);
            if (!found)
                ce->text = datumCopy(txt, false, -1);
        }
        SPI_freetuptable(SPI_tuptable);
    }

    out = (Datum *) palloc0(sizeof(Datum) * n);
    out_nulls = (bool *) palloc(sizeof(bool) * n);
    for (int i = 0; i < n; i++)
    {
        IdKey       key;
        const char *label = NULL;

        out_nulls[i] = true;
        if (in_nulls[i])
            continue;
        id_key(&key, in_elems[i], "input");
        label = first_nonempty(&arm_name, &key, rendered, render_ids);
        if (label == NULL)
            label = first_nonempty(&arm_lemma, &key, rendered, render_ids);
        if (label != NULL)
        {
            MemoryContext old = MemoryContextSwitchTo(caller);

            out[i] = CStringGetTextDatum(label);
            MemoryContextSwitchTo(old);
            out_nulls[i] = false;
        }
        else
        {
            /* arm 3: canonical name text AS-IS, matching the scalar's third arm. */
            CanonEntry *ce = (CanonEntry *) hash_search(canon, &key,
                                                        HASH_FIND, NULL);

            if (ce != NULL)
            {
                MemoryContext old = MemoryContextSwitchTo(caller);

                out[i] = datumCopy(ce->text, false, -1);
                MemoryContextSwitchTo(old);
                out_nulls[i] = false;
            }
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
    ArrayType    *arm_input = NULL;   /* residual ids the arms run on; NULL = none */
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
    if (!validate_id_array(in_arr, "realize_batch"))
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
    if (!PG_ARGISNULL(1))
    {
        lang = PG_GETARG_DATUM(1);
        lang_null = false;
    }

    deconstruct_array(in_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &in_elems, &in_nulls, &n);
    if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
        elog(ERROR, "realize_batch: SPI_connect failed");

    {
        Oid two[2] = { BYTEAARRAYOID, BYTEAOID };
        Oid one[1] = { BYTEAARRAYOID };

        ensure_name_plans();
        ensure_plan(&plan_translation, Q_TRANSLATION, 2, two);
        ensure_plan(&plan_canonical, Q_CANONICAL, 1, one);
        ensure_plan(&plan_defines, Q_DEFINES, 1, one);
    }

    /* Seed the render union with the inputs themselves (arm 3, self render). */
    render_ids = make_id_htab("realize_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    for (int i = 0; i < n; i++)
        if (!in_nulls[i])
            render_union_add(render_ids, &union_ids, &un, &ucap, in_elems[i]);

    /* ---- THE ARMS RUN ON THE RESIDUAL, NOT ON EVERY INPUT ----
     *
     * Arm 1 is the self render, so an input that renders never consults has_name,
     * synset_lemma, translation, canonical or defines. Measured on 200 real object
     * ids, 185 (92.5%) render directly -- so running all five arms over the whole
     * input array, and rendering every candidate they return, is work thrown away
     * for the overwhelming majority. realize.render_text_batch was 1,385 ms mean over
     * 205 calls, ~1,200 ids per call against ~200 inputs.
     *
     * The inputs are rendered once here to decide the residual. That costs n extra
     * renders and removes roughly 5n candidate renders. Everything downstream --
     * the union, the single shared render pass, the ladder -- is untouched, so an
     * id in the residual is resolved exactly as before.
     *
     * When the residual is empty the arms are skipped entirely: five SPI queries and
     * their whole candidate set never happen. */
    {
        char **probe;

        /* Every arm must be a VALID EMPTY arm before the residual test, because the
         * ladder hash-searches all four unconditionally. run_arm builds by_id itself,
         * and arm_def's htab is built inside the defines block -- both of which are
         * now conditional, so without this an empty residual left four stack-garbage
         * ArmData structs for the ladder to search. That is what produced 4 wrong
         * labels out of 314 on the first attempt. */
        arm_name.by_id  = make_id_htab("has_name empty", sizeof(ArmEntry), 32);
        arm_lemma.by_id = make_id_htab("synset_lemma empty", sizeof(ArmEntry), 32);
        arm_trans.by_id = make_id_htab("translation empty", sizeof(ArmEntry), 32);
        arm_def.by_id   = make_id_htab("defines empty", sizeof(ArmEntry), 32);
        arm_name.cands = arm_lemma.cands = arm_trans.cands = arm_def.cands = NULL;
        arm_name.n = arm_lemma.n = arm_trans.n = arm_def.n = 0;
        arm_name.cap = arm_lemma.cap = arm_trans.cap = arm_def.cap = 0;

        probe = render_union(union_ids, un, "realize_batch residual probe");
        Datum *resid = (Datum *) palloc(sizeof(Datum) * Max(1, n));
        int32  nresid = 0;

        for (int i = 0; i < n; i++)
        {
            IdKey        key;
            RenderEntry *re;

            if (in_nulls[i])
                continue;
            id_key(&key, in_elems[i], "residual");
            re = (RenderEntry *) hash_search(render_ids, &key, HASH_FIND, NULL);
            if (re == NULL || probe[re->slot] == NULL || probe[re->slot][0] == '\0')
                resid[nresid++] = in_elems[i];
        }

        if (nresid > 0)
        {
            ArrayType *resid_arr = construct_array(resid, nresid, BYTEAOID, -1,
                                                   false, TYPALIGN_INT);

            run_name_arms(resid_arr, lang, lang_null, &arm_name, &arm_lemma,
                          render_ids, &union_ids, &un, &ucap);
            run_arm(plan_translation, PointerGetDatum(resid_arr), lang, lang_null,
                    &arm_trans, render_ids, &union_ids, &un, &ucap, "translation");
            arm_input = resid_arr;
        }
        else
            arm_input = NULL;
    }
    /* defines takes no lang; reuse the runner with a one-arg plan. Residual only. */
    if (arm_input != NULL)
    {
        Datum args[1] = { PointerGetDatum(arm_input) };
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

    /* ---- canonical-name arm (exact technical text, no rendering) ---- */
    canon = make_id_htab("canonical", sizeof(CanonEntry), 256);
    if (arm_input != NULL)
    {
        Datum args[1] = { PointerGetDatum(arm_input) };
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
    rendered = render_union(union_ids, un, "realize_batch");

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

        /* arm 1: SELF RENDER, NULLIF '' -- CONTENT BEFORE NAME.
         *
         * render_text is the VALUE an entity carries; a name is the LABEL for it.
         * An entity that has content must emit the content. This arm used to sit
         * third, behind has_name and synset_lemma, so any entity carrying a
         * HAS_NAME edge emitted its name instead of itself -- and every Unicode
         * codepoint carries one, which put "MIDDLE DOT" and "COLON" into replies.
         *
         * Measured over the 224 distinct constituents of 40 tier-3 containers: all
         * 224 render, only 16 carry a name, and all 16 are the label losing to the
         * content (COLON/':', DIGIT ONE/'1', AMPERSAND/'&'). Reordering cannot lose
         * a name: an entity whose name IS the right answer renders empty and falls
         * through to arm 2 (a Language entity renders NULL and names 'English').
         *
         * This MUST match realize/realize.sql.in's COALESCE order -- the two are one
         * policy with two implementations (§15), and they diverged when the scalar
         * was fixed in 0b55b8ac and this was not. */
        {
            RenderEntry *re = (RenderEntry *) hash_search(render_ids, &key,
                                                          HASH_FIND, NULL);

            if (re != NULL && rendered[re->slot] != NULL
                && rendered[re->slot][0] != '\0')
                label = rendered[re->slot];
        }
        /* arms 2, 3: name then synset lemma, first non-empty render */
        if (label == NULL)
            label = first_nonempty(&arm_name, &key, rendered, render_ids);
        if (label == NULL)
            label = first_nonempty(&arm_lemma, &key, rendered, render_ids);
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
