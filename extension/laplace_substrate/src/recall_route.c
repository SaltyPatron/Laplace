/*
 * recall_route.c - the pure NLU intent router (no SPI): English prompt -> intent
 * + relation-type name. Split out of recall.c; route_prompt_impl/route_free and
 * the shared text helpers are exported via recall_route.h.
 */
#include "postgres.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/fmgrprotos.h"

#include "recall_route.h"
static void
route_clear(RouteResult *r)
{
    r->intent = NULL;
    r->phrase = NULL;
    r->phrase2 = NULL;
    r->type_name = NULL;
}

void
route_free(RouteResult *r)
{
    if (r->intent) pfree(r->intent);
    if (r->phrase) pfree(r->phrase);
    if (r->phrase2) pfree(r->phrase2);
    if (r->type_name) pfree(r->type_name);
    route_clear(r);
}

char *
text_to_cstr(Datum d)
{
    return text_to_cstring(DatumGetTextPP(d));
}

char *
trim_dup(const char *s)
{
    /* btrim1 is the 1-arg SQL btrim(text); the C symbol btrim is the 2-arg
     * (string, charset) version -- calling THAT with DirectFunctionCall1 made
     * it detoast stack garbage as its second argument (AV at entry; the
     * respond/converse never-worked crash, 2026-06-10). */
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

static bool
str_prefix_ci(const char *s, const char *pfx)
{
    char *l = lower_dup(s);
    char *p = lower_dup(pfx);
    bool  ok = (strncmp(l, p, strlen(p)) == 0);

    pfree(l);
    pfree(p);
    return ok;
}

static bool
str_in_ci(const char *s, const char *const *opts, int n)
{
    char *l = lower_dup(s);
    bool  ok = false;

    for (int i = 0; i < n; i++)
    {
        if (strcmp(l, opts[i]) == 0)
        {
            ok = true;
            break;
        }
    }
    pfree(l);
    return ok;
}

static int
regexp_groups(const char *str, const char *pat, const char *flags,
              char **g1, char **g2)
{
    Datum      result;
    ArrayType *arr;
    Datum     *elems;
    bool      *nulls;
    int        nelems;

    *g1 = NULL;
    *g2 = NULL;

    /* regexp_match needs a collation AND returns SQL NULL on no-match;
     * DirectFunctionCall* elogs on a NULL return, so invoke via fcinfo. */
    {
        LOCAL_FCINFO(fcinfo, 3);

        InitFunctionCallInfoData(*fcinfo, NULL, 3, DEFAULT_COLLATION_OID,
                                 NULL, NULL);
        fcinfo->args[0].value = CStringGetTextDatum(str);
        fcinfo->args[0].isnull = false;
        fcinfo->args[1].value = CStringGetTextDatum(pat);
        fcinfo->args[1].isnull = false;
        fcinfo->args[2].value = CStringGetTextDatum(flags);
        fcinfo->args[2].isnull = false;

        result = regexp_match(fcinfo);
        if (fcinfo->isnull)
            return 0;
    }

    arr = DatumGetArrayTypeP(result);
    deconstruct_array(arr, TEXTOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &nelems);
    if (nelems < 1 || nulls[0])
        return 0;

    *g1 = text_to_cstr(elems[0]);
    if (nelems >= 2 && !nulls[1])
        *g2 = text_to_cstr(elems[1]);
    return 1;
}

static bool
try_groups(const char *p, const char *pat, RouteResult *r,
           const char *intent, const char *type_name, bool need_g2)
{
    char *g1 = NULL;
    char *g2 = NULL;

    if (!regexp_groups(p, pat, "i", &g1, &g2))
        return false;
    if (need_g2 && g2 == NULL)
    {
        if (g1) pfree(g1);
        if (g2) pfree(g2);
        return false;
    }

    r->intent = pstrdup(intent);
    if (g1) r->phrase = g1;
    if (g2) r->phrase2 = g2;
    if (type_name) r->type_name = pstrdup(type_name);
    return true;
}

static bool
try_groups_alt(const char *p, const char *pat1, const char *pat2,
               RouteResult *r, const char *intent, const char *type_name)
{
    if (try_groups(p, pat1, r, intent, type_name, false))
        return true;
    if (pat2 == NULL)
        return false;
    return try_groups(p, pat2, r, intent, type_name, false);
}

static void
set_possessive_intent(const char *cap, RouteResult *r)
{
    char *lc = lower_dup(cap);

    {
        const char *def_meaning[] = { "definition", "meaning" };
        if (str_in_ci(lc, def_meaning, 2))
            r->intent = pstrdup("define");
        else if (str_prefix_ci(lc, "synonym"))
            r->intent = pstrdup("synonyms");
        else if (str_prefix_ci(lc, "translation"))
            r->intent = pstrdup("translate");
        else if (str_prefix_ci(lc, "example"))
            r->intent = pstrdup("examples");
        else if (str_prefix_ci(lc, "cause"))
        {
            r->intent = pstrdup("related_in");
            r->type_name = pstrdup("CAUSES");
        }
        else
            r->intent = pstrdup("related");
    }

    if (str_prefix_ci(lc, "antonym"))
        r->type_name = pstrdup("IS_ANTONYM_OF");
    else if (str_prefix_ci(lc, "part"))
        r->type_name = pstrdup("HAS_PART");
    else if (str_prefix_ci(lc, "cause") && r->type_name == NULL)
        r->type_name = pstrdup("CAUSES");
    else if (str_prefix_ci(lc, "use"))
        r->type_name = pstrdup("USED_FOR");

    pfree(lc);
}

void
route_prompt_impl(const char *prompt, RouteResult *r)
{
    char *p;
    char *g1 = NULL;
    char *g2 = NULL;

    route_clear(r);
    p = trim_dup(prompt);

    if (try_groups_alt(p,
                       "^what\\s+does\\s+(.+?)\\s+mean\\y",
                       "^(?:define|meaning\\s+of)\\s+(.+)$",
                       r, "define", NULL))
        goto done;

    if (try_groups_alt(p,
                       "^what\\s+(?:is|are)\\s+(?:(?:a|an|the)\\s+)?(.+?)\\s+used\\s+for\\??$",
                       "^uses?\\s+of\\s+(.+?)\\??$",
                       r, "related", "USED_FOR"))
        goto done;

    g1 = NULL;
    g2 = NULL;
    if (regexp_groups(p,
                          "^how\\s+(?:are|is)\\s+(.+?)\\s+(?:and|&)\\s+(.+?)\\s+related\\??$",
                          "i", &g1, &g2) ||
        (!g1 && regexp_groups(p,
                                  "^how\\s+(?:is|are)\\s+(.+?)\\s+related\\s+to\\s+(.+?)\\??$",
                                  "i", &g1, &g2)) ||
        (!g1 && regexp_groups(p,
                                  "^(?:relate|relation\\s+(?:between|of))\\s+(.+?)\\s+(?:and|to|&)\\s+(.+?)\\??$",
                                  "i", &g1, &g2)) ||
        (!g1 && regexp_groups(p, "^(.+?)\\s+vs\\.?\\s+(.+?)\\??$", "i", &g1, &g2)))
    {
        r->intent = pstrdup("reason");
        r->phrase = g1;
        r->phrase2 = g2;
        goto done;
    }

    if (try_groups(p, "^(?:is|are)\\s+(?:(?:a|an|the)\\s+)?(.+?)\\s+(?:a|an|the)\\s+(.+?)\\??$",
                   r, "is_a", NULL, true))
        goto done;

    if (try_groups(p, "^what(?:'s|\\s+is|\\s+are)\\s+(?:(?:a|an|the)\\s+)?(.+?)\\??$",
                   r, "what_is", NULL, false))
        goto done;

    if (try_groups(p, "^(?:tell\\s+me\\s+about|describe|about)\\s+(.+?)\\??$",
                   r, "describe", NULL, false))
        goto done;

    if (try_groups(p, "^translate\\s+(.+?)(?:\\s+(?:in|to|into)\\s+.+)?$",
                   r, "translate", NULL, false))
        goto done;

    if (try_groups(p, "^(?:walk|continue|free[- ]?associate(?:\\s+from)?)\\s+(.+?)\\??$",
                   r, "walk", NULL, false))
        goto done;

    if (try_groups(p, "^complete\\s+(.+?)\\??$", r, "complete", NULL, false))
        goto done;

    if (try_groups(p,
                   "^(?:what\\s+(?:word\\s+|comes?\\s+)?(?:comes?\\s+)?after|what\\s+follows?|after|follows?)\\s+(.+?)\\??$",
                   r, "related", "PRECEDES", false))
        goto done;

    if (try_groups(p,
                   "^(?:what\\s+(?:word\\s+|comes?\\s+)?(?:comes?\\s+)?before|what\\s+precedes?|before|precedes?)\\s+(.+?)\\??$",
                   r, "related", "FOLLOWS", false))
        goto done;

    g1 = NULL;
    if (regexp_groups(p,
                          "^(?:(?:and|what\\s+about)\\s+)?(?:its|their|his|her)\\s+(definition|meaning|synonyms?|antonyms?|translations?|examples?|parts?|causes?|uses?)\\??$",
                          "i", &g1, &g2))
    {
        set_possessive_intent(g1, r);
        pfree(g1);
        goto done;
    }

    if (try_groups(p, "^(?:what\\s+about|and)\\s+(?:it|that|this|them|those)\\??$",
                   r, "describe", NULL, false))
        goto done;

    if (try_groups(p, "^antonyms?\\s+(?:of|for)?\\s*(.+?)\\??$",
                   r, "related", "IS_ANTONYM_OF", false))
        goto done;

    if (try_groups(p, "^parts?\\s+of\\s+(.+?)\\??$", r, "related", "HAS_PART", false))
        goto done;

    if (try_groups_alt(p,
                       "^(?:what\\s+causes|causes?\\s+of)\\s+(.+?)\\??$",
                       NULL,
                       r, "related_in", "CAUSES"))
        goto done;

    if (try_groups(p, "^what\\s+does\\s+(.+?)\\s+cause\\??$", r, "related", "CAUSES", false))
        goto done;

    if (try_groups(p, "synonyms?\\s+(?:of|for)?\\s*(.+)$", r, "synonyms", NULL, false))
        goto done;

    if (try_groups(p, "examples?\\s+(?:of|for)?\\s*(.+)$", r, "examples", NULL, false))
        goto done;

    if (try_groups(p, "^how\\s+(?:do\\s+(?:i|you|we)|to)\\s+(.+?)(?:\\s+in\\s+\\w+)?$",
                   r, "related", "HAS_EXAMPLE", false))
        goto done;

    if (try_groups(p,
                   "^(?:show\\s+(?:me\\s+)?(?:an?\\s+)?)?(?:code\\s+)?example\\s+(?:of|for)\\s+(.+?)$",
                   r, "related", "HAS_EXAMPLE", false))
        goto done;

    if (try_groups(p,
                   "^(?:write|generate|implement|create)\\s+(?:(?:a|an|some|the)\\s+)?(.+?)(?:\\s+(?:function|method|script|snippet|code|program|class))?$",
                   r, "related", "HAS_EXAMPLE", false))
        goto done;

    r->intent = pstrdup("fallback");
    r->phrase = pstrdup(p);

done:
    pfree(p);
}

