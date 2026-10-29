/* The execution module registers no hooks or GUCs; the substrate host
 * library owns them. Versioned SQL bindings name this library, so a new build is
 * selected by the catalog while the server keeps running.
 *
 * Each versioned execution module links its own static core and Native closure,
 * giving it module-local perfcache state on every platform.
 * Redirecting laplace_perfcache_ready / laplace_highway_ready / laplace_vocabulary_ready /
 * laplace_chess_position_ready inside the execution module ensures the execution DLL's
 * static tables are populated on demand using the GUC paths owned by the host module. */
#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "varatt.h"
#include "laplace/core/qualifier_law.h"

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

bool
laplace_chess_position_ready(void)
{
    const char *path;
    int rc;

    if (chess_position_table_is_loaded())
        return true;
    path = GetConfigOptionByName("laplace_substrate.chess_position_perfcache_path", NULL, true);
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

PG_FUNCTION_INFO_V1(pg_laplace_hash128_lo);

Datum
pg_laplace_hash128_lo(PG_FUNCTION_ARGS)
{
    bytea         *b;
    const uint8_t *p;
    uint64_t       val = 0;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    b = PG_GETARG_BYTEA_PP(0);
    if (VARSIZE_ANY_EXHDR(b) < 8)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("hash128_lo: id must be at least 8 bytes (got %zu)",
                        (size_t) VARSIZE_ANY_EXHDR(b))));
    p = (const uint8_t *) VARDATA_ANY(b);
    for (int i = 0; i < 8; i++)
        val = (val << 8) | (uint64_t) p[i];
    PG_RETURN_INT64((int64) val);
}

PG_FUNCTION_INFO_V1(pg_laplace_qualifier_bit);

Datum
pg_laplace_qualifier_bit(PG_FUNCTION_ARGS)
{
    char *family = text_to_cstring(PG_GETARG_TEXT_PP(0));
    char *value  = text_to_cstring(PG_GETARG_TEXT_PP(1));
    int   bit    = laplace_qualifier_bit(family, value);

    if (bit < 0)
        ereport(ERROR,
                (errcode(ERRCODE_RAISE_EXCEPTION),
                 errmsg("undeclared claim qualifier %s/%s", family, value),
                 errhint("engine/manifest/qualifiers.toml")));
    pfree(family);
    pfree(value);
    PG_RETURN_INT32(bit);
}

PG_FUNCTION_INFO_V1(pg_laplace_has_qualifier);

Datum
pg_laplace_has_qualifier(PG_FUNCTION_ARGS)
{
    bytea         *mask;
    char          *family;
    char          *value;
    int            bit;
    Size           len;
    const uint8_t *bytes;
    bool           has;

    if (PG_ARGISNULL(1) || PG_ARGISNULL(2))
        PG_RETURN_NULL();
    family = text_to_cstring(PG_GETARG_TEXT_PP(1));
    value  = text_to_cstring(PG_GETARG_TEXT_PP(2));
    bit    = laplace_qualifier_bit(family, value);
    if (bit < 0)
        ereport(ERROR,
                (errcode(ERRCODE_RAISE_EXCEPTION),
                 errmsg("undeclared claim qualifier %s/%s", family, value),
                 errhint("engine/manifest/qualifiers.toml")));
    pfree(family);
    pfree(value);
    if (PG_ARGISNULL(0))
        PG_RETURN_BOOL(false);
    mask  = PG_GETARG_BYTEA_PP(0);
    len   = VARSIZE_ANY_EXHDR(mask);
    if ((Size) (bit >> 3) >= len)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("index %d out of valid range, 0..%d",
                        bit, (int) (len * 8) - 1)));
    bytes = (const uint8_t *) VARDATA_ANY(mask);
    has   = ((bytes[bit >> 3] >> (bit & 7)) & 1) != 0;
    PG_RETURN_BOOL(has);
}

PG_MODULE_MAGIC;
