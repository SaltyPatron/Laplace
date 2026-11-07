#include "postgres.h"
#include "common/int.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/fmgrprotos.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "laplace/core/math4d.h"

#include "recall_internal.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "recall_route.h"
#include "consensus_scan.h"
#include "laplace/core/sql_catalog.h"

PG_FUNCTION_INFO_V1(pg_laplace_recall_intent);
PG_FUNCTION_INFO_V1(pg_laplace_recall);
PG_FUNCTION_INFO_V1(pg_laplace_recall_session);

void
reply_buf_init(ReplyBuf *buf)
{
    buf->cxt = CurrentMemoryContext;
    buf->rows = NULL;
    buf->n = 0;
    buf->cap = 0;
}

void
reply_buf_add(ReplyBuf *buf, Datum reply, Datum mu, Datum witnesses,
              bool reply_null, bool mu_null, bool wit_null)
{
    MemoryContext old = MemoryContextSwitchTo(buf->cxt);

    if (buf->n >= buf->cap)
    {
        buf->cap = buf->cap < 8 ? 8 : buf->cap * 2;
        buf->rows = buf->rows
            ? repalloc(buf->rows, buf->cap * sizeof(ReplyRow))
            : palloc(buf->cap * sizeof(ReplyRow));
    }

    {
        ReplyRow *row = &buf->rows[buf->n++];

        row->reply_null = reply_null;
        row->mu_null = mu_null;
        row->wit_null = wit_null;
        row->reply = reply_null ? (Datum) 0 : datumCopy(reply, false, -1);
        row->mu = mu_null ? (Datum) 0 : datumCopy(mu, false, -1);
        row->witnesses = witnesses;
    }
    MemoryContextSwitchTo(old);
}

void
emit_reply_row(ReturnSetInfo *rsinfo, Datum reply, Datum mu, Datum witnesses,
               bool reply_null, bool mu_null, bool wit_null)
{
    Datum values[3];
    bool  nulls[3] = { reply_null, mu_null, wit_null };

    values[0] = reply;
    values[1] = mu;
    values[2] = witnesses;
    tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
}

void
reply_buf_emit(ReturnSetInfo *rsinfo, ReplyBuf *buf)
{
    for (int i = 0; i < buf->n; i++)
    {
        ReplyRow *row = &buf->rows[i];

        emit_reply_row(rsinfo, row->reply, row->mu, row->witnesses,
                       row->reply_null, row->mu_null, row->wit_null);
    }
}

int
spi_forward_replies(ReplyBuf *buf, const char *query,
                    int nparams, Oid *types, Datum *values, const char *nulls)
{
    int rc = SPI_execute_with_args(query, nparams, types, values, nulls, true, 0);

    if (rc != SPI_OK_SELECT)
        elog(ERROR, "recall: query failed: %s", SPI_result_code_string(rc));

    for (uint64 i = 0; i < SPI_processed; i++)
    {
        HeapTuple tup = SPI_tuptable->vals[i];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      n0, n1, n2;
        Datum     v0, v1, v2;

        v0 = SPI_getbinval(tup, td, 1, &n0);
        v1 = SPI_getbinval(tup, td, 2, &n1);
        v2 = SPI_getbinval(tup, td, 3, &n2);
        reply_buf_add(buf, v0, v1, v2, n0, n1, n2);
    }
    return (int) SPI_processed;
}

static Datum
spi_resolve_topic(const char *phrase, Datum context, bool ctx_null)
{
    Oid   types[2] = { TEXTOID, BYTEAOID };
    Datum args[2];
    char  nulls[3] = "  ";
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
        "SELECT converse.resolve_topic($1, $2)",
        2, types, args, nulls, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    return SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
}

static void
emit_no_topic_msg(ReplyBuf *buf, const char *prompt, const RouteResult *route)
{
    char msg[512];

    if (route->intent && strcmp(route->intent, "fallback") == 0)
        snprintf(msg, sizeof(msg),
                 "I hold no consensus about any word of \"%s\" yet.", prompt);
    else
        snprintf(msg, sizeof(msg),
                 "I hold no consensus about \"%s\" yet.",
                 route->phrase ? route->phrase : prompt);
    reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, false, true, true);
}

/* Sense context for the gloss reads: the observation's other resolved ids,
 * excluding the topic (converse.recall_context_exclude). Pure id resolution;
 * no grammar is consulted. */
static Datum
spi_context_ids(const char *prompt, Datum topic)
{
    Oid   types[2] = { TEXTOID, BYTEAOID };
    Datum args[2];
    bool  isnull;
    int   rc;

    if (prompt == NULL || topic == (Datum) 0)
        return (Datum) 0;

    args[0] = CStringGetTextDatum(prompt);
    args[1] = topic;
    rc = SPI_execute_with_args(
        "SELECT converse.recall_context_exclude($1, $2)",
        2, types, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return (Datum) 0;
    {
        Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        return isnull ? (Datum) 0 : d;
    }
}

static void
emit_label_fallback(ReplyBuf *buf, Datum topic, const char *suffix)
{
    Datum lbl = spi_label(topic);
    char  msg[384];
    const char *name = (lbl != (Datum) 0) ? text_to_cstr(lbl) : "?";

    snprintf(msg, sizeof(msg), "I hold \"%s\" but %s", name, suffix);
    reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, false, true, true);
}

static void
emit_type_miss(ReplyBuf *buf, Datum topic, const char *type_name, bool incoming)
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
    reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, false, true, true);
}

static void
respond_is_a(ReplyBuf *buf, Datum topic, Datum topic2)
{
    if (topic2 == (Datum) 0)
    {
        reply_buf_add(buf,
                      CStringGetTextDatum("is_a needs a second topic; none resolved."),
                      (Datum) 0, (Datum) 0, false, true, true);
        return;
    }

    {
        Oid   types[2] = { BYTEAOID, BYTEAOID };
        Datum args[2] = { topic, topic2 };
        bool  path_null, mu_null;
        int   rc = SPI_execute_with_args(
            "SELECT ip.path, ip.types, ip.path_mu "
            "FROM converse.isa_path($1, $2) ip LIMIT 1",
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
                char  rnulls[4] = "   ";
                Datum lang = spi_word_language(topic);
                bool  cn;
                int   crc;

                rargs[0] = path;
                rargs[1] = types_a;
                rargs[2] = lang;
                if (lang == (Datum) 0)
                    rnulls[2] = 'n';

                crc = SPI_execute_with_args(
                    "SELECT converse.recall_is_a_yes_reply($1, $2, $3)",
                    3, rtypes, rargs, rnulls, true, 1);
                if (crc == SPI_OK_SELECT && SPI_processed > 0)
                {
                    Datum reply = SPI_getbinval(SPI_tuptable->vals[0],
                                                SPI_tuptable->tupdesc, 1, &cn);
                    reply_buf_add(buf, reply, mu, (Datum) 0, cn, mu_null, true);
                }
                return;
            }
        }

        {
            Oid   rtypes[3] = { BYTEAOID, BYTEAOID, BYTEAOID };
            Datum rargs[3];
            char  rnulls[4] = "   ";
            Datum lang = spi_word_language(topic);
            int   crc;

            rargs[0] = topic;
            rargs[1] = topic2;
            rargs[2] = lang;
            if (lang == (Datum) 0)
                rnulls[2] = 'n';

            crc = SPI_execute_with_args(
                "SELECT converse.recall_is_a_no_reply($1, $2, $3)",
                3, rtypes, rargs, rnulls, true, 1);
            if (crc == SPI_OK_SELECT && SPI_processed > 0)
            {
                bool cn;
                Datum reply = SPI_getbinval(SPI_tuptable->vals[0],
                                            SPI_tuptable->tupdesc, 1, &cn);
                reply_buf_add(buf, reply, (Datum) 0, (Datum) 0, cn, true, true);
            }
        }
    }
}

/* The read shape used when no intent is supplied: the topic's witnessed
 * gloss, else its strongest outgoing chain. */
#define ROUTE_DEFAULT_INTENT "fallback"

static void respond_routed(const char *prompt, Datum context, bool ctx_null,
                           RouteResult *routep, const RouteBind *bind,
                           ReplyBuf *buf);

static void
respond_impl(const char *prompt, Datum context, bool ctx_null, ReplyBuf *buf)
{
    RouteResult route = { 0 };
    RouteBind   bind = { 0 };

    route.intent = pstrdup(ROUTE_DEFAULT_INTENT);
    bind.topic = spi_resolve_topic(prompt, context, ctx_null);
    bind.ctx_ids = spi_context_ids(prompt, bind.topic);
    respond_routed(prompt, context, ctx_null, &route, &bind, buf);
}

/* Intents answered by recall_<intent>_response(topic) alone; when no
 * consensus answers, the reply names the topic and what is unwitnessed. */
static const struct {
    const char *intent;
    const char *sql;
    const char *fallback;
} kSingleArgIntents[] = {
    { "languages",
      "SELECT reply, eff_mu, witnesses FROM converse.recall_languages_response($1)",
      "no cross-language consensus yet." },
    { "synonyms",
      "SELECT reply, eff_mu, witnesses FROM converse.recall_synonyms_response($1)",
      "no synonym consensus yet." },
    { "examples",
      "SELECT reply, eff_mu, witnesses FROM converse.recall_examples_response($1)",
      "no example consensus yet." },
    { "describe",
      "SELECT reply, eff_mu, witnesses FROM converse.recall_describe_response($1)",
      "no relation consensus to describe yet." },
};

/* Returns true if intent matched a single-arg shape and was answered. */
static bool
respond_single_arg_intent(ReplyBuf *buf, const char *intent, Datum topic)
{
    for (size_t i = 0; i < lengthof(kSingleArgIntents); i++)
    {
        if (strcmp(intent, kSingleArgIntents[i].intent) != 0)
            continue;
        Oid   types[1] = { BYTEAOID };
        Datum args[1]  = { topic };
        int   n = spi_forward_replies(buf, kSingleArgIntents[i].sql, 1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, kSingleArgIntents[i].fallback);
        return true;
    }
    return false;
}

/* Dispatch one recall intent over already-resolved ids and buffer the reply
 * rows (text, effective mu, witness count). Takes ownership of route. */
static void
respond_routed(const char *prompt, Datum context, bool ctx_null,
               RouteResult *routep, const RouteBind *bind, ReplyBuf *buf)
{
    RouteResult route = *routep;
    Datum       topic = bind->topic;
    int         n;

    if (topic == (Datum) 0)
    {
        emit_no_topic_msg(buf, prompt, &route);
        route_free(&route);
        return;
    }

    if (route.intent && respond_single_arg_intent(buf, route.intent, topic))
    {
        route_free(&route);
        return;
    }

    if (route.intent && strcmp(route.intent, "define") == 0)
    {
        Oid   types[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum args[2] = { topic, bind->ctx_ids };
        char  nulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_define_response($1, $2)",
            2, types, args, nulls);
        if (n == 0)
            emit_label_fallback(buf, topic, "no glosses have been witnessed yet.");
    }
    else if (route.intent && strcmp(route.intent, "what_is") == 0)
    {
        Oid   types[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum args[2] = { topic, bind->ctx_ids };
        char  nulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_what_is_response($1, $2)",
            2, types, args, nulls);
        if (n == 0)
            emit_label_fallback(buf, topic, "no sense consensus has been witnessed yet.");
    }
    else if (route.intent && strcmp(route.intent, "translate") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2];
        char  nulls[3] = "  ";

        args[0] = topic;
        if (route.phrase2)
            args[1] = CStringGetTextDatum(route.phrase2);
        else
            nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_translate_response($1, $2)",
            2, types, args, nulls);
        if (n == 0)
        {
            if (route.phrase2)
            {
                Datum lbl = spi_label(topic);
                const char *name = (lbl != (Datum) 0) ? text_to_cstr(lbl) : "?";
                char  msg[384];

                snprintf(msg, sizeof(msg),
                         "I hold no witnessed %s translation of \"%s\" yet.",
                         route.phrase2, name);
                reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0,
                              false, true, true);
            }
            else
                emit_label_fallback(buf, topic, "no translation consensus yet.");
        }
    }
    else if (route.intent && strcmp(route.intent, "related") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic,
                          CStringGetTextDatum(route.type_name ? route.type_name : "") };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_related_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_type_miss(buf, topic, route.type_name, false);
    }
    else if (route.intent && strcmp(route.intent, "related_in") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic,
                          CStringGetTextDatum(route.type_name ? route.type_name : "") };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_related_in_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_type_miss(buf, topic, route.type_name, true);
    }
    else if (route.intent && strcmp(route.intent, "is_a") == 0)
    {
        respond_is_a(buf, topic, bind->topic2);
    }
    else if (route.intent && strcmp(route.intent, "reason") == 0)
    {
        Datum topic2 = bind->topic2;
        if (topic2 == (Datum) 0)
        {
            reply_buf_add(buf,
                          CStringGetTextDatum("reason needs a second topic; none resolved."),
                          (Datum) 0, (Datum) 0, false, true, true);
        }
        else
        {
            Oid   types[2] = { BYTEAOID, BYTEAOID };
            Datum args[2] = { topic, topic2 };
            spi_forward_replies(buf,
                "SELECT reply, eff_mu, witnesses "
                "FROM converse.recall_relation_summary_response($1, $2)",
                2, types, args, NULL);
        }
    }
    else if (route.intent &&
             (strcmp(route.intent, "walk") == 0 || strcmp(route.intent, "complete") == 0))
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic, CStringGetTextDatum(route.intent) };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_walk_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no outgoing consensus to walk yet.");
    }
    else if (route.intent && strcmp(route.intent, "fallback") == 0)
    {
        Oid   dtypes[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum dargs[2] = { topic, bind->ctx_ids };
        char  dnulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) dnulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_fallback_gloss($1, $2)",
            2, dtypes, dargs, dnulls);
        if (n == 0)
        {
            Oid   wtypes[1] = { BYTEAOID };
            Datum wargs[1] = { topic };
            n = spi_forward_replies(buf,
                "SELECT reply, eff_mu, witnesses "
                "FROM converse.recall_fallback_walk($1)",
                1, wtypes, wargs, NULL);
            if (n == 0)
                emit_label_fallback(buf, topic, "no gloss or continuation witnessed yet.");
        }
    }
    else
    {
        /* An intent outside the vocabulary (recall_intent rejects these before
         * dispatch) is answered as define. */
        Oid   types[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum args[2] = { topic, bind->ctx_ids };
        char  nulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM converse.recall_define_response($1, $2)",
            2, types, args, nulls);
        if (n == 0)
            emit_label_fallback(buf, topic, "no glosses have been witnessed yet.");
    }

    route_free(&route);
}

/* converse.recall_intent(p_intent, p_topic, p_topic2, p_type_name, p_lang, p_context_ids)
 *
 * Recall over resolved ids: the caller names the read shape. No observation
 * text is consulted. */
Datum
pg_laplace_recall_intent(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    RouteResult    route = { 0 };
    RouteBind      bind = { 0 };
    char          *intent;
    ReplyBuf       buf;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("recall_intent: p_intent must not be NULL"),
                        errhint("SELECT shape FROM converse.query_shapes()")));
    if (PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("recall_intent: p_topic must not be NULL"),
                        errhint("resolve it first: converse.resolve_ref('<word or id hex>')")));

    intent = trim_dup(text_to_cstring(PG_GETARG_TEXT_PP(0)));
    if (!route_intent_known(intent))
        ereport(ERROR, (errmsg("recall_intent: unknown shape \"%s\"", intent),
                        errhint("SELECT shape FROM converse.query_shapes()")));

    route.intent = intent;
    bind.topic = PG_GETARG_DATUM(1);
    if (!PG_ARGISNULL(2))
        bind.topic2 = PG_GETARG_DATUM(2);
    if (!PG_ARGISNULL(3))
        route.type_name = text_to_cstring(PG_GETARG_TEXT_PP(3));
    /* p_lang names the target language of the translate shape. */
    if (!PG_ARGISNULL(4))
        route.phrase2 = text_to_cstring(PG_GETARG_TEXT_PP(4));
    if (!PG_ARGISNULL(5))
        bind.ctx_ids = PG_GETARG_DATUM(5);

    reply_buf_init(&buf);
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "recall_intent: SPI_connect failed");

    respond_routed("", (Datum) 0, true, &route, &bind, &buf);

    laplace_spi_finish(spi_top);
    InitMaterializedSRF(fcinfo, 0);
    reply_buf_emit(rsinfo, &buf);
    return (Datum) 0;
}

Datum
pg_laplace_recall(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt_txt;
    char          *prompt;
    Datum          context = (Datum) 0;
    bool           ctx_null = true;
    ReplyBuf       buf;
    bool           spi_top = false;

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

    reply_buf_init(&buf);
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "recall: SPI_connect failed");

    respond_impl(prompt, context, ctx_null, &buf);

    laplace_spi_finish(spi_top);
    InitMaterializedSRF(fcinfo, 0);
    reply_buf_emit(rsinfo, &buf);
    pfree(prompt);
    return (Datum) 0;
}

Datum
pg_laplace_recall_session(PG_FUNCTION_ARGS)
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

    /* An absent session is a stateless read, never a backend-owned identity.
     * Reusing the PID leaked context across pooled callers and produced short
     * byte strings that were not valid entity IDs. Explicit sessions retain
     * their opaque token; their serialization belongs to the session owner. */
    if (PG_ARGISNULL(1))
    {
        pfree(prompt);
        return pg_laplace_recall(fcinfo);
    }
    session = PG_GETARG_DATUM(1);

    {
        ReplyBuf buf;
        bool     spi_top = false;

        reply_buf_init(&buf);
        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "recall_session: SPI_connect failed");

        {
            Oid   types[1] = { BYTEAOID };
            Datum args[1] = { session };
            bool  isnull;
            int   rc = SPI_execute_with_args(
                "SELECT generation.session_last_resolved($1)",
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
            RouteResult route = { 0 };
            RouteBind   bind = { 0 };
            Oid         itypes[3] = { BYTEAOID, TEXTOID, BYTEAOID };
            Datum       iargs[3];

            route.intent = pstrdup(ROUTE_DEFAULT_INTENT);
            bind.topic = spi_resolve_topic(prompt, context, ctx_null);
            bind.ctx_ids = spi_context_ids(prompt, bind.topic);

            iargs[0] = session;
            iargs[1] = CStringGetTextDatum(prompt);
            iargs[2] = bind.topic;

            /* Records the observation as a conversational-provenance witness
             * of the session; failure raises rather than dropping the turn. */
            int rec_rc = SPI_execute_with_args(
                "SELECT generation.session_record_prompt($1, $2, $3)",
                3, itypes, iargs, bind.topic == (Datum) 0 ? "  n" : NULL, false, 0);
            if (rec_rc != SPI_OK_SELECT)
                elog(ERROR, "recall_session: session_record_prompt failed (SPI rc %d)",
                     rec_rc);

            /* The topic resolved for the session record answers the read;
             * respond_routed takes ownership of route. */
            respond_routed(prompt, context, ctx_null, &route, &bind, &buf);
        }
        laplace_spi_finish(spi_top);

        InitMaterializedSRF(fcinfo, 0);
        reply_buf_emit(rsinfo, &buf);
    }

    pfree(prompt);
    return (Datum) 0;
}
