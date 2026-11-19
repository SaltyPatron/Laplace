#include "laplace/core/sql_catalog.h"
#include <stdatomic.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

typedef struct { const char *name, *parameters, *text; } SqlQuery;
#define SQL_QUERY(name, parameters, text) {name, parameters, text},
static const SqlQuery queries[] = {
#include "sql_catalog.def"
};
#undef SQL_QUERY

enum {
    QUERY_COUNT = (int)(sizeof(queries) / sizeof(queries[0])),
    HASH_CAP = 2048,
    HASH_MASK = HASH_CAP - 1
};
_Static_assert(QUERY_COUNT > 0 && QUERY_COUNT < 1536, "SQL catalog size exceeds hash table capacity");

static uint16_t hash_slots[HASH_CAP];
static atomic_int hash_state;

static uint32_t fnv1a(const char *s)
{
    uint32_t h = 2166136261u;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        h = (h ^ *p) * 16777619u;
    return h;
}

static void ensure_index(void)
{
    if (atomic_load_explicit(&hash_state, memory_order_acquire) == 2) return;
    int expected = 0;
    if (atomic_compare_exchange_strong_explicit(&hash_state, &expected, 1,
                                                memory_order_acq_rel, memory_order_acquire)) {
        for (int i = 0; i < QUERY_COUNT; ++i) {
            uint32_t pos = fnv1a(queries[i].name) & HASH_MASK;
            while (hash_slots[pos] != 0) pos = (pos + 1u) & HASH_MASK;
            hash_slots[pos] = (uint16_t)(i + 1);
        }
        atomic_store_explicit(&hash_state, 2, memory_order_release);
    } else {
        while (atomic_load_explicit(&hash_state, memory_order_acquire) != 2) { }
    }
}

static const SqlQuery *lookup(const char *name)
{
    if (name == NULL) return NULL;
    ensure_index();
    uint32_t pos = fnv1a(name) & HASH_MASK;
    for (;;) {
        uint16_t slot = hash_slots[pos];
        if (slot == 0) return NULL;
        const SqlQuery *q = &queries[slot - 1u];
        if (strcmp(name, q->name) == 0) return q;
        pos = (pos + 1u) & HASH_MASK;
    }
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
