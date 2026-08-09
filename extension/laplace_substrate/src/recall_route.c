/* String helpers and the canonical intent vocabulary for the recall responders.
 *
 * This file used to hold a ~30-pattern English regex ladder (route_prompt_impl)
 * that guessed a read intent and a relation type from the surface form of a
 * prompt: "^what\s+does\s+(.+?)\s+mean", "^is\s+(?:a|an|the)\s+", "vs\.?",
 * "^tell\s+me\s+about", and so on. It was the one place where a substrate whose
 * identity law is content-addressed and language-agnostic could only be
 * questioned in English. Callers now pass the intent structurally — see
 * laplace.recall_intent() and laplace.query_shapes().
 */

#include "postgres.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgrprotos.h"

#include "recall_route.h"

void
route_free(RouteResult *r)
{
    if (r->intent) pfree(r->intent);
    if (r->phrase) pfree(r->phrase);
    if (r->phrase2) pfree(r->phrase2);
    if (r->type_name) pfree(r->type_name);
    r->intent = NULL;
    r->phrase = NULL;
    r->phrase2 = NULL;
    r->type_name = NULL;
}

char *
text_to_cstr(Datum d)
{
    return text_to_cstring(DatumGetTextPP(d));
}

char *
trim_dup(const char *s)
{
    return text_to_cstr(DirectFunctionCall1Coll(btrim1, DEFAULT_COLLATION_OID,
                                                CStringGetTextDatum(s)));
}

bool
str_empty(const char *s)
{
    return s == NULL || s[0] == '\0';
}

char *
lower_dup(const char *s)
{
    return text_to_cstr(DirectFunctionCall1Coll(lower, DEFAULT_COLLATION_OID,
                                                CStringGetTextDatum(s)));
}

/* The read shapes recall_intent() dispatches. Kept in one place so the C
 * dispatch, the SQL catalog (query_shapes) and any UI stay in parity. */
static const char *const route_intents[] = {
    "define",                   /* witnessed glosses, sense-disambiguated */
    "what_is",                  /* gloss + IS_A ladder upward */
    "describe",                 /* salient facts across the content bands */
    "synonyms",
    "translate",                /* cross-lingual surfaces via the ILI hub */
    "languages",                /* which languages witness this concept */
    "examples",
    "related",                  /* outgoing edges of one relation type */
    "related_in",               /* incoming edges of one relation type */
    "is_a",                     /* witnessed IS_A chain between two topics */
    "reason",                   /* how two topics relate, with verdict */
    "walk",                     /* greedy strongest-edge chain */
    "complete",                 /* COMPLETES_TO beam */
    "fallback",                 /* gloss, then walk */
};

bool
route_intent_known(const char *intent)
{
    if (intent == NULL)
        return false;
    for (size_t i = 0; i < lengthof(route_intents); i++)
        if (strcmp(intent, route_intents[i]) == 0)
            return true;
    return false;
}
