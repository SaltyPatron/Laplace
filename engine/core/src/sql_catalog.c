#include "laplace/core/sql_catalog.h"
#include <stddef.h>
#include <string.h>

typedef struct { const char *name, *parameters, *text; } SqlQuery;
#define SQL_QUERY(name, parameters, text) {name, parameters, text},
static const SqlQuery queries[] = {
#include "sql_catalog.def"
};
#undef SQL_QUERY

static const SqlQuery *lookup(const char *name)
{
    if (name == NULL) return NULL;
    for (size_t i = 0; i < sizeof(queries) / sizeof(queries[0]); ++i)
        if (strcmp(name, queries[i].name) == 0) return &queries[i];
    return NULL;
}
const char *laplace_sql_query_text(const char *name)
{
    const SqlQuery *q = lookup(name);
    return q ? q->text : NULL;
}
const char *laplace_sql_query_parameters(const char *name)
{
    const SqlQuery *q = lookup(name);
    return q ? q->parameters : NULL;
}
