/* The execution module registers no hooks or GUCs; the substrate host
 * library owns them. Versioned SQL bindings name this library, so a new build is
 * selected by the catalog while the server keeps running.
 *
 * On Windows, both laplace_substrate.dll and laplace_execution_<hash>.dll link
 * laplace_core_static.lib, giving each DLL its own module-local perfcache statics.
 * Redirecting laplace_perfcache_ready / laplace_highway_ready / laplace_vocabulary_ready /
 * laplace_chess_position_ready inside the execution module ensures the execution DLL's
 * static tables are populated on demand using the GUC paths owned by the host module. */
#include "postgres.h"
#include "fmgr.h"

#ifdef WIN32
#include "laplace/core/chess_position_table.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/vocabulary_table.h"
#include "perfcache_native.h"

bool
laplace_perfcache_ready(void)
{
    const char *path;
    int rc;

    if (codepoint_table_is_loaded())
        return true;
    path = laplace_substrate_perfcache_path();
    if (path == NULL || path[0] == '\0')
        return false;
    rc = codepoint_table_load_perfcache(path);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load perfcache \"%s\" in execution module (rc=%d)",
                        path, rc)));
    return true;
}

bool
laplace_highway_ready(void)
{
    const char *path;

    if (highway_table_is_loaded())
        return true;
    path = laplace_substrate_highway_perfcache_path();
    if (path == NULL || path[0] == '\0')
        return false;
    if (highway_table_load(path) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load highway perfcache \"%s\" in execution module",
                        path)));
    return true;
}

bool
laplace_vocabulary_ready(void)
{
    const char *path;
    int rc;

    if (vocabulary_table_is_loaded())
        return true;
    path = laplace_substrate_vocabulary_perfcache_path();
    if (path == NULL || path[0] == '\0')
        return false;
    rc = vocabulary_table_load(path);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load vocabulary perfcache \"%s\" in execution module (rc=%d)",
                        path, rc)));
    return true;
}

bool
laplace_chess_position_ready(void)
{
    const char *path;
    int rc;

    if (chess_position_table_is_loaded())
        return true;
    path = laplace_substrate_chess_position_perfcache_path();
    if (path == NULL || path[0] == '\0')
        return false;
    rc = chess_position_table_load(path);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load chess position perfcache \"%s\" in execution module (rc=%d)",
                        path, rc)));
    return true;
}
#endif

PG_MODULE_MAGIC;
