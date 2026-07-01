
















#include "postgres.h"

#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/memutils.h"

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"

#include "perfcache_native.h"

static char *perfcache_path = NULL;
static bool ingest_bulk_novel = false;
static int native_mkl_threads = 1;

void
laplace_substrate_perfcache_init(void)
{
    DefineCustomStringVariable(
        "laplace_substrate.perfcache_path",
        "Path to the T0 perfcache blob (laplace_t0_perfcache.bin).",
        "Empty disables perfcache-backed lookups; consumers fall back or error per their contract.",
        &perfcache_path,
        "",
        PGC_SIGHUP,
        0,
        NULL, NULL, NULL);
    DefineCustomBoolVariable(
        "laplace_substrate.ingest_bulk_novel",
        "Skip merge anti-join for batches the client already proved novel (bulk-fresh path).",
        NULL,
        &ingest_bulk_novel,
        false,
        PGC_USERSET,
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

bool
laplace_perfcache_ready(void)
{
    int rc;

    if (codepoint_table_is_loaded())
        return true;
    if (perfcache_path == NULL || perfcache_path[0] == '\0')
        return false;

    rc = codepoint_table_load_perfcache(perfcache_path);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_CONFIG_FILE_ERROR),
                 errmsg("laplace_substrate: failed to load perfcache \"%s\" (rc=%d)",
                        perfcache_path, rc),
                 errdetail("rc -1: open/stat/mmap failure; -2: bad magic/version; "
                           "-3: record count/size mismatch; -4: body CRC mismatch."),
                 errhint("Fix laplace_substrate.perfcache_path or redeploy the blob "
                         "(install-extensions.cmd stages it).")));
    return true;
}



static uint32_t *rev_idx = NULL;
static uint64_t  rev_count = 0;
static const codepoint_entry_t *rev_records = NULL;

static int
rev_cmp(const void *pa, const void *pb)
{
    uint32_t a = *(const uint32_t *) pa;
    uint32_t b = *(const uint32_t *) pb;
    return memcmp(&rev_records[a].hash, &rev_records[b].hash, sizeof(hash128_t));
}

static void
rev_index_ensure(void)
{
    const codepoint_entry_t *records;
    uint64_t count;

    if (rev_idx != NULL)
        return;

    if (codepoint_table_records(&records, &count) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_substrate: perfcache records unavailable for reverse index")));

    {
        uint32_t *idx = (uint32_t *)
            MemoryContextAlloc(TopMemoryContext, sizeof(uint32_t) * count);
        for (uint64_t i = 0; i < count; ++i)
            idx[i] = (uint32_t) i;
        rev_records = records;
        qsort(idx, count, sizeof(uint32_t), rev_cmp);
        rev_count = count;
        rev_idx = idx;
    }
}

bool
laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp)
{
    uint64_t lo, hi;

    if (!laplace_perfcache_ready())
        return false;
    rev_index_ensure();

    lo = 0;
    hi = rev_count;
    while (lo < hi)
    {
        uint64_t mid = lo + ((hi - lo) >> 1);
        uint32_t cp = rev_idx[mid];
        int c = memcmp(id, &rev_records[cp].hash, sizeof(hash128_t));

        if (c < 0)
            hi = mid;
        else if (c > 0)
            lo = mid + 1;
        else
        {
            
            if (cp == 0 || (cp >= 0xD800 && cp <= 0xDFFF))
                return false;
            *out_cp = cp;
            return true;
        }
    }
    return false;
}



PG_FUNCTION_INFO_V1(pg_laplace_word_id);

Datum
pg_laplace_word_id(PG_FUNCTION_ARGS)
{
    text       *t = PG_GETARG_TEXT_PP(0);
    const char *data = VARDATA_ANY(t);
    Size        len = VARSIZE_ANY_EXHDR(t);
    hash128_t   id;
    int         rc;
    bytea      *out;

    if (len == 0)
        PG_RETURN_NULL();

    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("word_id requires the T0 perfcache"),
                 errhint("ALTER SYSTEM SET laplace_substrate.perfcache_path = '<blob>'; "
                         "SELECT pg_reload_conf(); (install-extensions.cmd wires this)")));

    rc = laplace_content_root_id((const uint8_t *) data, (size_t) len, &id);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("word_id: content decomposition failed (rc=%d)", rc)));

    out = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(out, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(out), &id, sizeof(hash128_t));
    PG_RETURN_BYTEA_P(out);
}

PG_FUNCTION_INFO_V1(pg_laplace_codepoint_for_id);

Datum
pg_laplace_codepoint_for_id(PG_FUNCTION_ARGS)
{
    bytea   *b = PG_GETARG_BYTEA_PP(0);
    uint32_t cp;

    if (VARSIZE_ANY_EXHDR(b) != sizeof(hash128_t))
        PG_RETURN_NULL();
    if (!laplace_perfcache_codepoint_for_id((const uint8_t *) VARDATA_ANY(b), &cp))
        PG_RETURN_NULL();
    PG_RETURN_INT32((int32) cp);
}





PG_FUNCTION_INFO_V1(pg_laplace_is_all_whitespace);

Datum
pg_laplace_is_all_whitespace(PG_FUNCTION_ARGS)
{
    text *t;

    if (PG_ARGISNULL(0))
        PG_RETURN_BOOL(false);
    if (!laplace_perfcache_ready())
        ereport(ERROR,
                (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                 errmsg("is_all_whitespace requires the T0 perfcache"),
                 errhint("ALTER SYSTEM SET laplace_substrate.perfcache_path = '<blob>'; "
                         "SELECT pg_reload_conf();")));
    t = PG_GETARG_TEXT_PP(0);
    PG_RETURN_BOOL(laplace_text_is_all_whitespace(
        (const uint8_t *) VARDATA_ANY(t), VARSIZE_ANY_EXHDR(t)) != 0);
}
