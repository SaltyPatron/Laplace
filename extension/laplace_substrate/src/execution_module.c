/* The execution module registers no hooks or GUCs; the substrate host
 * library owns them. Versioned SQL bindings name this library, so a new build is
 * selected by the catalog while the server keeps running.
 *
 * Each versioned execution module links its own static core and Native closure,
 * giving it module-local perfcache state on every platform.
 * Redirecting laplace_perfcache_ready / laplace_highway_ready / laplace_vocabulary_ready
 * inside the execution module ensures the execution DLL's static tables are populated
 * on demand using the GUC paths owned by the host module. */
#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "varatt.h"

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
    path = GetConfigOptionByName("laplace_substrate.perfcache_path", NULL, true);
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
laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp)
{
    hash128_t key;

    if (!laplace_perfcache_ready())
        return false;
    memcpy(&key, id, sizeof(key));
    return codepoint_table_lookup_id(&key, out_cp) == 0;
}

bool
laplace_highway_ready(void)
{
    const char *path;

    if (highway_table_is_loaded())
        return true;
    path = GetConfigOptionByName("laplace_substrate.highway_perfcache_path", NULL, true);
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
    path = GetConfigOptionByName("laplace_substrate.vocabulary_perfcache_path", NULL, true);
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

PG_MODULE_MAGIC;
