#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/fmgrprotos.h"
#include "utils/memutils.h"

PG_FUNCTION_INFO_V1(pg_laplace_route_prompt);
PG_FUNCTION_INFO_V1(pg_laplace_respond);
PG_FUNCTION_INFO_V1(pg_laplace_converse);

typedef struct RouteResult
{
    char *intent;
    char *phrase;
    char *phrase2;
    char *type_name;
} RouteResult;

static void
route_clear(RouteResult *r)
{
    r->intent = NULL;
    r->phrase = NULL;
    r->phrase2 = NULL;
    r->type_name = NULL;
}

static void
route_free(RouteResult *r)
{
    if (r->intent) pfree(r->intent);
    if (r->phrase) pfree(r->phrase);
    if (r->phrase2) pfree(r->phrase2);
    if (r->type_name) pfree(r->type_name);
    route_clear(r);
}

static char *
text_to_cstr(Datum d)
{
    return text_to_cstring(DatumGetTextPP(d));
}

static char *
trim_dup(const char *s)
{
    return text_to_cstr(DirectFunctionCall1(btrim, CStringGetTextDatum(s)));
}

static bool
str_empty(const char *s)
{
    return s == NULL || s[0] == '\0';
}

static char *
lower_dup(const char *s)
{
    return text_to_cstr(DirectFunctionCall1(lower, CStringGetTextDatum(s)));
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
spi_regexp_groups(const char *str, const char *pat, const char *flags,
                  char **g1, char **g2)
{
    Oid   argtypes[3] = { TEXTOID, TEXTOID, TEXTOID };
    Datum args[3];
    int   rc;
    bool  isnull;

    *g1 = NULL;
    *g2 = NULL;

    args[0] = CStringGetTextDatum(str);
    args[1] = CStringGetTextDatum(pat);
    args[2] = CStringGetTextDatum(flags);

    rc = SPI_execute_with_args(
        "SELECT (regexp_match($1, $2, $3))[1], (regexp_match($1, $2, $3))[2]",
        3, argtypes, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return 0;

    {
        Datum d1 = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
            *g1 = text_to_cstr(d1);
    }
    {
        Datum d2 = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
        if (!isnull)
            *g2 = text_to_cstr(d2);
    }
    return (*g1 != NULL) ? 1 : 0;
}

static bool
try_groups(const char *p, const char *pat, RouteResult *r,
           const char *intent, const char *type_name, bool need_g2)
{
    char *g1 = NULL;
    char *g2 = NULL;

    if (!spi_regexp_groups(p, pat, "i", &g1, &g2))
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

static void
route_prompt_impl(const char *prompt, RouteResult *r)
{
    char *p = trim_dup(prompt);
    char *g1 = NULL;
    char *g2 = NULL;

    route_clear(r);

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
    if (spi_regexp_groups(p,
                          "^how\\s+(?:are|is)\\s+(.+?)\\s+(?:and|&)\\s+(.+?)\\s+related\\??$",
                          "i", &g1, &g2) ||
        (!g1 && spi_regexp_groups(p,
                                  "^how\\s+(?:is|are)\\s+(.+?)\\s+related\\s+to\\s+(.+?)\\??$",
                                  "i", &g1, &g2)) ||
        (!g1 && spi_regexp_groups(p,
                                  "^(?:relate|relation\\s+(?:between|of))\\s+(.+?)\\s+(?:and|to|&)\\s+(.+?)\\??$",
                                  "i", &g1, &g2)) ||
        (!g1 && spi_regexp_groups(p, "^(.+?)\\s+vs\\.?\\s+(.+?)\\??$", "i", &g1, &g2)))
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
    if (spi_regexp_groups(p,
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

static void
emit_route_row(ReturnSetInfo *rsinfo, const RouteResult *r)
{
    Datum values[4];
    bool  nulls[4] = { false, false, false, false };

    if (!r->intent) nulls[0] = true;
    else values[0] = CStringGetTextDatum(r->intent);
    if (!r->phrase) nulls[1] = true;
    else values[1] = CStringGetTextDatum(r->phrase);
    if (!r->phrase2) nulls[2] = true;
    else values[2] = CStringGetTextDatum(r->phrase2);
    if (!r->type_name) nulls[3] = true;
    else values[3] = CStringGetTextDatum(r->type_name);

    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
}

static void
emit_reply_row(ReturnSetInfo *rsinfo, Datum reply, Datum mu, Datum witnesses,
               bool mu_null, bool wit_null)
{
    Datum values[3];
    bool  nulls[3] = { false, mu_null, wit_null };

    values[0] = reply;
    values[1] = mu;
    values[2] = witnesses;
    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
}

static int
spi_forward_replies(ReturnSetInfo *rsinfo, const char *query,
                    int nparams, Oid *types, Datum *values, const char *nulls)
{
    int rc = SPI_execute_with_args(query, nparams, types, values, nulls, true, 0);

    if (rc != SPI_OK_SELECT)
        elog(ERROR, "respond_native: query failed: %s", SPI_result_code_string(rc));

    for (uint64 i = 0; i < SPI_processed; i++)
    {
        HeapTuple tup = SPI_tuptable->vals[i];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      n1, n2;
        Datum     v0, v1, v2;

        bool n0;

        v0 = SPI_getbinval(tup, td, 1, &n0);
        v1 = SPI_getbinval(tup, td, 2, &n1);
        v2 = SPI_getbinval(tup, td, 3, &n2);
        emit_reply_row(rsinfo, v0, v1, v2, n1, n2);
        (void) n0;
    }
    return (int) SPI_processed;
}

static Datum
spi_resolve_topic(const char *phrase, Datum context, bool ctx_null)
{
    Oid   types[2] = { TEXTOID, BYTEAOID };
    Datum args[2];
    char  nulls[3] = " n";
    bool  isnull;
    int   rc;

    if (phrase)
        args[0] = CStringGetTextDatum(phrase);
    else
        nulls[0] = 'n';
    args[1] = context;
    if (ctx_null)
        nulls[1] = 'n';

    rc = SPI_execute_with_args(
        "SELECT laplace.resolve_topic($1, $2)",
        2, types, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static Datum
spi_label(Datum topic)
{
    Oid   types[1] = { BYTEAOID };
    Datum args[1] = { topic };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.label($1)", 1, types, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static void
emit_no_topic_msg(ReturnSetInfo *rsinfo, const char *prompt,
                  const RouteResult *route)
{
    char msg[512];

    if (route->intent && strcmp(route->intent, "fallback") == 0)
        snprintf(msg, sizeof(msg),
                 "I hold no consensus about any word of \"%s\" yet.", prompt);
    else
        snprintf(msg, sizeof(msg),
                 "I hold no consensus about \"%s\" yet.",
                 route->phrase ? route->phrase : prompt);
    emit_reply_row(rsinfo, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, true, true);
}

static void
emit_label_fallback(ReturnSetInfo *rsinfo, Datum topic, const char *suffix)
{
    Datum lbl = spi_label(topic);
    char  msg[384];
    const char *name = (lbl != (Datum) 0) ? text_to_cstr(lbl) : "?";

    snprintf(msg, sizeof(msg), "I hold \"%s\" but %s", name, suffix);
    emit_reply_row(rsinfo, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, true, true);
}

static void
emit_type_miss(ReturnSetInfo *rsinfo, Datum topic,
               const char *type_name, bool incoming)
{
    Datum lbl = spi_label(topic);
    char  msg[384];
    char *rel = lower_dup(type_name ? type_name : "relation");
    const char *name = (lbl != (Datum) 0) ? text_to_cstr(lbl) : "?";

    for (char *c = rel; *c; c++)
        if (*c == '_') *c = ' ';
    snprintf(msg, sizeof(msg), "I hold \"%s\" but no %s%s consensus yet.",
             name, incoming ? "incoming " : "", rel);
    pfree(rel);
    emit_reply_row(rsinfo, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, true, true);
}

static Datum
spi_word_language(Datum topic)
{
    Oid   types[1] = { BYTEAOID };
    Datum args[1] = { topic };
    bool  isnull;
    int   rc;

    rc = SPI_execute_with_args(
        "SELECT laplace.word_language($1)", 1, types, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static void
respond_is_a(ReturnSetInfo *rsinfo, Datum topic, const RouteResult *route)
{
    Datum topic2 = spi_resolve_topic(route->phrase2, (Datum) 0, true);

    if (topic2 == (Datum) 0)
    {
        char msg[256];
        snprintf(msg, sizeof(msg), "I hold no consensus about \"%s\" yet.",
                 route->phrase2 ? route->phrase2 : "?");
        emit_reply_row(rsinfo, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, true, true);
        return;
    }

    {
        Oid   types[2] = { BYTEAOID, BYTEAOID };
        Datum args[2] = { topic, topic2 };
        bool  path_null, mu_null;
        int   rc = SPI_execute_with_args(
            "SELECT ip.path, ip.types, ip.path_mu "
            "FROM laplace.isa_path($1, $2) ip LIMIT 1",
            2, types, args, NULL, true, 1);

        if (rc == SPI_OK_SELECT && SPI_processed > 0)
        {
            bool  types_null;
            Datum path = SPI_getbinval(SPI_tuptable->vals[0],
                                       SPI_tuptable->tupdesc, 1, &path_null);
            Datum types_a = SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 2, &types_null);
            Datum mu = SPI_getbinval(SPI_tuptable->vals[0],
                                     SPI_tuptable->tupdesc, 3, &mu_null);

            if (!path_null && !types_null)
            {
                Oid   rtypes[3] = { BYTEAARRAYOID, BYTEAARRAYOID, BYTEAOID };
                Datum rargs[3];
                char  rnulls[4] = "  n";
                Datum lang = spi_word_language(topic);
                bool  cn;
                int   crc;

                rargs[0] = path;
                rargs[1] = types_a;
                rargs[2] = lang;
                if (lang == (Datum) 0)
                    rnulls[2] = 'n';

                crc = SPI_execute_with_args(
                    "SELECT 'Yes — ' || laplace.realize_path($1, $2, $3)",
                    3, rtypes, rargs, rnulls, true, 1);
                if (crc == SPI_OK_SELECT && SPI_processed > 0)
                {
                    Datum reply = SPI_getbinval(SPI_tuptable->vals[0],
                                                SPI_tuptable->tupdesc, 1, &cn);
                    emit_reply_row(rsinfo, reply, mu, (Datum) 0, mu_null, true);
                }
                return;
            }
        }

        {
            Oid   rtypes[3] = { BYTEAOID, BYTEAOID, BYTEAOID };
            Datum rargs[3];
            char  rnulls[4] = "  n";
            Datum lang = spi_word_language(topic);
            int   crc;

            rargs[0] = topic;
            rargs[1] = topic2;
            rargs[2] = lang;
            if (lang == (Datum) 0)
                rnulls[2] = 'n';

            crc = SPI_execute_with_args(
                "SELECT 'No witnessed IS_A path from \"' "
                "       || COALESCE(laplace.realize($1, $3), laplace.label($1)) "
                "       || '\" to \"' "
                "       || COALESCE(laplace.realize($2, $3), laplace.label($2)) || '\".'",
                3, rtypes, rargs, rnulls, true, 1);
            if (crc == SPI_OK_SELECT && SPI_processed > 0)
            {
                bool cn;
                Datum reply = SPI_getbinval(SPI_tuptable->vals[0],
                                            SPI_tuptable->tupdesc, 1, &cn);
                emit_reply_row(rsinfo, reply, (Datum) 0, (Datum) 0, true, true);
            }
        }
    }
}

static void
respond_impl(const char *prompt, Datum context, bool ctx_null, ReturnSetInfo *rsinfo)
{
    RouteResult route;
    Datum       topic;
    int         n;

    route_prompt_impl(prompt, &route);
    topic = spi_resolve_topic(route.phrase, context, ctx_null);

    if (topic == (Datum) 0)
    {
        emit_no_topic_msg(rsinfo, prompt, &route);
        route_free(&route);
        return;
    }

    if (route.intent && strcmp(route.intent, "define") == 0)
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(rsinfo,
            "SELECT d.definition, d.eff_mu, d.witnesses "
            "FROM laplace.define($2, "
            "  COALESCE((SELECT array_agg(ps.id) FROM laplace.prompt_state($1) ps "
            "            WHERE ps.id <> $2), ARRAY[]::bytea[])) d",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no glosses have been witnessed yet.");
    }
    else if (route.intent && strcmp(route.intent, "what_is") == 0)
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(rsinfo,
            "SELECT laplace.label($2) || ': ' || d.definition, d.eff_mu, d.witnesses "
            "FROM laplace.define($2, "
            "  COALESCE((SELECT array_agg(ps.id) FROM laplace.prompt_state($1) ps "
            "            WHERE ps.id <> $2), ARRAY[]::bytea[]), 3) d "
            "UNION ALL "
            "SELECT repeat('  ', h.depth) || E'\\u2192 is ' || COALESCE(h.hypernym, '?') "
            "       || COALESCE(': ' || h.gloss, ''), NULL::numeric, NULL::bigint "
            "FROM laplace.hypernyms($2, 6) h WHERE h.depth > 0",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no sense consensus has been witnessed yet.");
    }
    else if (route.intent && strcmp(route.intent, "translate") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(rsinfo,
            "SELECT t.translation || ' [' || COALESCE(t.language, '?') || ']', "
            "       t.eff_mu, t.witnesses FROM laplace.translations($1) t",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no translation consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "synonyms") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(rsinfo,
            "SELECT s.synonym, s.eff_mu, s.witnesses FROM laplace.synonyms($1) s",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no synonym consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "examples") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(rsinfo,
            "SELECT e.example, e.eff_mu, e.witnesses FROM laplace.examples($1) e",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no example consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "describe") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(rsinfo,
            "SELECT d.type || ': ' || COALESCE(d.fact, '?'), d.eff_mu, d.witnesses "
            "FROM laplace.describe($1, laplace.word_language($1)) d",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no relation consensus to describe yet.");
    }
    else if (route.intent && strcmp(route.intent, "related") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic,
                          CStringGetTextDatum(route.type_name ? route.type_name : "") };
        n = spi_forward_replies(rsinfo,
            "SELECT f.fact, f.eff_mu, f.witnesses "
            "FROM laplace.related($1, laplace.relation_type_id($2), laplace.word_language($1)) f",
            2, types, args, NULL);
        if (n == 0)
            emit_type_miss(rsinfo, topic, route.type_name, false);
    }
    else if (route.intent && strcmp(route.intent, "related_in") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic,
                          CStringGetTextDatum(route.type_name ? route.type_name : "") };
        n = spi_forward_replies(rsinfo,
            "SELECT f.fact, f.eff_mu, f.witnesses "
            "FROM laplace.related_in($1, laplace.relation_type_id($2), laplace.word_language($1)) f",
            2, types, args, NULL);
        if (n == 0)
            emit_type_miss(rsinfo, topic, route.type_name, true);
    }
    else if (route.intent && strcmp(route.intent, "is_a") == 0)
    {
        respond_is_a(rsinfo, topic, &route);
    }
    else if (route.intent && strcmp(route.intent, "reason") == 0)
    {
        Datum topic2 = spi_resolve_topic(route.phrase2, (Datum) 0, true);
        if (topic2 == (Datum) 0)
        {
            char msg[256];
            snprintf(msg, sizeof(msg), "I hold no consensus about \"%s\" yet.",
                     route.phrase2 ? route.phrase2 : "?");
            emit_reply_row(rsinfo, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, true, true);
        }
        else
        {
            Oid   types[2] = { BYTEAOID, BYTEAOID };
            Datum args[2] = { topic, topic2 };
            spi_forward_replies(rsinfo,
                "SELECT CASE WHEN rs.relation IS NOT NULL "
                "            THEN rs.relation || '  [' || rs.verdict || ']' "
                "            ELSE rs.verdict END, rs.mu, rs.usage "
                "FROM laplace.relatedness($1, $2) rs",
                2, types, args, NULL);
        }
    }
    else if (route.intent &&
             (strcmp(route.intent, "walk") == 0 || strcmp(route.intent, "complete") == 0))
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic, CStringGetTextDatum(route.intent) };
        n = spi_forward_replies(rsinfo,
            "SELECT COALESCE(laplace.realize($1, laplace.word_language($1)), laplace.label($1)) "
            "       || string_agg(' —' || COALESCE(laplace.type_label(g.type_id), '?') || E'\\u2192 ' "
            "                     || COALESCE(laplace.realize(g.entity_id, laplace.word_language($1)), "
            "                                 laplace.label(g.entity_id)), '' ORDER BY g.step), "
            "       min(g.eff_mu), NULL::bigint "
            "FROM laplace.generate_greedy($1, "
            "     CASE WHEN $2 = 'complete' THEN laplace.relation_type_id('COMPLETES_TO') END, 8) g "
            "HAVING count(*) > 0",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no outgoing consensus to walk yet.");
    }
    else
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(rsinfo,
            "SELECT laplace.label($2) || ': ' || d.definition, d.eff_mu, d.witnesses "
            "FROM laplace.define($2, "
            "  COALESCE((SELECT array_agg(ps.id) FROM laplace.prompt_state($1) ps "
            "            WHERE ps.id <> $2), ARRAY[]::bytea[]), 3) d",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(rsinfo, topic, "no glosses have been witnessed yet.");
    }

    route_free(&route);
}

Datum
pg_laplace_route_prompt(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt_txt;
    RouteResult    route;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("route_prompt: p_prompt must not be NULL")));
    prompt_txt = PG_GETARG_TEXT_PP(0);

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "route_prompt: SPI_connect failed");

    route_prompt_impl(text_to_cstring(prompt_txt), &route);
    emit_route_row(rsinfo, &route);
    route_free(&route);

    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_respond(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt_txt;
    char          *prompt;
    Datum          context = (Datum) 0;
    bool           ctx_null = true;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt_txt = PG_GETARG_TEXT_PP(0);
    prompt = trim_dup(text_to_cstring(prompt_txt));
    if (str_empty(prompt))
    {
        pfree(prompt);
        PG_RETURN_NULL();
    }

    if (!PG_ARGISNULL(1))
    {
        context = PG_GETARG_DATUM(1);
        ctx_null = false;
    }

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "respond: SPI_connect failed");

    respond_impl(prompt, context, ctx_null, rsinfo);

    pfree(prompt);
    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_converse(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt_txt;
    char          *prompt;
    Datum          session;
    Datum          context = (Datum) 0;
    bool           ctx_null = true;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    prompt_txt = PG_GETARG_TEXT_PP(0);
    prompt = trim_dup(text_to_cstring(prompt_txt));
    if (str_empty(prompt))
    {
        pfree(prompt);
        PG_RETURN_NULL();
    }

    if (PG_ARGISNULL(1))
    {
        char pidbuf[32];
        snprintf(pidbuf, sizeof(pidbuf), "%d", MyProcPid);
        session = DirectFunctionCall2(pg_convert_to,
                                      CStringGetTextDatum(pidbuf),
                                      CStringGetTextDatum("UTF8"));
    }
    else
        session = PG_GETARG_DATUM(1);

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "converse: SPI_connect failed");

    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { session };
        bool  isnull;
        int   rc = SPI_execute_with_args(
            "SELECT t.resolved_id FROM laplace.converse_turns t "
            "WHERE t.session_id = $1 AND t.resolved_id IS NOT NULL "
            "ORDER BY t.ord DESC LIMIT 1",
            1, types, args, NULL, true, 1);
        if (rc == SPI_OK_SELECT && SPI_processed > 0)
        {
            context = SPI_getbinval(SPI_tuptable->vals[0],
                                    SPI_tuptable->tupdesc, 1, &isnull);
            if (!isnull)
                ctx_null = false;
        }
    }

    {
        RouteResult route;
        Datum       topic;
        Oid         itypes[3] = { BYTEAOID, TEXTOID, BYTEAOID };
        Datum       iargs[3];

        route_prompt_impl(prompt, &route);
        topic = spi_resolve_topic(route.phrase, context, ctx_null);
        route_free(&route);

        iargs[0] = session;
        iargs[1] = CStringGetTextDatum(prompt);
        iargs[2] = topic;
        SPI_execute_with_args(
            "INSERT INTO laplace.converse_turns (session_id, ord, prompt, resolved_id) "
            "SELECT $1, COALESCE(max(t.ord), 0) + 1, $2, $3 "
            "FROM laplace.converse_turns t WHERE t.session_id = $1",
            3, itypes, iargs, "  n", false, 0);
    }

    respond_impl(prompt, context, ctx_null, rsinfo);

    pfree(prompt);
    SPI_finish();
    return (Datum) 0;
}
