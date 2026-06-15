/*
 * recall_route.h — the pure NLU intent router: maps an English prompt to an
 * intent + relation-type name with NO SPI/DB access (only PG text functions).
 * Split out of recall.c so the router is a testable seam, isolated from the
 * SPI-bound responder/entry points that consume it. (This router is a known
 * layering fork — it hard-codes relation-type strings the engine also owns —
 * slated for engine-side routing later; isolating it is the prerequisite.)
 *
 * Defined in recall_route.c. Include after postgres.h.
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

/* Shared text helpers (used by both the router and the responder in recall.c). */
extern char *text_to_cstr(Datum d);
extern char *trim_dup(const char *s);
extern bool  str_empty(const char *s);
extern char *lower_dup(const char *s);

/* Route a prompt into r (caller route_free's it). */
extern void route_prompt_impl(const char *prompt, RouteResult *r);
extern void route_free(RouteResult *r);

#endif                          /* LAPLACE_RECALL_ROUTE_H */
