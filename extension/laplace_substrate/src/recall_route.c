/* String helpers and the intent vocabulary for recall. The read intent is an
 * argument of converse.recall_intent(); nothing here infers it from the
 * surface form of the observation. */

#include "postgres.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/fmgrprotos.h"
#include "utils/memutils.h"

#include "recall_internal.h"
#include "recall_route.h"
#include "spi_common.h"

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
    static struct { const char *q; SPIPlanPtr plan; } cache[16];
    static int n_cached = 0;
    SPIPlanPtr plan = NULL;
    int rc;

    for (int i = 0; i < n_cached; i++)
    {
        if (cache[i].q == query || strcmp(cache[i].q, query) == 0)
        {
            plan = cache[i].plan;
            break;
        }
    }
    if (plan == NULL)
    {
        if (n_cached < (int) lengthof(cache))
        {
            spi_common_ensure_plan(&cache[n_cached].plan, query, nparams, types);
            cache[n_cached].q = query;
            plan = cache[n_cached++].plan;
        }
        else
        {
            spi_common_ensure_plan(&plan, query, nparams, types);
        }
    }
    rc = SPI_execute_plan(plan, values, nulls, true, 0);

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

/* The read shapes converse.recall_intent() dispatches; converse.query_shapes()
 * publishes the same set. */
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
