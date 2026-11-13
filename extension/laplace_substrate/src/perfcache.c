#include "postgres.h"

#include "fmgr.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/memutils.h"

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/vocabulary_table.h"
#include "laplace/core/perfcache_format.h"

#include "perfcache_native.h"

static char *perfcache_path = NULL;
static char *perfcache_profile = NULL;
static char *highway_perfcache_path = NULL;
static char *highway_profile = NULL;
static char *vocabulary_perfcache_path = NULL;
static int native_mkl_threads = 1;

void
laplace_substrate_perfcache_init(void)
{
    DefineCustomStringVariable(
        "laplace_substrate.perfcache_path",
        "Path to the T0 perfcache blob (tier0.bin or shard prefix).",
        "Empty disables perfcache-backed lookups; consumers fall back or error per their contract.",
        &perfcache_path,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomStringVariable(
        "laplace_substrate.perfcache_profile",
        "Tier-0 modular branch profile (ascii, utf, cjk, bmp, full, or + separated shards).",
        "Empty selects the full Unicode floor (or LAPLACE_TIER0_PROFILE when set).",
        &perfcache_profile,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomStringVariable(
        "laplace_substrate.highway_perfcache_path",
        "Path to the highway perfcache blob (laplace_highway_perfcache.bin).",
        "Empty disables highway plane-selection functions; they error until configured.",
        &highway_perfcache_path,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomStringVariable(
        "laplace_substrate.highway_profile",
        "Highway domain profile (core, wordnet, frames, lexical, full).",
        "Empty selects full (or LAPLACE_HIGHWAY_PROFILE when set).",
        &highway_profile,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomStringVariable(
        "laplace_substrate.vocabulary_perfcache_path",
        "Path to the governed vocabulary blob (laplace_vocabulary_perfcache.bin).",
        "UPOS, UD relations and subtypes, features and feature values as content records "
        "with their governed codes. Empty disables vocabulary reads; they error until configured.",
        &vocabulary_perfcache_path,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomIntVariable(
        "laplace_substrate.native_mkl_threads",
        "MKL thread count for this backend when mkl_threads is not passed from env (PG has no env.cmd).",
        NULL,
        &native_mkl_threads,
        1,
        1,
        64,
        PGC_SUSET,
        0,
        NULL, NULL, NULL);
    MarkGUCPrefixReserved("laplace_substrate");
}

int
laplace_substrate_native_mkl_threads(void)
{
    return native_mkl_threads;
}

const char *
laplace_substrate_perfcache_path(void)
{
    return perfcache_path;
}

const char *
laplace_substrate_highway_perfcache_path(void)
{
    return highway_perfcache_path;
}

const char *
laplace_substrate_vocabulary_perfcache_path(void)
{
    return vocabulary_perfcache_path;
}

static int
load_configured_t0_perfcache(void)
{
    if (perfcache_profile != NULL && perfcache_profile[0] != '\0')
        return codepoint_table_load_branches(perfcache_path, perfcache_profile);
    return codepoint_table_load_perfcache(perfcache_path);
}

bool
laplace_perfcache_ready(void)
{
    int rc;

    if (codepoint_table_is_loaded())
        return true;
    if (perfcache_path == NULL || perfcache_path[0] == '\0')
        return false;

    rc = load_configured_t0_perfcache();
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load perfcache \"%s\" (rc=%d)",
                        perfcache_path, rc),
                 errdetail("rc -1: open/stat/mmap failure; -2: bad magic/version/profile; "
                           "-3: record count/size mismatch; -4: body CRC mismatch."),
                 errhint("Fix laplace_substrate.perfcache_path or redeploy the blob "
                         "(install-extensions.cmd stages it).")));
    return true;
}

bool
laplace_highway_ready(void)
{
    if (highway_table_is_loaded())
        return true;
    if (highway_perfcache_path == NULL || highway_perfcache_path[0] == '\0')
        return false;

    if (((highway_profile != NULL && highway_profile[0] != '\0')
            ? highway_table_load_profile(highway_perfcache_path, highway_profile)
            : highway_table_load(highway_perfcache_path)) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load highway perfcache \"%s\"",
                        highway_perfcache_path),
                 errhint("Fix laplace_substrate.highway_perfcache_path or redeploy the "
                         "blob (install-extensions.cmd stages it).")));
    return true;
}

bool
laplace_vocabulary_ready(void)
{
    int rc;

    if (vocabulary_table_is_loaded())
        return true;
    if (vocabulary_perfcache_path == NULL || vocabulary_perfcache_path[0] == '\0')
        return false;

    rc = vocabulary_table_load(vocabulary_perfcache_path);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load vocabulary perfcache \"%s\" (rc=%d)",
                        vocabulary_perfcache_path, rc),
                 errdetail("rc -1: open/stat/mmap failure; -2: bad magic/version; -3: layout; "
                           "-4: body checksum mismatch; -5: a vocabulary's codes are not 1..N."),
                 errhint("Fix laplace_substrate.vocabulary_perfcache_path or redeploy the blob.")));
    return true;
}

/* Builds the codepoint reverse index (id -> codepoint) by round-tripping one
 * codepoint and fails if the round trip does not hold. */
static void
rev_index_ensure(void)
{
    const codepoint_entry_t *probe = codepoint_table_lookup('A');
    uint32_t cp;
    if (probe == NULL || codepoint_table_lookup_id(&probe->hash, &cp) != 0 || cp != 'A')
        ereport(ERROR, (errmsg("laplace_substrate: codepoint reverse index initialization failed")));
}

void
laplace_substrate_perfcache_prewarm(void)
{
    int rc;

    if (!process_shared_preload_libraries_in_progress)
        return;
#ifdef EXEC_BACKEND
    if (IsUnderPostmaster)
        return;
#endif

    if (perfcache_path != NULL && perfcache_path[0] != '\0')
    {
        rc = load_configured_t0_perfcache();
        if (rc == 0)
        {
            rev_index_ensure();
            ereport(LOG,
                    (errmsg("laplace_substrate: perfcache prewarmed in postmaster (\"%s\")",
                            perfcache_path)));
        }
        else
            ereport(WARNING,
                    (errmsg("laplace_substrate: perfcache prewarm failed (rc=%d, \"%s\"); "
                            "backends fall back to lazy load", rc, perfcache_path)));
    }

    if (highway_perfcache_path != NULL && highway_perfcache_path[0] != '\0')
    {
        if (((highway_profile != NULL && highway_profile[0] != '\0')
            ? highway_table_load_profile(highway_perfcache_path, highway_profile)
            : highway_table_load(highway_perfcache_path)) != 0)
            ereport(WARNING,
                    (errmsg("laplace_substrate: highway prewarm failed (\"%s\"); "
                            "backends fall back to lazy load", highway_perfcache_path)));
    }

    if (vocabulary_perfcache_path != NULL && vocabulary_perfcache_path[0] != '\0')
    {
        rc = vocabulary_table_load(vocabulary_perfcache_path);
        if (rc != 0)
            ereport(WARNING,
                    (errmsg("laplace_substrate: vocabulary perfcache prewarm failed "
                            "(rc=%d, \"%s\"); backends fall back to lazy load",
                            rc, vocabulary_perfcache_path)));
    }
}

bool
laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp)
{
    hash128_t key;
    if (!laplace_perfcache_ready()) return false;
    memcpy(&key, id, sizeof(key));
    return codepoint_table_lookup_id(&key, out_cp) == 0;
}

PG_FUNCTION_INFO_V1(pg_laplace_perfcache_receipt);

Datum
pg_laplace_perfcache_receipt(PG_FUNCTION_ARGS)
{
    hash128_t receipt;
    bytea *out;

    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("perfcache_receipt requires the configured T0 perfcache")));

    if (codepoint_table_copy_receipt(&receipt) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_CORRUPTED),
                 errmsg("perfcache_receipt could not read the loaded T0 receipt")));

    out = (bytea *) palloc(VARHDRSZ + sizeof(receipt));
    SET_VARSIZE(out, VARHDRSZ + sizeof(receipt));
    memcpy(VARDATA(out), &receipt, sizeof(receipt));
    PG_RETURN_BYTEA_P(out);
}

static void
text_arg_root_id(text *t, const char *fn_name, const char *param_name, hash128_t *out)
{
    const char *data = VARDATA_ANY(t);
    Size        len  = VARSIZE_ANY_EXHDR(t);
    int         rc;

    if (len == 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("%s: %s must not be empty", fn_name, param_name)));
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("%s requires the T0 perfcache", fn_name)));
    rc = laplace_content_root_id((const uint8_t *) data, (size_t) len, out);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("%s: content decomposition failed for %s (rc=%d)",
                        fn_name, param_name, rc)));
}

PG_FUNCTION_INFO_V1(pg_laplace_property_value_id);

Datum
pg_laplace_property_value_id(PG_FUNCTION_ARGS)
{
    hash128_t children[2];
    bytea    *out;

    text_arg_root_id(PG_GETARG_TEXT_PP(0), "property_value_id", "property", &children[0]);
    text_arg_root_id(PG_GETARG_TEXT_PP(1), "property_value_id", "value", &children[1]);

    out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(out, VARHDRSZ + sizeof(hash128_t));
    hash128_merkle(0, children, 2, (hash128_t *) VARDATA(out));
    PG_RETURN_BYTEA_P(out);
}
