/* The execution module registers no hooks, GUCs or caches; the substrate host
 * library owns them. Versioned SQL bindings name this library, so a new build is
 * selected by the catalog while the server keeps running. */
#include "postgres.h"
#include "fmgr.h"
PG_MODULE_MAGIC;
