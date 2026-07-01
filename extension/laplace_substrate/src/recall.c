#include "postgres.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/fmgrprotos.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "recall_route.h"

PG_FUNCTION_INFO_V1(pg_laplace_parse_ask);
PG_FUNCTION_INFO_V1(pg_laplace_recall);
PG_FUNCTION_INFO_V1(pg_laplace_recall_session);

typedef struct ReplyRow
{
    Datum reply;
    Datum mu;
    Datum witnesses;
    bool  reply_null;
    bool  mu_null;
    bool  wit_null;
} ReplyRow;

typedef struct ReplyBuf
{
    MemoryContext cxt;          


    ReplyRow *rows;
    int       n;
    int       cap;
} ReplyBuf;

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
reply_buf_init(ReplyBuf *buf)
{
    buf->cxt = CurrentMemoryContext;    
    buf->rows = NULL;
    buf->n = 0;
    buf->cap = 0;
}

static void
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

static void
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

static void
reply_buf_emit(ReturnSetInfo *rsinfo, ReplyBuf *buf)
{
    for (int i = 0; i < buf->n; i++)
    {
        ReplyRow *row = &buf->rows[i];

        emit_reply_row(rsinfo, row->reply, row->mu, row->witnesses,
                       row->reply_null, row->mu_null, row->wit_null);
    }
}

static int
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
        "SELECT laplace.resolve_topic($1, $2)",
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
respond_is_a(ReplyBuf *buf, Datum topic, const RouteResult *route)
{
    Datum topic2 = spi_resolve_topic(route->phrase2, (Datum) 0, true);

    if (topic2 == (Datum) 0)
    {
        char msg[256];
        snprintf(msg, sizeof(msg), "I hold no consensus about \"%s\" yet.",
                 route->phrase2 ? route->phrase2 : "?");
        reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, false, true, true);
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
                    "SELECT laplace.recall_is_a_yes_reply($1, $2, $3)",
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
                "SELECT laplace.recall_is_a_no_reply($1, $2, $3)",
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

static void
respond_impl(const char *prompt, Datum context, bool ctx_null, ReplyBuf *buf)
{
    RouteResult route;
    Datum       topic;
    int         n;

    route_prompt_impl(prompt, &route);
    topic = spi_resolve_topic(route.phrase, context, ctx_null);

    if (topic == (Datum) 0)
    {
        emit_no_topic_msg(buf, prompt, &route);
        route_free(&route);
        return;
    }

    if (route.intent && strcmp(route.intent, "define") == 0)
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_define_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no glosses have been witnessed yet.");
    }
    else if (route.intent && strcmp(route.intent, "what_is") == 0)
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_what_is_response($1, $2)",
            2, types, args, NULL);
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
            "FROM laplace.recall_translate_response($1, $2)",
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
    else if (route.intent && strcmp(route.intent, "languages") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_languages_response($1)",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no cross-language consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "synonyms") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_synonyms_response($1)",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no synonym consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "examples") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_examples_response($1)",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no example consensus yet.");
    }
    else if (route.intent && strcmp(route.intent, "describe") == 0)
    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_describe_response($1)",
            1, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no relation consensus to describe yet.");
    }
    else if (route.intent && strcmp(route.intent, "related") == 0)
    {
        Oid   types[2] = { BYTEAOID, TEXTOID };
        Datum args[2] = { topic,
                          CStringGetTextDatum(route.type_name ? route.type_name : "") };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_related_response($1, $2)",
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
            "FROM laplace.recall_related_in_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_type_miss(buf, topic, route.type_name, true);
    }
    else if (route.intent && strcmp(route.intent, "is_a") == 0)
    {
        respond_is_a(buf, topic, &route);
    }
    else if (route.intent && strcmp(route.intent, "reason") == 0)
    {
        Datum topic2 = spi_resolve_topic(route.phrase2, (Datum) 0, true);
        if (topic2 == (Datum) 0)
        {
            char msg[256];
            snprintf(msg, sizeof(msg), "I hold no consensus about \"%s\" yet.",
                     route.phrase2 ? route.phrase2 : "?");
            reply_buf_add(buf, CStringGetTextDatum(msg), (Datum) 0, (Datum) 0, false, true, true);
        }
        else
        {
            Oid   types[2] = { BYTEAOID, BYTEAOID };
            Datum args[2] = { topic, topic2 };
            spi_forward_replies(buf,
                "SELECT reply, eff_mu, witnesses "
                "FROM laplace.recall_relation_summary_response($1, $2)",
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
            "FROM laplace.recall_walk_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no outgoing consensus to walk yet.");
    }
    else if (route.intent && strcmp(route.intent, "fallback") == 0)
    {
        





        Oid   dtypes[2] = { TEXTOID, BYTEAOID };
        Datum dargs[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_fallback_gloss($1, $2)",
            2, dtypes, dargs, NULL);
        if (n == 0)
        {
            Oid   wtypes[1] = { BYTEAOID };
            Datum wargs[1] = { topic };
            n = spi_forward_replies(buf,
                "SELECT reply, eff_mu, witnesses "
                "FROM laplace.recall_fallback_walk($1)",
                1, wtypes, wargs, NULL);
            if (n == 0)
                emit_label_fallback(buf, topic, "no gloss or continuation witnessed yet.");
        }
    }
    else
    {
        Oid   types[2] = { TEXTOID, BYTEAOID };
        Datum args[2] = { CStringGetTextDatum(prompt), topic };
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_define_response($1, $2)",
            2, types, args, NULL);
        if (n == 0)
            emit_label_fallback(buf, topic, "no glosses have been witnessed yet.");
    }

    route_free(&route);
}

Datum
pg_laplace_parse_ask(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    text          *prompt_txt;
    char          *prompt;
    RouteResult    route;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("parse_ask: p_prompt must not be NULL")));
    prompt_txt = PG_GETARG_TEXT_PP(0);
    prompt = text_to_cstring(prompt_txt);

    route_prompt_impl(prompt, &route);
    InitMaterializedSRF(fcinfo, 0);
    emit_route_row(rsinfo, &route);
    route_free(&route);
    pfree(prompt);
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

    if (PG_ARGISNULL(1))
    {
        




        char   pidbuf[32];
        int    len = snprintf(pidbuf, sizeof(pidbuf), "%d", MyProcPid);
        bytea *b = (bytea *) palloc(VARHDRSZ + len);

        SET_VARSIZE(b, VARHDRSZ + len);
        memcpy(VARDATA(b), pidbuf, len);
        session = PointerGetDatum(b);
    }
    else
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
                "SELECT laplace.session_last_resolved($1)",
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
                "SELECT laplace.session_record_prompt($1, $2, $3)",
                3, itypes, iargs, topic == (Datum) 0 ? "  n" : NULL, false, 0);
        }

        respond_impl(prompt, context, ctx_null, &buf);
        laplace_spi_finish(spi_top);

        InitMaterializedSRF(fcinfo, 0);
        reply_buf_emit(rsinfo, &buf);
    }

    pfree(prompt);
    return (Datum) 0;
}
