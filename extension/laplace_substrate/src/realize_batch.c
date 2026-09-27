/*
 * REALIZE for a set of ids: one text per input id, positionally aligned, from a
 * bounded number of set reads over unrefuted consensus. Per id, the first of:
 *   - lexicalization: a word attested HAS_SENSE of it, attested in the
 *     requested language first, then by effective mu, first non-empty render;
 *   - a tier-0 codepoint's own glyph, from the perfcache;
 *   - its own content render, if non-empty;
 *   - an attested HAS_NAME, first non-empty render;
 *   - an IS_TRANSLATION_OF target, requested language first, first non-empty;
 *   - the render of its top-mu HAS_DEFINITION as-is ('' is a result).
 * A NULL lang gives no language preference. Ids nothing realizes yield NULL,
 * never hex. realize.realize() is the scalar form of the same order.
 *
 * Inputs and lexicalization candidates render in one batch through the
 * cycle-safe constituent closure (realize.render_text_batch). The name,
 * translation and definition arms run only on ids that neither lexicalize nor
 * render, and only the ids they add render in a second batch. The render union
 * is append-only, so slots are stable and a repeated id reuses its result.
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
#include "perfcache_native.h"
#include "laplace/core/sql_catalog.h"

PG_FUNCTION_INFO_V1(pg_laplace_realize_batch);
PG_FUNCTION_INFO_V1(pg_laplace_resolve_name_batch);
PG_FUNCTION_INFO_V1(pg_laplace_lexicalize_batch);

/* ---- prepared plans, one per arm, kept across calls ---- */

static SPIPlanPtr plan_has_name = NULL;
static SPIPlanPtr plan_synset_lemma = NULL;
static SPIPlanPtr plan_translation = NULL;
static SPIPlanPtr plan_defines = NULL;
static SPIPlanPtr plan_render = NULL;

/* Names: realize.name_candidates (sql_catalog.def), relation HAS_NAME. */

/* Lexicalization: an entity is realized by the words attested to express it
 * (word HAS_SENSE entity), requested language first, then strongest. */
static const char *Q_SYNSET_LEMMA =
    "SELECT hs.object_id, hs.subject_id,"
    /* The language is the attestation's context, not the word's: one surface
     * can express different entities in different languages. One probe of the
     * (subject, type, object) attestation index per candidate. */
    "       EXISTS (SELECT 1 FROM laplace.attestations a"
    "               WHERE a.subject_id = hs.subject_id AND a.type_id = hs.type_id"
    "                 AND a.object_id = hs.object_id AND a.context_id = $2) AS lp,"
    "       consensus.eff_mu(hs.rating, hs.rd) AS mu"
    " FROM laplace.v_consensus_unrefuted hs"
    " WHERE hs.object_id = ANY($1)"
    "   AND hs.type_id = laplace.relation_type_id('HAS_SENSE')"
    " ORDER BY hs.object_id, lp DESC, mu DESC, hs.subject_id";

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

    ensure_plan(&plan_has_name, laplace_sql_query_text("realize.name_candidates"), 2, two);
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

/* Every arm's ORDER BY closes on an id, so the first row per input id is a
 * property of the data, not of the plan the input array's size selects. */


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

static Datum label_batch(FunctionCallInfo fcinfo, bool with_names, const char *what);

Datum
pg_laplace_resolve_name_batch(PG_FUNCTION_ARGS)
{
    return label_batch(fcinfo, true, "resolve_name_batch");
}

/* (ids bytea[], lang bytea) -> text[]: each concept's lexicalization (the best word a
 * source says expresses it), NULL for ids nothing lexicalizes. */
Datum
pg_laplace_lexicalize_batch(PG_FUNCTION_ARGS)
{
    return label_batch(fcinfo, false, "lexicalize_batch");
}

static Datum
label_batch(FunctionCallInfo fcinfo, bool with_names, const char *what)
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
    ArmData       arm_name, arm_lemma;
    char        **rendered;
    Datum        *out;
    bool         *out_nulls;
    ArrayType    *result;
    int           dims[1], lbs[1];

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    in_arr = PG_GETARG_ARRAYTYPE_P(0);
    if (!validate_id_array(in_arr, what))
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
    if (!PG_ARGISNULL(1))
    {
        lang = PG_GETARG_DATUM(1);
        lang_null = false;
    }
    deconstruct_array(in_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &in_elems, &in_nulls, &n);
    if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
        elog(ERROR, "%s: SPI_connect failed", what);
    ensure_name_plans();

    render_ids = make_id_htab("label_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    if (with_names)
        run_arm(plan_has_name, PointerGetDatum(in_arr), lang, lang_null,
                &arm_name, render_ids, &union_ids, &un, &ucap, "has_name");
    else
    {
        arm_name.by_id = make_id_htab("has_name unused", sizeof(ArmEntry), 32);
        arm_name.cands = NULL;
        arm_name.n = arm_name.cap = 0;
    }
    run_arm(plan_synset_lemma, PointerGetDatum(in_arr), lang, lang_null,
            &arm_lemma, render_ids, &union_ids, &un, &ucap, "lexicalization");
    rendered = render_union(union_ids, un, what);

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
    int32         input_render_count = 0;
    char        **input_rendered;
    ArmData       arm_name, arm_lemma, arm_trans, arm_def;
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
        ensure_plan(&plan_defines, Q_DEFINES, 1, one);
    }

    /* Seed the render union with the inputs themselves (self render). */
    render_ids = make_id_htab("realize_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    for (int i = 0; i < n; i++)
        if (!in_nulls[i])
            render_union_add(render_ids, &union_ids, &un, &ucap, in_elems[i]);

    /* The name, translation and definition arms run only on the residual: ids
     * with no lexicalization and no non-empty self render. Input renders are
     * the prefix of the append-only union; an arm that names an input again
     * reuses that result (NULL or empty included). An empty residual runs no
     * arm. */
    {
        /* The ladder searches every arm, and the residual arms are built only
         * when the residual is non-empty, so each starts as a valid empty arm. */
        arm_name.by_id  = make_id_htab("has_name empty", sizeof(ArmEntry), 32);
        arm_trans.by_id = make_id_htab("translation empty", sizeof(ArmEntry), 32);
        arm_def.by_id   = make_id_htab("defines empty", sizeof(ArmEntry), 32);
        arm_name.cands = arm_trans.cands = arm_def.cands = NULL;
        arm_name.n = arm_trans.n = arm_def.n = 0;
        arm_name.cap = arm_trans.cap = arm_def.cap = 0;

        /* Lexicalization runs over every input and precedes the self render:
         * an entity whose own content is an identifier is realized through the
         * words that express it. An id that is no HAS_SENSE object costs one
         * index miss. */
        run_arm(plan_synset_lemma, PointerGetDatum(in_arr), lang, lang_null,
                &arm_lemma, render_ids, &union_ids, &un, &ucap, "lexicalization");

        input_render_count = un;
        input_rendered = render_union(union_ids, un, "realize_batch inputs");
        Datum *resid = (Datum *) palloc(sizeof(Datum) * Max(1, n));
        int32  nresid = 0;

        for (int i = 0; i < n; i++)
        {
            IdKey        key;
            RenderEntry *re;

            if (in_nulls[i])
                continue;
            id_key(&key, in_elems[i], "residual");
            if (first_nonempty(&arm_lemma, &key, input_rendered, render_ids) != NULL)
                continue;
            re = (RenderEntry *) hash_search(render_ids, &key, HASH_FIND, NULL);
            if (re == NULL || input_rendered[re->slot] == NULL
                || input_rendered[re->slot][0] == '\0')
                resid[nresid++] = in_elems[i];
        }

        if (nresid > 0)
        {
            ArrayType *resid_arr = construct_array(resid, nresid, BYTEAOID, -1,
                                                   false, TYPALIGN_INT);

            run_arm(plan_has_name, PointerGetDatum(resid_arr), lang, lang_null,
                    &arm_name, render_ids, &union_ids, &un, &ucap, "has_name");
            run_arm(plan_translation, PointerGetDatum(resid_arr), lang, lang_null,
                    &arm_trans, render_ids, &union_ids, &un, &ucap, "translation");
            arm_input = resid_arr;
        }
        else
            arm_input = NULL;
    }
    /* Definition arm: residual only, no language argument, top-mu row per id. */
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
                continue;       /* only the top-mu row per id */
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

    /* Render only the ids the residual arms appended; existing slots never
     * move. */
    rendered = input_rendered;
    if (un > input_render_count)
    {
        int32  additional_count = un - input_render_count;
        char **additional = render_union(union_ids + input_render_count,
                                          additional_count,
                                          "realize_batch fallback candidates");

        rendered = (char **) repalloc(rendered, sizeof(char *) * un);
        memcpy(rendered + input_render_count, additional,
               sizeof(char *) * additional_count);
        pfree(additional);
    }

    /* ---- per-id ladder, output aligned to the input ---- */
    out = (Datum *) palloc(sizeof(Datum) * n);
    out_nulls = (bool *) palloc(sizeof(bool) * n);
    for (int i = 0; i < n; i++)
    {
        IdKey       key;
        const char *label = NULL;

        out_nulls[i] = true;
        out[i] = (Datum) 0;
        if (in_nulls[i])
            continue;
        id_key(&key, in_elems[i], "input");

        /* Lexicalization: the words expressing the entity. */
        label = first_nonempty(&arm_lemma, &key, rendered, render_ids);

        /* A codepoint is its own glyph (perfcache), never its UCD name. */
        if (label == NULL)
        {
            uint32_t cp;

            if (laplace_perfcache_codepoint_for_id((const uint8_t *) VARDATA_ANY(DatumGetByteaPP(in_elems[i])), &cp)
                && cp != 0 && (cp < 0xD800 || cp > 0xDFFF))
            {
                char   buf[5];
                int    nb = 0;

                if (cp < 0x80) buf[nb++] = (char) cp;
                else if (cp < 0x800) { buf[nb++] = (char) (0xC0 | (cp >> 6)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
                else if (cp < 0x10000) { buf[nb++] = (char) (0xE0 | (cp >> 12)); buf[nb++] = (char) (0x80 | ((cp >> 6) & 0x3F)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
                else { buf[nb++] = (char) (0xF0 | (cp >> 18)); buf[nb++] = (char) (0x80 | ((cp >> 12) & 0x3F)); buf[nb++] = (char) (0x80 | ((cp >> 6) & 0x3F)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
                buf[nb] = '\0';
                label = pstrdup(buf);
            }
        }

        /* Self render before name: an entity that carries content emits its
         * content; a name is a label for it. An entity whose name is its only
         * realization renders empty and falls through to the name arm. */
        {
            RenderEntry *re = (RenderEntry *) hash_search(render_ids, &key,
                                                          HASH_FIND, NULL);

            if (label == NULL && re != NULL && rendered[re->slot] != NULL
                && rendered[re->slot][0] != '\0')
                label = rendered[re->slot];
        }
        /* An attested name, first non-empty render. */
        if (label == NULL)
            label = first_nonempty(&arm_name, &key, rendered, render_ids);
        /* A translation target, first non-empty render. */
        if (label == NULL)
            label = first_nonempty(&arm_trans, &key, rendered, render_ids);
        /* The top-mu definition's render as-is (may be NULL; '' is a result). */
        if (label == NULL)
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
