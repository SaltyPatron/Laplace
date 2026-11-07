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

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/lsyscache.h"
#include "utils/rls.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "opaque_surface.h"
#include "consensus_scan.h"
#include "realize_batch_internal.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/entity_type_law.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/sql_catalog.h"

PG_FUNCTION_INFO_V1(pg_laplace_realize_batch);
PG_FUNCTION_INFO_V1(pg_laplace_realize);
PG_FUNCTION_INFO_V1(pg_laplace_resolve_name_batch);
PG_FUNCTION_INFO_V1(pg_laplace_lexicalize_batch);

#define ensure_name_plans realize_ensure_name_plans
#define id_key realize_id_key
#define make_id_htab realize_make_id_htab
#define render_union_add realize_render_union_add
#define run_arm realize_run_arm
#define first_nonempty realize_first_nonempty
#define validate_id_array realize_validate_id_array
#define render_union realize_render_union

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
        run_arm(realize_plan_has_name(), PointerGetDatum(in_arr), lang, lang_null,
                &arm_name, render_ids, &union_ids, &un, &ucap, "has_name");
    else
    {
        arm_name.by_id = make_id_htab("has_name unused", sizeof(ArmEntry), 32);
        arm_name.cands = NULL;
        arm_name.n = arm_name.cap = 0;
    }
    run_arm(realize_plan_synset_lemma(), PointerGetDatum(in_arr), lang, lang_null,
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

    realize_ensure_all_plans();

    /* Seed the render union with the inputs themselves (self render). */
    render_ids = make_id_htab("realize_batch render union",
                              sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);
    for (int i = 0; i < n; i++)
        if (!in_nulls[i])
            render_union_add(render_ids, &union_ids, &un, &ucap, in_elems[i]);

    /* The name, lexicalization, translation and definition arms run only on the
     * residual: ids whose own self render is NULL, empty, or an opaque identifier
     * surface (such as an ILI or WordNet offset). Input renders are the prefix of
     * the append-only union; an arm that names an input again reuses that result.
     * An empty residual runs no arm. */
    {
        /* The ladder searches every arm, and the residual arms are built only
         * when the residual is non-empty, so each starts as a valid empty arm. */
        arm_lemma.by_id = make_id_htab("lexicalization empty", sizeof(ArmEntry), 32);
        arm_name.by_id  = make_id_htab("has_name empty", sizeof(ArmEntry), 32);
        arm_trans.by_id = make_id_htab("translation empty", sizeof(ArmEntry), 32);
        arm_def.by_id   = make_id_htab("defines empty", sizeof(ArmEntry), 32);
        arm_lemma.cands = arm_name.cands = arm_trans.cands = arm_def.cands = NULL;
        arm_lemma.n = arm_name.n = arm_trans.n = arm_def.n = 0;
        arm_lemma.cap = arm_name.cap = arm_trans.cap = arm_def.cap = 0;

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
            re = (RenderEntry *) hash_search(render_ids, &key, HASH_FIND, NULL);
            if (re == NULL || input_rendered[re->slot] == NULL
                || input_rendered[re->slot][0] == '\0'
                || laplace_opaque_surface(input_rendered[re->slot], true))
            {
                hash128_t rid;
                const char *rcanon = NULL;
                memcpy(&rid, key.bytes, 16);
                if (laplace_relation_canonical_for_type_id(&rid) == NULL &&
                    (laplace_entity_type_lookup(&rid, &rcanon) != 0 || rcanon == NULL))
                    resid[nresid++] = in_elems[i];
            }
        }

        if (nresid > 0)
        {
            ArrayType *resid_arr = construct_array(resid, nresid, BYTEAOID, -1,
                                                   false, TYPALIGN_INT);

            run_arm(realize_plan_synset_lemma(), PointerGetDatum(resid_arr), lang, lang_null,
                    &arm_lemma, render_ids, &union_ids, &un, &ucap, "lexicalization");
            run_arm(realize_plan_has_name(), PointerGetDatum(resid_arr), lang, lang_null,
                    &arm_name, render_ids, &union_ids, &un, &ucap, "has_name");
            run_arm(realize_plan_translation(), PointerGetDatum(resid_arr), lang, lang_null,
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
        int   rc = SPI_execute_plan(realize_plan_defines(), args, NULL, true, 0);

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

        /* Firmware relation-type and entity-type registry identities. */
        if (label == NULL)
        {
            hash128_t rid;
            const char *rcanon = NULL;
            memcpy(&rid, key.bytes, 16);
            rcanon = laplace_relation_canonical_for_type_id(&rid);
            if (rcanon != NULL)
                label = rcanon;
            else if (laplace_entity_type_lookup(&rid, &rcanon) == 0 && rcanon != NULL)
                label = rcanon;
        }

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
         * realization renders empty and falls through to the name arm. An
         * opaque identifier surface (ILI or WordNet offset) yields to residual
         * arms if any exist, else keeps its self render. */
        {
            RenderEntry *re = (RenderEntry *) hash_search(render_ids, &key,
                                                          HASH_FIND, NULL);
            const char  *self_s = (re != NULL && rendered[re->slot] != NULL
                                   && rendered[re->slot][0] != '\0')
                                  ? rendered[re->slot] : NULL;

            if (label == NULL && self_s != NULL && !laplace_opaque_surface(self_s, true))
                label = self_s;
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
                    RenderEntry *dre;

                    id_key(&ck, arm_def.cands[e->start], "defines render");
                    dre = (RenderEntry *) hash_search(render_ids, &ck,
                                                      HASH_FIND, NULL);
                    if (dre != NULL && rendered[dre->slot] != NULL)
                        label = rendered[dre->slot];
                }
            }
            if (label == NULL && self_s != NULL)
                label = self_s;
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

Datum
realize_scalar_from_one_elem_batch(ArrayType *arr)
{
    Datum *elems;
    bool  *nulls;
    int    n;

    deconstruct_array(arr, TEXTOID, -1, false, TYPALIGN_INT, &elems, &nulls, &n);
    if (n < 1 || nulls[0])
        return (Datum) 0;
    return elems[0];
}

Datum
pg_laplace_realize(PG_FUNCTION_ARGS)
{
    LOCAL_FCINFO(batch_fcinfo, 2);
    Datum      id_datum;
    ArrayType *in_arr;
    Datum      batch_res;
    Datum      res;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id_datum = PG_GETARG_DATUM(0);
    in_arr = construct_array(&id_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);

    InitFunctionCallInfoData(*batch_fcinfo, NULL, 2, PG_GET_COLLATION(), NULL, NULL);
    batch_fcinfo->args[0].value = PointerGetDatum(in_arr);
    batch_fcinfo->args[0].isnull = false;
    batch_fcinfo->args[1].value = PG_ARGISNULL(1) ? (Datum) 0 : PG_GETARG_DATUM(1);
    batch_fcinfo->args[1].isnull = PG_ARGISNULL(1);

    batch_res = pg_laplace_realize_batch(batch_fcinfo);
    if (batch_fcinfo->isnull)
        PG_RETURN_NULL();
    res = realize_scalar_from_one_elem_batch(DatumGetArrayTypeP(batch_res));
    if (res == (Datum) 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(res);
}
