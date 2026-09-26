/* Structural read intent for recall. The intent, relation type and both
 * topics arrive as arguments; none is inferred from the surface form of the
 * observation.
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

/* Membership in the recall intent vocabulary (see route_intents). */
extern bool  route_intent_known(const char *intent);

extern void route_free(RouteResult *r);

#endif
