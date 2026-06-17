









#ifndef LAPLACE_RECALL_ROUTE_H
#define LAPLACE_RECALL_ROUTE_H

typedef struct RouteResult
{
    char *intent;
    char *phrase;
    char *phrase2;
    char *type_name;
} RouteResult;


extern char *text_to_cstr(Datum d);
extern char *trim_dup(const char *s);
extern bool  str_empty(const char *s);
extern char *lower_dup(const char *s);


extern void route_prompt_impl(const char *prompt, RouteResult *r);
extern void route_free(RouteResult *r);

#endif                          
