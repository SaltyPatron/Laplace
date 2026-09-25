#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "perfcache_native.h"
#include "laplace/core/vocabulary_table.h"

/*
 * The governed vocabularies, served from laplace_vocabulary_perfcache.bin: every value
 * is content (its id is the content id of its text; a feature value is the composition
 * [feature, value]) with the append-only code it holds in its vocabulary. These reads
 * never touch the entities table: a vocabulary value needs no row to have a code.
 */

static laplace_vocabulary_family_t
vocabulary_family(text *name)
{
    static const char *const names[LAPLACE_VOCABULARY_FAMILY_COUNT] = {
        "upos", "deprel", "deprel_subtype", "feature", "feature_value"};
    char *s = text_to_cstring(name);

    for (int f = 0; f < LAPLACE_VOCABULARY_FAMILY_COUNT; f++)
        if (strcmp(s, names[f]) == 0)
            return (laplace_vocabulary_family_t) f;
    ereport(ERROR,
            (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
             errmsg("undeclared vocabulary \"%s\"", s),
             errhint("Vocabularies: upos, deprel, deprel_subtype, feature, feature_value.")));
    return LAPLACE_VOCABULARY_FAMILY_COUNT;
}

static void
require_vocabulary(const char *caller)
{
    if (!laplace_vocabulary_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("%s: the vocabulary perfcache is not configured", caller),
                 errhint("ALTER SYSTEM SET laplace_substrate.vocabulary_perfcache_path = "
                         "'<laplace_vocabulary_perfcache.bin>'; SELECT pg_reload_conf();")));
}

PG_FUNCTION_INFO_V1(pg_laplace_vocabulary_ready);

/* () -> bool: the vocabulary perfcache is configured and mapped. */
Datum
pg_laplace_vocabulary_ready(PG_FUNCTION_ARGS)
{
    PG_RETURN_BOOL(laplace_vocabulary_ready());
}

PG_FUNCTION_INFO_V1(pg_laplace_vocabulary);

/* (vocabulary) -> (code, label, id, parent_code): the vocabulary in code order. */
Datum
pg_laplace_vocabulary(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    laplace_vocabulary_family_t family = vocabulary_family(PG_GETARG_TEXT_PP(0));
    uint32_t count;

    require_vocabulary("laplace.vocabulary");
    InitMaterializedSRF(fcinfo, 0);
    count = vocabulary_table_family_count(family);
    for (uint32_t code = 1; code <= count; code++)
    {
        const laplace_vocabulary_perfcache_record_t *r = vocabulary_table_lookup(family, (uint16_t) code);
        Datum values[4];
        bool nulls[4] = {false, false, false, false};

        if (r == NULL)
            continue;
        values[0] = Int32GetDatum((int32) r->code);
        values[1] = CStringGetTextDatum(vocabulary_table_label(r));
        values[2] = hash128_to_datum(&r->id);
        nulls[3] = r->parent_code == 0;
        values[3] = Int32GetDatum((int32) r->parent_code);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
    }
    return (Datum) 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_vocabulary_code);

/* (vocabulary, content id) -> code, NULL when the entity holds no code there. */
Datum
pg_laplace_vocabulary_code(PG_FUNCTION_ARGS)
{
    laplace_vocabulary_family_t family = vocabulary_family(PG_GETARG_TEXT_PP(0));
    hash128_t id = datum_to_hash128(PG_GETARG_DATUM(1));
    const laplace_vocabulary_perfcache_record_t *r;

    require_vocabulary("laplace.vocabulary_code");
    r = vocabulary_table_find(family, &id);
    if (r == NULL)
        PG_RETURN_NULL();
    PG_RETURN_INT32((int32) r->code);
}
