/* Execution has no postmaster hooks, GUC registration or independent cache.
 * The installed substrate host owns those resources. Versioned SQL bindings
 * select this implementation; the PostgreSQL process remains running. */
#include "postgres.h"
#include "fmgr.h"
PG_MODULE_MAGIC;
