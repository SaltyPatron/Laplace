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
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "laplace/core/math4d.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "recall_route.h"

PG_FUNCTION_INFO_V1(pg_laplace_recall_intent);
PG_FUNCTION_INFO_V1(pg_laplace_recall);
PG_FUNCTION_INFO_V1(pg_laplace_recall_session);
PG_FUNCTION_INFO_V1(pg_laplace_define_fast);
PG_FUNCTION_INFO_V1(pg_laplace_word_shape_peers_fast);

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

/* Sense-disambiguation context for the gloss responders: the other resolvable
 * tokens the caller supplied. Structural callers pass ids directly; the text
 * entry points derive them from the prompt via prompt_state(). Either way this
 * is pure id resolution — no grammar is consulted. */
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
        "SELECT laplace.recall_context_exclude($1, $2)",
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

/* The default read shape when a caller supplies no intent. A bare prompt says
 * nothing structural about what is being asked, so answering it means showing
 * what is witnessed about the topic: gloss first, then the strongest chain.
 * Every other shape is selected explicitly by the caller. */
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

/* The routed body: takes an intent plus already-resolved ids. recall_session
 * shares it so session_record_prompt and the reply resolve the topic once.
 * Owns route: frees it on every path. */
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

    if (route.intent && strcmp(route.intent, "define") == 0)
    {
        Oid   types[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum args[2] = { topic, bind->ctx_ids };
        char  nulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_define_response($1, $2)",
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
            "FROM laplace.recall_what_is_response($1, $2)",
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
        





        Oid   dtypes[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum dargs[2] = { topic, bind->ctx_ids };
        char  dnulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) dnulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_fallback_gloss($1, $2)",
            2, dtypes, dargs, dnulls);
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
        /* Unknown intent. recall_intent() rejects these before dispatch, so
         * reaching here means an internal caller passed something outside the
         * published vocabulary; answer with the gloss rather than nothing. */
        Oid   types[2] = { BYTEAOID, BYTEAARRAYOID };
        Datum args[2] = { topic, bind->ctx_ids };
        char  nulls[3] = "  ";

        if (bind->ctx_ids == (Datum) 0) nulls[1] = 'n';
        n = spi_forward_replies(buf,
            "SELECT reply, eff_mu, witnesses "
            "FROM laplace.recall_define_response($1, $2)",
            2, types, args, nulls);
        if (n == 0)
            emit_label_fallback(buf, topic, "no glosses have been witnessed yet.");
    }

    route_free(&route);
}


typedef struct DefineCandidate
{
    Datum   definition_id;
    int64   score_fp;
    int64   witness_count;
} DefineCandidate;

typedef struct DefineCandBuf
{
    DefineCandidate *rows;
    int              n;
    int              cap;
} DefineCandBuf;

static void
define_cand_add(DefineCandBuf *buf, Datum definition_id, int64 score_fp, int64 witness_count)
{
    if (buf->n >= buf->cap)
    {
        buf->cap = buf->cap < 16 ? 16 : buf->cap * 2;
        buf->rows = buf->rows
            ? repalloc(buf->rows, buf->cap * sizeof(DefineCandidate))
            : palloc(buf->cap * sizeof(DefineCandidate));
    }
    buf->rows[buf->n].definition_id = copy_bytea_datum(definition_id);
    buf->rows[buf->n].score_fp = score_fp;
    buf->rows[buf->n].witness_count = witness_count;
    buf->n++;
}

static int
define_cand_cmp(const void *a, const void *b)
{
    int64 sa = ((const DefineCandidate *) a)->score_fp;
    int64 sb = ((const DefineCandidate *) b)->score_fp;
    return (sb > sa) - (sb < sa);
}

/*
 * Native replacement for the define()/senses()/lexical_peers() SQL composition
 * chain. That chain was measured taking 48+ seconds and 2.27M shared-buffer
 * hits for a single word, root-caused to: (1) every function in the chain
 * uses a CTE, which PostgreSQL cannot inline for set-returning functions --
 * each nested call is planned and executed as an opaque black box with zero
 * cross-function optimization, and (2) render_text() was being called on
 * every candidate row before the final ORDER BY/LIMIT trimmed the result to
 * p_limit. This function does the whole thing as a small, fixed number of
 * explicitly sequenced SPI calls instead: fetch lexical peers once (not the
 * 2-3x redundant calls the SQL chain made), fetch bounded candidate sets via
 * indexed ANY(array) joins, rank in C, and render_text() only the winners.
 */
static void
define_fast_impl(Datum p_word, ArrayType *p_context_arr, int p_limit, ReplyBuf *buf)
{
    hash128_t   has_sense    = rel_type_id("HAS_SENSE");
    hash128_t   is_sense_of  = rel_type_id("IS_SENSE_OF");
    hash128_t   has_def      = rel_type_id("HAS_DEFINITION");
    Datum       peers_arr;
    bool        peers_null;
    int         rc;
    DefineCandBuf cands = {0};
    bool        has_context = p_context_arr != NULL && ArrayGetNItems(
                                   ARR_NDIM(p_context_arr), ARR_DIMS(p_context_arr)) > 0;


    {
        Oid   types[1] = { BYTEAOID };
        Datum args[1] = { p_word };

        rc = SPI_execute_with_args(
            "SELECT laplace.lexical_peers($1)", 1, types, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
            return;
        peers_arr = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &peers_null);
        if (peers_null)
            return;
        peers_arr = copy_bytea_datum(peers_arr);
    }


    {
        Oid   types[4] = { BYTEAARRAYOID, BYTEAOID, BYTEAOID, BYTEAOID };
        Datum args[4] = { peers_arr, hash128_to_datum(&has_sense),
                          hash128_to_datum(&is_sense_of), hash128_to_datum(&has_def) };

        rc = SPI_execute_with_args(
            "SELECT g.object_id, g.rating, g.rd, g.witness_count, "
            "       s.rating, s.rd, ss.rating, ss.rd "
            "FROM laplace.v_consensus_unrefuted s "
            "JOIN laplace.v_consensus_unrefuted ss ON ss.subject_id = s.object_id AND ss.type_id = $3 "
            "JOIN laplace.v_consensus_unrefuted g  ON g.subject_id  = ss.object_id AND g.type_id = $4 "
            "WHERE s.subject_id = ANY($1) AND s.type_id = $2",
            4, types, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "define_fast: sense query failed: %s", SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool n0, n1, n2, n3, ns1, ns2, ns3, ns4;
            Datum obj   = SPI_getbinval(tup, td, 1, &n0);
            Datum rat   = SPI_getbinval(tup, td, 2, &n1);
            Datum rd    = SPI_getbinval(tup, td, 3, &n2);
            Datum wc    = SPI_getbinval(tup, td, 4, &n3);
            Datum srat  = SPI_getbinval(tup, td, 5, &ns1);
            Datum srd   = SPI_getbinval(tup, td, 6, &ns2);
            Datum ssrat = SPI_getbinval(tup, td, 7, &ns3);
            Datum ssrd  = SPI_getbinval(tup, td, 8, &ns4);
            int64 score = laplace_effective_mu_fp(DatumGetInt64(rat), DatumGetInt64(rd));

            if (!ns1 && !ns2) score += laplace_effective_mu_fp(DatumGetInt64(srat), DatumGetInt64(srd));
            if (!ns3 && !ns4) score += laplace_effective_mu_fp(DatumGetInt64(ssrat), DatumGetInt64(ssrd));
            define_cand_add(&cands, obj, score, n3 ? 0 : DatumGetInt64(wc));
        }
    }


    {
        Oid   types[2] = { BYTEAARRAYOID, BYTEAOID };
        Datum args[2] = { peers_arr, hash128_to_datum(&has_def) };

        rc = SPI_execute_with_args(
            "SELECT g.object_id, g.rating, g.rd, g.witness_count "
            "FROM laplace.v_consensus_unrefuted g "
            "WHERE g.subject_id = ANY($1) AND g.type_id = $2",
            2, types, args, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "define_fast: peer-definition query failed: %s", SPI_result_code_string(rc));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool n0, n1, n2, n3;
            Datum obj = SPI_getbinval(tup, td, 1, &n0);
            Datum rat = SPI_getbinval(tup, td, 2, &n1);
            Datum rd  = SPI_getbinval(tup, td, 3, &n2);
            Datum wc  = SPI_getbinval(tup, td, 4, &n3);
            int64 score = laplace_effective_mu_fp(DatumGetInt64(rat), DatumGetInt64(rd));

            define_cand_add(&cands, obj, score, n3 ? 0 : DatumGetInt64(wc));
        }
    }

    if (cands.n == 0)
        return;


    if (has_context)
    {
        Datum *ids = (Datum *) palloc(sizeof(Datum) * cands.n);
        int   *chain_next;
        HTAB  *cand_idx;
        HASHCTL ctl;
        ArrayType *cand_arr;
        Oid   types[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
        Datum args[2];

        for (int i = 0; i < cands.n; i++) ids[i] = cands.rows[i].definition_id;
        cand_arr = construct_array(ids, cands.n, BYTEAOID, -1, false, 'i');
        args[0] = PointerGetDatum(cand_arr);
        args[1] = PointerGetDatum(p_context_arr);

        /* Hash-index the candidates by definition id: the old per-row linear
         * scan was O(results × candidates) bytea compares. Duplicate ids are
         * chained (built back-to-front so chains run in ascending index
         * order) because the linear scan credited EVERY matching candidate,
         * not just the first. */
        {
            typedef struct DefineCandIdxEntry
            {
                hash128_t key;
                int       head;
            } DefineCandIdxEntry;

            memset(&ctl, 0, sizeof(ctl));
            ctl.keysize   = sizeof(hash128_t);
            ctl.entrysize = sizeof(DefineCandIdxEntry);
            cand_idx = hash_create("define_fast cand idx", cands.n > 16 ? cands.n : 16,
                                   &ctl, HASH_ELEM | HASH_BLOBS);
            chain_next = (int *) palloc(sizeof(int) * cands.n);
            for (int i = cands.n - 1; i >= 0; i--)
            {
                hash128_t key = datum_to_hash128(cands.rows[i].definition_id);
                bool      found;
                DefineCandIdxEntry *e = (DefineCandIdxEntry *)
                    hash_search(cand_idx, &key, HASH_ENTER, &found);

                chain_next[i] = found ? e->head : -1;
                e->head = i;
            }

            rc = SPI_execute_with_args(
                "SELECT c.object_id, sum(laplace.eff_mu(c.rating, c.rd)) "
                "FROM laplace.v_consensus_unrefuted c "
                "WHERE c.object_id = ANY($1) AND c.subject_id = ANY($2) "
                "GROUP BY c.object_id",
                2, types, args, NULL, true, 0);
            if (rc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool n0, n1;
                    Datum obj = SPI_getbinval(tup, td, 1, &n0);
                    Datum sum = SPI_getbinval(tup, td, 2, &n1);
                    hash128_t key;
                    bool      found;
                    DefineCandIdxEntry *e;

                    if (n1) continue;
                    key = datum_to_hash128(obj);
                    e = (DefineCandIdxEntry *)
                        hash_search(cand_idx, &key, HASH_FIND, &found);
                    if (!found) continue;
                    for (int i = e->head; i >= 0; i = chain_next[i])
                        cands.rows[i].score_fp += DatumGetInt64(sum);
                }
            }
            hash_destroy(cand_idx);
            pfree(chain_next);
        }
        pfree(ids);
    }

    qsort(cands.rows, cands.n, sizeof(DefineCandidate), define_cand_cmp);

    {
        int n_out = cands.n < p_limit ? cands.n : p_limit;

        for (int i = 0; i < n_out; i++)
        {
            Datum text_datum = spi_render_text(cands.rows[i].definition_id);
            Datum eff_mu_num;
            bool  mu_null = false;

            if (text_datum == (Datum) 0)
                continue;
            eff_mu_num = DirectFunctionCall2(numeric_round,
                DirectFunctionCall2(numeric_div,
                    DirectFunctionCall1(int8_numeric, Int64GetDatum(cands.rows[i].score_fp)),
                    DirectFunctionCall1(int8_numeric, Int64GetDatum(INT64CONST(1000000000)))),
                Int32GetDatum(3));
            reply_buf_add(buf, text_datum, eff_mu_num,
                         Int64GetDatum(cands.rows[i].witness_count),
                         false, mu_null, false);
        }
    }
}

Datum
pg_laplace_define_fast(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          word;
    ArrayType     *context_arr = NULL;
    int            limit;
    ReplyBuf       buf;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    word = PG_GETARG_DATUM(0);
    if (!PG_ARGISNULL(1))
        context_arr = PG_GETARG_ARRAYTYPE_P(1);
    limit = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);

    reply_buf_init(&buf);
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "define_fast: SPI_connect failed");

    define_fast_impl(word, context_arr, limit, &buf);

    laplace_spi_finish(spi_top);
    InitMaterializedSRF(fcinfo, 0);
    reply_buf_emit(rsinfo, &buf);
    return (Datum) 0;
}

typedef struct ShapeCandidate
{
    Datum entity_id;
    Datum coord;
    double ang;
    double fr;
} ShapeCandidate;

static int
shape_cand_cmp(const void *a, const void *b)
{
    const ShapeCandidate *ca = (const ShapeCandidate *) a;
    const ShapeCandidate *cb = (const ShapeCandidate *) b;

    if (ca->ang != cb->ang) return (ca->ang > cb->ang) - (ca->ang < cb->ang);
    return (ca->fr > cb->fr) - (ca->fr < cb->fr);
}

/*
 * Native replacement for word_shape_peers()'s KNN stage. Root cause
 * (verified via EXPLAIN on the decomposed CTEs): the original query's
 * `ORDER BY p2.coord <<->> me.coord LIMIT 500`, where me.coord came from an
 * outer CTE (a correlated column reference, not a literal/bound parameter),
 * was NOT eligible for GiST KNN index-ordered scanning -- Postgres fell back
 * to a full hash join against ALL of entities (~1.9M rows via 4 parallel
 * workers) followed by a top-N heapsort over ~785K joined rows, touching
 * ~72K buffer pages just for a "bounded" 500-row scan. Fetching the anchor
 * coordinate first and passing it as a genuine SPI bound parameter to the
 * second query restores real GiST index usage. Also drops the original's
 * `entity_exists(n.entity_id)` filter, which is structurally always true
 * here (n comes from physicalities JOIN entities, so the FK already
 * guarantees the entities row exists) and was pure dead weight.
 */
static Datum
word_shape_peers_fast_impl(Datum p_word, double p_frechet_max)
{
    Oid    types1[1] = { BYTEAOID };
    Datum  args1[1] = { p_word };
    int    rc;
    Datum  me_curve, me_coord, me_type_id;
    int32  me_nconst;
    Oid    geom_oid;
    char  *me_case_class = NULL;
    ShapeCandidate *raw;
    int    n_raw = 0;
    ShapeCandidate *survivors;
    int    n_survivors = 0;

    /* Issue 51: the Frechet gate must run on GEOMETRY. physicalities.trajectory
     * vertices are mantissa-packed child IDENTITIES (exponent pinned 0x3FF) --
     * feeding them to the DP measured hash bits, behaving as a rough aligned
     * sequence-identity test. word_curve() rebuilds the constituent COORD curve
     * (ST_MakeLine ORDER BY ordinal) -- the same metric word_shape_distance uses.
     * The anchor curve is fetched once here and passed as a bound parameter;
     * candidate curves build inside the batched call below (STABLE functions in
     * filters run per row -- the anchor must never be recomputed per candidate). */
    rc = SPI_execute_with_args(
        "SELECT laplace.word_curve($1), p.coord, e.type_id, p.n_constituents "
        "FROM laplace.physicalities p "
        "JOIN laplace.entities e ON e.id = p.entity_id "
        "WHERE p.entity_id = $1 AND p.type = 1 "
        "  AND p.trajectory IS NOT NULL AND p.coord IS NOT NULL "
        "ORDER BY p.id LIMIT 1",
        1, types1, args1, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return PointerGetDatum(construct_empty_array(BYTEAOID));

    {
        HeapTuple me_tup = SPI_tuptable->vals[0];
        TupleDesc me_td  = SPI_tuptable->tupdesc;
        bool n0, n1, n2, n3;

        me_curve   = copy_bytea_datum(SPI_getbinval(me_tup, me_td, 1, &n0));
        me_coord   = copy_bytea_datum(SPI_getbinval(me_tup, me_td, 2, &n1));
        me_type_id = copy_bytea_datum(SPI_getbinval(me_tup, me_td, 3, &n2));
        me_nconst  = DatumGetInt32(SPI_getbinval(me_tup, me_td, 4, &n3));
        geom_oid   = SPI_gettypeid(me_td, 2);
        /* word_curve is NULL when the word has no constituent coords -- no
         * shape to gate on; empty result, same contract as a missing anchor. */
        if (n0)
            return PointerGetDatum(construct_empty_array(BYTEAOID));
    }

    {
        Oid   ctypes[1] = { BYTEAOID };
        Datum cargs[1] = { p_word };
        int   crc = SPI_execute_with_args(
            "SELECT laplace.word_case_class_surface($1)", 1, ctypes, cargs, NULL, true, 1);
        if (crc == SPI_OK_SELECT && SPI_processed > 0)
        {
            bool cn;
            Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &cn);
            if (!cn)
                me_case_class = text_to_cstring(DatumGetTextPP(d));
        }
    }

    {
        Oid   types2[3] = { BYTEAOID, geom_oid, INT4OID };
        Datum args2[3] = { p_word, me_coord, Int32GetDatum(500) };

        rc = SPI_execute_with_args(
            "SELECT p2.entity_id, p2.coord, e2.type_id, p2.n_constituents "
            "FROM laplace.physicalities p2 "
            "JOIN laplace.entities e2 ON e2.id = p2.entity_id "
            "WHERE p2.type = 1 AND p2.trajectory IS NOT NULL AND p2.coord IS NOT NULL "
            "  AND p2.entity_id <> $1 "
            "ORDER BY p2.coord <<->> $2 LIMIT $3",
            3, types2, args2, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "word_shape_peers_fast: KNN query failed: %s", SPI_result_code_string(rc));

        raw = (ShapeCandidate *) palloc(sizeof(ShapeCandidate) * (SPI_processed + 1));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool cn0, cn1, cn2, cn3;
            Datum eid   = SPI_getbinval(tup, td, 1, &cn0);
            Datum coord = SPI_getbinval(tup, td, 2, &cn1);
            Datum tid   = SPI_getbinval(tup, td, 3, &cn2);
            Datum ncst  = SPI_getbinval(tup, td, 4, &cn3);

            if (cn0 || cn1 || cn2 || cn3) continue;
            if (!bytea_eq(tid, me_type_id)) continue;
            if (DatumGetInt32(ncst) != me_nconst) continue;
            raw[n_raw].entity_id  = copy_bytea_datum(eid);
            raw[n_raw].coord      = copy_bytea_datum(coord);
            n_raw++;
        }
    }

    /*
     * Batch the per-candidate post-filter into a small, fixed number of SPI
     * calls instead of up to 3 * n_raw individual round-trips -- each SPI
     * call pays real parse/plan/executor overhead regardless of how little
     * work it does, so N individual calls cost far more than one call over
     * an N-element array (unnest ... WITH ORDINALITY zips a position index
     * back onto each row so results can be scattered back to the right
     * candidate in C).
     */
    survivors = (ShapeCandidate *) palloc(sizeof(ShapeCandidate) * (n_raw + 1));
    {
        bool *case_ok = (bool *) palloc0(sizeof(bool) * n_raw);

        if (me_case_class == NULL)
        {
            for (int i = 0; i < n_raw; i++) case_ok[i] = true;
        }
        else if (n_raw > 0)
        {
            Datum *idarr = (Datum *) palloc(sizeof(Datum) * n_raw);
            ArrayType *id_array;
            Oid   ctypes[1] = { BYTEAARRAYOID };
            Datum cargs[1];
            int   crc;

            for (int i = 0; i < n_raw; i++) idarr[i] = raw[i].entity_id;
            id_array = construct_array(idarr, n_raw, BYTEAOID, -1, false, 'i');
            cargs[0] = PointerGetDatum(id_array);

            crc = SPI_execute_with_args(
                "SELECT t.idx, laplace.word_case_class_surface(t.entity_id) "
                "FROM unnest($1::bytea[]) WITH ORDINALITY AS t(entity_id, idx)",
                1, ctypes, cargs, NULL, true, 0);
            if (crc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool  in0, in1;
                    int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                    Datum ccd = SPI_getbinval(tup, td, 2, &in1);

                    if (in0 || in1 || idx < 1 || idx > n_raw) continue;
                    {
                        char *cc = text_to_cstring(DatumGetTextPP(ccd));
                        case_ok[idx - 1] = (strcmp(cc, me_case_class) == 0);
                        pfree(cc);
                    }
                }
            }
            pfree(idarr);
        }

        {
            int    n_matched = 0;
            int   *matched_raw_idx = (int *) palloc(sizeof(int) * (n_raw + 1));
            Datum *eidarr = (Datum *) palloc(sizeof(Datum) * (n_raw + 1));
            Datum *coordarr = (Datum *) palloc(sizeof(Datum) * (n_raw + 1));

            for (int i = 0; i < n_raw; i++)
            {
                if (!case_ok[i]) continue;
                matched_raw_idx[n_matched] = i;
                eidarr[n_matched] = raw[i].entity_id;
                coordarr[n_matched] = raw[i].coord;
                n_matched++;
            }

            if (n_matched > 0)
            {
                ArrayType *eid_array = construct_array(eidarr, n_matched, BYTEAOID, -1, false, 'i');
                ArrayType *coord_array = construct_array(coordarr, n_matched, geom_oid, -1, false, 'i');
                Oid   geom_arr_oid = get_array_type(geom_oid);
                int   grc;
                double *fr_by_pos = (double *) palloc0(sizeof(double) * n_matched);
                double *ang_by_pos = (double *) palloc0(sizeof(double) * n_matched);
                bool  *ok_by_pos = (bool *) palloc0(sizeof(bool) * n_matched);
                bool  *coord_ok = (bool *) palloc0(sizeof(bool) * n_matched);
                double *cand_xyzm = (double *) palloc0(sizeof(double) * (size_t) n_matched * 4);
                double me_xyzm[4] = {0, 0, 0, 0};

                /* Frechet stays SQL-computed (order-sensitive, variable-length DP
                 * recurrence -- doesn't batch across candidates the same way a
                 * fixed-width angular distance does). Issue 51: it now runs over
                 * word_curve(entity) -- the constituent COORD curve, word_shape_
                 * distance's metric -- never the mantissa-packed identity
                 * trajectory, whose vertices are hash bits, not S3 shape. */
                {
                    Oid   ftypes[2] = { BYTEAARRAYOID, geom_oid };
                    Datum fargs[2] = { PointerGetDatum(eid_array), me_curve };

                    grc = SPI_execute_with_args(
                        "SELECT t.idx, public.laplace_frechet_4d(laplace.word_curve(t.entity_id), $2) "
                        "FROM unnest($1::bytea[]) WITH ORDINALITY AS t(entity_id, idx)",
                        2, ftypes, fargs, NULL, true, 0);
                    if (grc == SPI_OK_SELECT)
                    {
                        for (uint64 r = 0; r < SPI_processed; r++)
                        {
                            HeapTuple tup = SPI_tuptable->vals[r];
                            TupleDesc td  = SPI_tuptable->tupdesc;
                            bool  in0, in1;
                            int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                            Datum frd = SPI_getbinval(tup, td, 2, &in1);

                            if (in0 || idx < 1 || idx > n_matched || in1) continue;
                            fr_by_pos[idx - 1] = DatumGetFloat8(frd);
                            ok_by_pos[idx - 1] = true;
                        }
                    }
                }

                /* Angular distance: SQL only extracts raw coordinate components
                 * (no distance math) -- laplace_substrate doesn't link liblwgeom,
                 * so this is the actual Datum-to-double boundary; the distance
                 * computation itself is math4d_angular_distance_batch, native,
                 * AVX2-batched. See engine/core/src/math4d.c. */
                {
                    Oid   mtypes[1] = { geom_oid };
                    Datum margs[1] = { me_coord };
                    int   mrc;

                    mrc = SPI_execute_with_args(
                        "SELECT ST_X($1), ST_Y($1), ST_Z($1), ST_M($1)",
                        1, mtypes, margs, NULL, true, 1);
                    if (mrc == SPI_OK_SELECT && SPI_processed == 1)
                    {
                        HeapTuple tup = SPI_tuptable->vals[0];
                        TupleDesc td  = SPI_tuptable->tupdesc;
                        bool i0, i1, i2, i3;

                        me_xyzm[0] = DatumGetFloat8(SPI_getbinval(tup, td, 1, &i0));
                        me_xyzm[1] = DatumGetFloat8(SPI_getbinval(tup, td, 2, &i1));
                        me_xyzm[2] = DatumGetFloat8(SPI_getbinval(tup, td, 3, &i2));
                        me_xyzm[3] = DatumGetFloat8(SPI_getbinval(tup, td, 4, &i3));
                    }
                }
                {
                    Oid   ctypes[1] = { geom_arr_oid };
                    Datum cargs2[1] = { PointerGetDatum(coord_array) };
                    int   crc2;

                    crc2 = SPI_execute_with_args(
                        "SELECT t.idx, ST_X(t.coord), ST_Y(t.coord), ST_Z(t.coord), ST_M(t.coord) "
                        "FROM unnest($1) WITH ORDINALITY AS t(coord, idx)",
                        1, ctypes, cargs2, NULL, true, 0);
                    if (crc2 == SPI_OK_SELECT)
                    {
                        for (uint64 r = 0; r < SPI_processed; r++)
                        {
                            HeapTuple tup = SPI_tuptable->vals[r];
                            TupleDesc td  = SPI_tuptable->tupdesc;
                            bool  in0, in1, in2, in3, in4;
                            int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                            Datum xd = SPI_getbinval(tup, td, 2, &in1);
                            Datum yd = SPI_getbinval(tup, td, 3, &in2);
                            Datum zd = SPI_getbinval(tup, td, 4, &in3);
                            Datum wd = SPI_getbinval(tup, td, 5, &in4);

                            if (in0 || idx < 1 || idx > n_matched) continue;
                            if (in1 || in2 || in3 || in4) continue;
                            cand_xyzm[(idx - 1) * 4 + 0] = DatumGetFloat8(xd);
                            cand_xyzm[(idx - 1) * 4 + 1] = DatumGetFloat8(yd);
                            cand_xyzm[(idx - 1) * 4 + 2] = DatumGetFloat8(zd);
                            cand_xyzm[(idx - 1) * 4 + 3] = DatumGetFloat8(wd);
                            coord_ok[idx - 1] = true;
                        }
                    }
                }
                math4d_angular_distance_batch(cand_xyzm, n_matched, me_xyzm, ang_by_pos);
                for (int p = 0; p < n_matched; p++)
                    if (!coord_ok[p])
                        ok_by_pos[p] = false;

                for (int p = 0; p < n_matched; p++)
                {
                    int i = matched_raw_idx[p];

                    if (!ok_by_pos[p]) continue;
                    /* Frechet is the sole admission gate -- it's order-sensitive, unlike
                     * angular/centroid distance, which is mathematically order-INSENSITIVE
                     * (centroid = average of constituent points, and averaging is commutative),
                     * so it can never discriminate anagrams and must not be an independent OR
                     * admission branch. Kept only as the ang-first ORDER BY tie-break below. */
                    if (fr_by_pos[p] > p_frechet_max) continue;
                    survivors[n_survivors].entity_id = raw[i].entity_id;
                    survivors[n_survivors].fr = fr_by_pos[p];
                    survivors[n_survivors].ang = ang_by_pos[p];
                    n_survivors++;
                }
                pfree(fr_by_pos);
                pfree(ang_by_pos);
                pfree(ok_by_pos);
            }
            pfree(matched_raw_idx);
            pfree(eidarr);
            pfree(coordarr);
        }
        pfree(case_ok);
    }
    pfree(raw);

    qsort(survivors, n_survivors, sizeof(ShapeCandidate), shape_cand_cmp);

    {
        int n_out = n_survivors < 48 ? n_survivors : 48;
        Datum *out = (Datum *) palloc(sizeof(Datum) * (n_out > 0 ? n_out : 1));

        for (int i = 0; i < n_out; i++) out[i] = survivors[i].entity_id;
        return PointerGetDatum(construct_array(out, n_out, BYTEAOID, -1, false, 'i'));
    }
}

Datum
pg_laplace_word_shape_peers_fast(PG_FUNCTION_ARGS)
{
    Datum  word;
    double frechet_max;
    bool   spi_top = false;
    Datum  result;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    word = PG_GETARG_DATUM(0);
    frechet_max = PG_ARGISNULL(1) ? 0.02 : PG_GETARG_FLOAT8(1);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "word_shape_peers_fast: SPI_connect failed");

    result = word_shape_peers_fast_impl(word, frechet_max);

    laplace_spi_finish(spi_top);
    PG_RETURN_ARRAYTYPE_P(DatumGetArrayTypeP(result));
}

/* recall_intent(p_intent, p_topic, p_topic2, p_type_name, p_lang, p_context_ids)
 *
 * The structural read entry point: the caller names the shape of the read and
 * supplies resolved content ids. Nothing about the answer depends on the
 * language the question was asked in, because no question text is consulted. */
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
                        errhint("SELECT shape FROM laplace.query_shapes()")));
    if (PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("recall_intent: p_topic must not be NULL"),
                        errhint("resolve it first: laplace.resolve_ref('<word or id hex>')")));

    intent = trim_dup(text_to_cstring(PG_GETARG_TEXT_PP(0)));
    if (!route_intent_known(intent))
        ereport(ERROR, (errmsg("recall_intent: unknown shape \"%s\"", intent),
                        errhint("SELECT shape FROM laplace.query_shapes()")));

    route.intent = intent;
    bind.topic = PG_GETARG_DATUM(1);
    if (!PG_ARGISNULL(2))
        bind.topic2 = PG_GETARG_DATUM(2);
    if (!PG_ARGISNULL(3))
        route.type_name = text_to_cstring(PG_GETARG_TEXT_PP(3));
    /* p_lang carries the target language for the translate shape. It names a
     * language, it does not parse one — any language can be asked for from any
     * language. */
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

            SPI_execute_with_args(
                "SELECT laplace.session_record_prompt($1, $2, $3)",
                3, itypes, iargs, bind.topic == (Datum) 0 ? "  n" : NULL, false, 0);

            /* Reuse the topic just resolved for session_record_prompt instead
             * of resolving again inside respond_impl — respond_routed takes
             * ownership of route. */
            respond_routed(prompt, context, ctx_null, &route, &bind, &buf);
        }
        laplace_spi_finish(spi_top);

        InitMaterializedSRF(fcinfo, 0);
        reply_buf_emit(rsinfo, &buf);
    }

    pfree(prompt);
    return (Datum) 0;
}
