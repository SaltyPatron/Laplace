/* Structural read intent for the recall responder family.
 *
 * The intent, the relation type and both topics arrive as ARGUMENTS — they are
 * never inferred from the surface form of a prompt. The English pattern ladder
 * that used to derive them here was removed: it made a language-agnostic
 * substrate answer only to English rhetoric, and the caller (UI, MCP, HTTP)
 * always knew the shape of the read it wanted anyway.
 */

#ifndef LAPLACE_RECALL_ROUTE_H
#define LAPLACE_RECALL_ROUTE_H

typedef struct RouteResult
{
    char *intent;
    char *phrase;
    char *phrase2;
    char *type_name;
} RouteResult;

/* Pre-resolved ids for a routed read. Every field is a bytea Datum or 0. */
typedef struct RouteBind
{
    Datum topic;                /* subject; 0 = unresolved */
    Datum topic2;               /* object for is_a / reason; 0 = none */
    Datum ctx_ids;              /* bytea[] sense-disambiguation context; 0 = none */
} RouteBind;

extern char *text_to_cstr(Datum d);
extern char *trim_dup(const char *s);
extern bool  str_empty(const char *s);
extern char *lower_dup(const char *s);

/* Canonical intent vocabulary. laplace.query_shapes() publishes it to callers
 * so a UI can build its controls from the substrate instead of hardcoding. */
extern bool  route_intent_known(const char *intent);

extern void route_free(RouteResult *r);

#endif
