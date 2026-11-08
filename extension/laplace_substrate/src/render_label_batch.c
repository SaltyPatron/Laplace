/*
 * Shared native implementation for:
 *   realize.render_batch(p_ids) / realize.render(p_id)
 *   realize.label_batch(p_ids)  / realize.label(p_id)
 *   converse.label_or_hex_batch(p_ids) / converse.label_or_hex(p_id)
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "opaque_surface.h"
#include "realize_batch_internal.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/entity_type_law.h"

PG_FUNCTION_INFO_V1(pg_laplace_render_batch);
PG_FUNCTION_INFO_V1(pg_laplace_render);
PG_FUNCTION_INFO_V1(pg_laplace_label_batch);
PG_FUNCTION_INFO_V1(pg_laplace_label);
PG_FUNCTION_INFO_V1(pg_laplace_label_or_hex_batch);
PG_FUNCTION_INFO_V1(pg_laplace_label_or_hex);

/*
 * Strip canonical URI prefixes/suffixes and opaque lexicon surfaces in pure C,
 * matching the historical 8-deep regexp_replace chain in realize.label:
 *   ^substrate/(?:type|relation|source|entity)/(.*)/v1$ -> \1
 *   ^substrate/pos/(?:probationary/[^/]+/)?(.+)/v1$     -> \1
 *   ^source/file/(.+)/v1$                               -> \1
 *   ^repo:(.+)/v1$                                      -> \1
 *   ^language:([a-z]{3})$                               -> \1
 *   ^ud/feature/(.+)$                                   -> \1
 *   ^tiny-codes/concept/(.+)/v1$                        -> \1
 *   ^(i[0-9]+|[0-9]{6,}-[nvarspNVARSP])$                -> ''
 */
static const char *
laplace_strip_canonical_label(const char *s)
{
    size_t len;

    if (s == NULL)
        return NULL;
    len = strlen(s);
    if (len == 0)
        return "";

    /* Rules with /v1 suffix */
    if (len > 3 && memcmp(s + len - 3, "/v1", 3) == 0)
    {
        size_t body_end = len - 3;

        if (body_end >= 10 && memcmp(s, "substrate/", 10) == 0)
        {
            const char *rest = s + 10;
            size_t      rlen = body_end - 10;

            if (rlen >= 5 && memcmp(rest, "type/", 5) == 0)
                return pnstrdup(rest + 5, rlen - 5);
            if (rlen >= 9 && memcmp(rest, "relation/", 9) == 0)
                return pnstrdup(rest + 9, rlen - 9);
            if (rlen >= 7 && memcmp(rest, "source/", 7) == 0)
                return pnstrdup(rest + 7, rlen - 7);
            if (rlen >= 7 && memcmp(rest, "entity/", 7) == 0)
                return pnstrdup(rest + 7, rlen - 7);
            if (rlen > 4 && memcmp(rest, "pos/", 4) == 0)
            {
                const char *pos = rest + 4;
                size_t      plen = rlen - 4;

                if (plen > 13 && memcmp(pos, "probationary/", 13) == 0)
                {
                    const char *sub = pos + 13;
                    size_t      slen = plen - 13;
                    const char *slash = (const char *) memchr(sub, '/', slen);

                    if (slash != NULL && slash > sub && (size_t) (slash - sub + 1) < slen)
                    {
                        size_t off = (size_t) (slash - sub) + 1;

                        return pnstrdup(sub + off, slen - off);
                    }
                }
                return pnstrdup(pos, plen);
            }
        }
        else if (body_end > 12 && memcmp(s, "source/file/", 12) == 0)
        {
            return pnstrdup(s + 12, body_end - 12);
        }
        else if (body_end > 5 && memcmp(s, "repo:", 5) == 0)
        {
            return pnstrdup(s + 5, body_end - 5);
        }
        else if (body_end > 19 && memcmp(s, "tiny-codes/concept/", 19) == 0)
        {
            return pnstrdup(s + 19, body_end - 19);
        }
    }

    if (len == 12 && memcmp(s, "language:", 9) == 0 &&
        s[9] >= 'a' && s[9] <= 'z' &&
        s[10] >= 'a' && s[10] <= 'z' &&
        s[11] >= 'a' && s[11] <= 'z')
    {
        return pnstrdup(s + 9, 3);
    }

    if (len > 11 && memcmp(s, "ud/feature/", 11) == 0)
    {
        return pnstrdup(s + 11, len - 11);
    }

    /* Opaque lexicon surface: ^(i[0-9]+|[0-9]{6,}-[nvarspNVARSP])$ -> '' */
    if (s[0] == 'i' && len > 1)
    {
        size_t i = 1;

        while (i < len && s[i] >= '0' && s[i] <= '9')
            ++i;
        if (i == len)
            return "";
    }
    {
        size_t i = 0;

        while (i < len && s[i] >= '0' && s[i] <= '9')
            ++i;
        if (i >= 6 && len == i + 2 && s[i] == '-' && strchr("nvarspNVARSP", s[i + 1]) != NULL)
            return "";
    }

    return s;
}

/*
 * Shared native implementation for:
 *   mode 0: realize.render_batch(p_ids)
 *   mode 1: realize.label_batch(p_ids)
 *   mode 2: converse.label_or_hex_batch(p_ids)
 */
static ArrayType *
render_or_label_batch_internal(ArrayType *in_arr, int mode, const char *what)
{
    MemoryContext caller = CurrentMemoryContext;
    Datum        *in_elems;
    bool         *in_nulls;
    int           n;
    bool          need_finish = false;
    HTAB         *render_ids;
    Datum        *union_ids;
    int32         un = 0, ucap;
    int32         input_render_count = 0;
    char        **input_rendered;
    char        **rendered;
    ArmData       arm_lemma, arm_name;
    const char  **primary_val;
    Datum        *out;
    bool         *out_nulls;
    ArrayType    *result;
    int           dims[1], lbs[1];

    if (!realize_validate_id_array(in_arr, what))
        return construct_empty_array(TEXTOID);

    deconstruct_array(in_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &in_elems, &in_nulls, &n);
    if (n == 0)
        return construct_empty_array(TEXTOID);

    render_ids = realize_make_id_htab("render_batch union",
                                      sizeof(RenderEntry), Max(256, n * 2));
    ucap = Max(64, n * 2);
    union_ids = (Datum *) palloc(sizeof(Datum) * ucap);

    primary_val = (const char **) palloc0(sizeof(const char *) * n);
    for (int i = 0; i < n; i++)
    {
        hash128_t   id;
        const char *canon = NULL;
        uint32_t    cp;

        if (in_nulls[i])
            continue;
        id = datum_to_hash128(in_elems[i]);
        canon = laplace_relation_canonical_for_type_id(&id);
        if (canon == NULL && laplace_entity_type_lookup(&id, &canon) != 0)
            canon = NULL;
        if (canon != NULL)
        {
            primary_val[i] = canon;
            continue;
        }
        if (laplace_perfcache_codepoint_for_id(
                (const uint8_t *) VARDATA_ANY(DatumGetByteaPP(in_elems[i])), &cp) &&
            cp != 0 && (cp < 0xD800 || cp > 0xDFFF))
        {
            char buf[5];
            int  nb = 0;

            if (cp < 0x80) buf[nb++] = (char) cp;
            else if (cp < 0x800) { buf[nb++] = (char) (0xC0 | (cp >> 6)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
            else if (cp < 0x10000) { buf[nb++] = (char) (0xE0 | (cp >> 12)); buf[nb++] = (char) (0x80 | ((cp >> 6) & 0x3F)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
            else { buf[nb++] = (char) (0xF0 | (cp >> 18)); buf[nb++] = (char) (0x80 | ((cp >> 12) & 0x3F)); buf[nb++] = (char) (0x80 | ((cp >> 6) & 0x3F)); buf[nb++] = (char) (0x80 | (cp & 0x3F)); }
            buf[nb] = '\0';
            primary_val[i] = pstrdup(buf);
        }
        realize_render_union_add(render_ids, &union_ids, &un, &ucap, in_elems[i]);
    }

    if (un > 0)
    {
        if (laplace_spi_connect(&need_finish) != SPI_OK_CONNECT)
            elog(ERROR, "%s: SPI_connect failed", what);
        realize_ensure_name_plans();
    }

    input_render_count = un;
    input_rendered = realize_render_union(union_ids, un, what);

    arm_lemma.by_id = realize_make_id_htab("lexicalization empty", sizeof(ArmEntry), 32);
    arm_name.by_id  = realize_make_id_htab("has_name empty", sizeof(ArmEntry), 32);
    arm_lemma.cands = arm_name.cands = NULL;
    arm_lemma.n = arm_name.n = 0;
    arm_lemma.cap = arm_name.cap = 0;

    {
        Datum *resid = (Datum *) palloc(sizeof(Datum) * Max(1, n));
        int32  nresid = 0;

        for (int i = 0; i < n; i++)
        {
            IdKey       key;
            const char *val = NULL;
            bool        is_registry = false;

            if (in_nulls[i])
                continue;
            realize_id_key(&key, in_elems[i], "input");

            if (primary_val[i] != NULL)
            {
                hash128_t   id = datum_to_hash128(in_elems[i]);
                const char *canon = laplace_relation_canonical_for_type_id(&id);

                if (canon == NULL && laplace_entity_type_lookup(&id, &canon) != 0)
                    canon = NULL;
                if (canon != NULL)
                {
                    val = canon;
                    is_registry = true;
                }
            }
            if (val == NULL && primary_val[i] != NULL)
                val = primary_val[i]; /* codepoint glyph */
            if (val == NULL)
            {
                RenderEntry *re = (RenderEntry *) hash_search(render_ids, &key,
                                                              HASH_FIND, NULL);

                if (re != NULL && input_rendered[re->slot] != NULL)
                    val = input_rendered[re->slot];
            }

            if (mode >= 1 && val != NULL)
            {
                val = laplace_strip_canonical_label(val);
                if (val != NULL && val[0] == '\0')
                    val = NULL;
            }

            /* Only unresolved or opaque identifier surfaces need lexicalization / HAS_NAME. */
            if (!is_registry && (val == NULL || val[0] == '\0' || laplace_opaque_surface(val, true)))
                resid[nresid++] = in_elems[i];

            primary_val[i] = val;
        }

        if (nresid > 0)
        {
            ArrayType *resid_arr = construct_array(resid, nresid, BYTEAOID, -1,
                                                   false, TYPALIGN_INT);

            realize_run_arm(realize_plan_synset_lemma(), PointerGetDatum(resid_arr),
                            (Datum) 0, true, &arm_lemma, render_ids, &union_ids,
                            &un, &ucap, "lexicalization");
            {
                int32 next_n = 0;

                for (int32 r = 0; r < nresid; r++)
                {
                    IdKey rk;

                    realize_id_key(&rk, resid[r], "residual filter");
                    if (hash_search(arm_lemma.by_id, &rk, HASH_FIND, NULL) == NULL)
                        resid[next_n++] = resid[r];
                }
                nresid = next_n;
            }
            if (nresid > 0)
            {
                resid_arr = construct_array(resid, nresid, BYTEAOID, -1,
                                            false, TYPALIGN_INT);
                realize_run_arm(realize_plan_has_name(), PointerGetDatum(resid_arr),
                                (Datum) 0, true, &arm_name, render_ids, &union_ids,
                                &un, &ucap, "has_name");
            }
        }
    }

    rendered = input_rendered;
    if (un > input_render_count)
    {
        int32  additional_count = un - input_render_count;
        char **additional = realize_render_union(union_ids + input_render_count,
                                                 additional_count,
                                                 "render_batch fallback candidates");

        rendered = (char **) repalloc(rendered, sizeof(char *) * un);
        memcpy(rendered + input_render_count, additional,
               sizeof(char *) * additional_count);
        pfree(additional);
    }

    out = (Datum *) palloc0(sizeof(Datum) * n);
    out_nulls = (bool *) palloc(sizeof(bool) * n);
    for (int i = 0; i < n; i++)
    {
        const char *val;

        out_nulls[i] = true;
        if (in_nulls[i] && mode == 0)
            continue;
        if (in_nulls[i] && mode == 1)
            continue;

        val = in_nulls[i] ? NULL : primary_val[i];
        if (!in_nulls[i])
        {
            IdKey       key;
            const char *lemma_val;

            realize_id_key(&key, in_elems[i], "input");
            lemma_val = realize_first_nonempty(&arm_lemma, &key, rendered, render_ids);
            if (lemma_val != NULL)
            {
                if (mode >= 1)
                {
                    lemma_val = laplace_strip_canonical_label(lemma_val);
                    if (lemma_val != NULL && lemma_val[0] == '\0')
                        lemma_val = NULL;
                }
                if (lemma_val != NULL)
                    val = lemma_val;
            }
            if (val == NULL)
            {
                val = realize_first_nonempty(&arm_name, &key, rendered, render_ids);
                if (mode >= 1 && val != NULL)
                {
                    val = laplace_strip_canonical_label(val);
                    if (val != NULL && val[0] == '\0')
                        val = NULL;
                }
            }
        }
        if (mode == 2 && val == NULL)
            val = "unrealized entity";

        if (val != NULL)
        {
            MemoryContext old = MemoryContextSwitchTo(caller);

            out[i] = CStringGetTextDatum(val);
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
    return result;
}

Datum
pg_laplace_render_batch(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    PG_RETURN_ARRAYTYPE_P(
        render_or_label_batch_internal(PG_GETARG_ARRAYTYPE_P(0), 0, "render_batch"));
}

Datum
pg_laplace_label_batch(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    PG_RETURN_ARRAYTYPE_P(
        render_or_label_batch_internal(PG_GETARG_ARRAYTYPE_P(0), 1, "label_batch"));
}

Datum
pg_laplace_label_or_hex_batch(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    PG_RETURN_ARRAYTYPE_P(
        render_or_label_batch_internal(PG_GETARG_ARRAYTYPE_P(0), 2, "label_or_hex_batch"));
}

Datum
pg_laplace_render(PG_FUNCTION_ARGS)
{
    Datum      id_datum;
    ArrayType *in_arr;
    ArrayType *out_arr;
    Datum      res;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id_datum = PG_GETARG_DATUM(0);
    in_arr = construct_array(&id_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    out_arr = render_or_label_batch_internal(in_arr, 0, "render");
    res = realize_scalar_from_one_elem_batch(out_arr);
    if (res == (Datum) 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(res);
}

Datum
pg_laplace_label(PG_FUNCTION_ARGS)
{
    Datum      id_datum;
    ArrayType *in_arr;
    ArrayType *out_arr;
    Datum      res;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id_datum = PG_GETARG_DATUM(0);
    in_arr = construct_array(&id_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    out_arr = render_or_label_batch_internal(in_arr, 1, "label");
    res = realize_scalar_from_one_elem_batch(out_arr);
    if (res == (Datum) 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(res);
}

Datum
pg_laplace_label_or_hex(PG_FUNCTION_ARGS)
{
    Datum      id_datum;
    ArrayType *in_arr;
    ArrayType *out_arr;
    Datum      res;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id_datum = PG_GETARG_DATUM(0);
    in_arr = construct_array(&id_datum, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    out_arr = render_or_label_batch_internal(in_arr, 2, "label_or_hex");
    res = realize_scalar_from_one_elem_batch(out_arr);
    if (res == (Datum) 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(res);
}
