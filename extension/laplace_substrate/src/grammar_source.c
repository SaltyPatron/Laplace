#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "utils/builtins.h"

#include "laplace/core/grammar_compose.h"
#include "laplace/core/grammar_registry.h"
#include "perfcache_native.h"

PG_FUNCTION_INFO_V1(pg_laplace_grammar_source_id);

/* The SQL boundary transports bytes and the declared grammar. Parsing and
 * composition are the same native operation used by source admission. */
Datum
pg_laplace_grammar_source_id(PG_FUNCTION_ARGS)
{
    bytea *input = PG_GETARG_BYTEA_PP(0);
    char *modality = text_to_cstring(PG_GETARG_TEXT_PP(1));
    const TSLanguage *recipe = laplace_grammar_lookup_by_id(modality);
    laplace_ast_t *ast = NULL;
    laplace_compose_result_t *composition = NULL;
    hash128_t id;
    bytea *result;
    int rc;

    if (!recipe)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("unknown source grammar: %s", modality)));

    /* Source composition resolves every lexical codepoint through the same T0
     * table as managed ingestion. Never allow an unloaded backend to choose a
     * fallback or produce a process-dependent source root. */
    if (!laplace_perfcache_ready())
        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("grammar_source_id requires the T0 perfcache")));

    rc = laplace_grammar_parse((const uint8_t *) VARDATA_ANY(input),
                              VARSIZE_ANY_EXHDR(input), recipe, &ast);
    if (rc != 0 || !ast)
        PG_RETURN_NULL();

    rc = laplace_grammar_source_compose(
        (const uint8_t *) VARDATA_ANY(input), VARSIZE_ANY_EXHDR(input),
        ast, modality, &composition);
    laplace_ast_free(ast);
    if (rc != 0 || !composition)
    {
        if (composition) laplace_compose_result_free(composition);
        if (rc == -3)
            ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY),
                            errmsg("source grammar composition allocation failed")));
        PG_RETURN_NULL();
    }

    id = laplace_compose_root_id(composition);
    laplace_compose_result_free(composition);
    result = (bytea *) palloc(VARHDRSZ + sizeof(id));
    SET_VARSIZE(result, VARHDRSZ + sizeof(id));
    memcpy(VARDATA(result), &id, sizeof(id));
    PG_RETURN_BYTEA_P(result);
}
