#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "spi_common.h"
#include "laplace/core/sql_catalog.h"
#include "spi_nested.h"
#include "content_membership_read.h"

/* Browse owns composition, deduplication, ranking, paging and receipts in C.
 * PostgreSQL executes retained, typed set reads. No generated SQL, SQL ranking,
 * recursive query or per-entity probe. Cursor fetches bound transfer memory. */
static SPIPlanPtr names_plan, facets_plan;

static SPIPlanPtr
plan(SPIPlanPtr *slot, const char *sql, int n, Oid *types)
{
    if (*slot == NULL) {
        *slot = SPI_prepare_cursor(sql, n, types, CURSOR_OPT_GENERIC_PLAN);
        if (*slot == NULL || SPI_keepplan(*slot) != 0)
            elog(ERROR, "browse: cannot retain indexed read plan");
    }
    return *slot;
}

static HTAB *
id_table(const char *name, Size entrysize)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entrysize;
    return hash_create(name, 256, &ctl, HASH_ELEM | HASH_BLOBS);
}

static int
id_compare(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static ArrayType *
id_array(HTAB *ids)
{
    long n = hash_get_num_entries(ids);
    HASH_SEQ_STATUS scan;
    hash128_t *id;
    if (n > MaxAllocSize / sizeof(Datum))
        elog(ERROR, "browse: identity set exceeds allocation capacity");
    Datum *items = palloc(Max(n, 1) * sizeof(Datum));
    int i = 0;
    hash_seq_init(&scan, ids);
    while ((id = hash_seq_search(&scan)) != NULL)
        items[i++] = hash128_to_datum(id);
    return construct_array(items, i, BYTEAOID, -1, false, TYPALIGN_INT);
}

/* Reuse the installed composition operation. Members are the decomposer's
 * whole content identities. No floor expansion, rendered search, or private
 * graph walk: the trajectory containment index elects the matching manifests.
 * The compatibility capacity cannot hide candidates. */
static hash128_t *
candidate_names(ArrayType *members, int capacity, int *count, bool *truncated)
{
    (void)capacity;
    *count = 0; *truncated = false;
    if (ArrayGetNItems(ARR_NDIM(members), ARR_DIMS(members)) == 0) return NULL;
    return laplace_content_membership_entities(members, true, count);
}

PG_FUNCTION_INFO_V1(pg_laplace_word_containers_containing_all);
Datum
pg_laplace_word_containers_containing_all(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *)fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) return (Datum)0;
    bool truncated;
    int count;
    hash128_t *ids = candidate_names(PG_GETARG_ARRAYTYPE_P(0),
        PG_ARGISNULL(1) ? 0 : Max(PG_GETARG_INT32(1), 0), &count, &truncated);
    for (int i = 0; i < count; ++i) {
        Datum values[] = {hash128_to_datum(&ids[i]), Int64GetDatum(count), BoolGetDatum(truncated)};
        bool nulls[] = {false, false, false};
        tuplestore_putvalues(r->setResult, r->setDesc, values, nulls);
    }
    return (Datum)0;
}

typedef enum {
    BROWSE_MATCH_NAME = 0,
    BROWSE_MATCH_SURFACE,
    BROWSE_MATCH_CONTAINS_ALL,
    BROWSE_MATCH_CONSTITUENT
} BrowseMatchKind;

typedef struct {
    hash128_t id;
    hash128_t name;
    hash128_t type;
    int64 rating, rd, witnesses;
    int16 tier;
    BrowseMatchKind match_kind;
    bool exists;
} BrowseHit;

static const char *
match_kind_text(BrowseMatchKind kind)
{
    switch (kind) {
        case BROWSE_MATCH_NAME: return "name";
        case BROWSE_MATCH_SURFACE: return "surface";
        case BROWSE_MATCH_CONTAINS_ALL: return "contains_all";
        case BROWSE_MATCH_CONSTITUENT: return "constituent";
    }
    elog(ERROR, "browse: invalid match kind");
    return "";
}

static void
add_structural_hit(HTAB *hits, const hash128_t *id, BrowseMatchKind match_kind)
{
    bool found;
    BrowseHit *hit = hash_search(hits, id, HASH_ENTER, &found);
    if (!found) {
        memset(hit, 0, sizeof(*hit));
        hit->id = *id;
        hit->name = *id;
        hit->match_kind = match_kind;
    }
}

static void
add_member_hits(HTAB *hits, ArrayType *members)
{
    if (members == NULL) return;
    if (ARR_NDIM(members) > 1 || ARR_ELEMTYPE(members) != BYTEAOID)
        elog(ERROR, "browse: members must be a 1-D bytea array");
    Datum *values;
    bool *nulls;
    int count;
    deconstruct_array(members, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    for (int i = 0; i < count; ++i) {
        if (nulls[i]) continue;
        bytea *value = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            elog(ERROR, "browse: members must contain 16-byte identities");
        hash128_t id = datum_to_hash128(values[i]);
        add_structural_hit(hits, &id, BROWSE_MATCH_CONSTITUENT);
    }
    pfree(values);
    pfree(nulls);
}

static int
hit_compare(const void *a, const void *b)
{
    const BrowseHit *x = a, *y = b;
    bool x_structural = x->match_kind != BROWSE_MATCH_NAME;
    bool y_structural = y->match_kind != BROWSE_MATCH_NAME;
    if (x_structural != y_structural) return x_structural ? 1 : -1;
    if (!x_structural) {
        int64 xm = eff_mu_display_fp(x->rating, x->rd), ym = eff_mu_display_fp(y->rating, y->rd);
        if (xm != ym) return xm > ym ? -1 : 1;
        if (x->witnesses != y->witnesses) return x->witnesses > y->witnesses ? -1 : 1;
    }
    return id_compare(&x->id, &y->id);
}

PG_FUNCTION_INFO_V1(pg_laplace_browse_named_entities);
Datum
pg_laplace_browse_named_entities(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *)fcinfo->resultinfo;
    int offset = PG_ARGISNULL(2) ? 0 : Max(PG_GETARG_INT32(2), 0);
    int limit = PG_ARGISNULL(3) ? 0 : Max(PG_GETARG_INT32(3), 0);
    int capacity = PG_ARGISNULL(4) ? 0 : Max(PG_GETARG_INT32(4), 0);
    if (limit == 0) return (Datum)0;
    bool spi_top = false, truncated = false;
    int count = 0;
    Oid array_type[] = {BYTEAARRAYOID};
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "browse: SPI connect failed");
    ArrayType *members = PG_ARGISNULL(0) ? NULL : PG_GETARG_ARRAYTYPE_P(0);
    hash128_t *names = members == NULL ? NULL : candidate_names(
        members, capacity, &count, &truncated);
    HTAB *hits = id_table("browse selected names", sizeof(BrowseHit));
    Datum *name_datums = palloc(Max(count, 1) * sizeof(Datum));
    for (int i = 0; i < count; ++i) name_datums[i] = hash128_to_datum(&names[i]);
    Datum args[] = {PointerGetDatum(construct_array(name_datums, count, BYTEAOID, -1, false, TYPALIGN_INT))};
    SPIPlanPtr p = plan(&names_plan,
        laplace_sql_query_text("browse.names"),
        1, array_type);
    Portal cursor = SPI_cursor_open(NULL, p, args, NULL, true);
    if (cursor == NULL) elog(ERROR, "browse: cannot open name read");
    for (;;) {
        CHECK_FOR_INTERRUPTS();
        SPI_cursor_fetch(cursor, true, 1024);
        uint64 n = SPI_processed;
        for (uint64 i = 0; i < n; ++i) {
            bool isnull, found;
            Datum v[5];
            for (int j = 0; j < 5; ++j) v[j] = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, j + 1, &isnull);
            BrowseHit hit = {0};
            hit.id = datum_to_hash128(v[0]); hit.name = datum_to_hash128(v[1]);
            hit.rating = DatumGetInt64(v[2]); hit.rd = DatumGetInt64(v[3]); hit.witnesses = DatumGetInt64(v[4]);
            hit.match_kind = BROWSE_MATCH_NAME;
            if (laplace_glicko2_refuted(hit.rating, hit.rd)) continue;
            BrowseHit *old = hash_search(hits, &hit.id, HASH_ENTER, &found);
            int64 mu = laplace_effective_mu_fp(hit.rating, hit.rd);
            int64 old_mu = found ? laplace_effective_mu_fp(old->rating, old->rd) : 0;
            if (!found || mu > old_mu || (mu == old_mu && (hit.witnesses > old->witnesses ||
                (hit.witnesses == old->witnesses && id_compare(&hit.name, &old->name) < 0)))) *old = hit;
        }
        SPI_freetuptable(SPI_tuptable);
        if (n == 0) break;
    }
    SPI_cursor_close(cursor);
    if (!PG_ARGISNULL(1)) {
        hash128_t exact = datum_to_hash128(PG_GETARG_DATUM(1));
        add_structural_hit(hits, &exact, BROWSE_MATCH_SURFACE);
    }
    /* Containment is a product result in its own right. Attested names above
     * attach domain entities; they do not hide the matched content DAG. */
    for (int i = 0; i < count; ++i)
        add_structural_hit(hits, &names[i], BROWSE_MATCH_CONTAINS_ALL);
    /* The query's witnessed words are also real substrate results. A missing
     * higher-tier phrase must not erase the canonical constituents that formed
     * the query. Existing stronger matches retain precedence by insertion order. */
    add_member_hits(hits, members);
    /* One batch hydrates all matched identities, including exact, containment,
     * and constituent arms. Unwitnessed computed ids disappear here. */
    args[0] = PointerGetDatum(id_array(hits));
    p = plan(&facets_plan, laplace_sql_query_text("entity.facets"), 1, array_type);
    if (SPI_execute_plan(p, args, NULL, true, 0) != SPI_OK_SELECT) elog(ERROR, "browse: facet read failed");
    for (uint64 i = 0; i < SPI_processed; ++i) {
        bool isnull;
        HeapTuple t = SPI_tuptable->vals[i]; TupleDesc d = SPI_tuptable->tupdesc;
        hash128_t id = datum_to_hash128(SPI_getbinval(t, d, 1, &isnull));
        BrowseHit *hit = hash_search(hits, &id, HASH_FIND, NULL);
        if (hit) {
            hit->exists = true; hit->tier = DatumGetInt16(SPI_getbinval(t, d, 2, &isnull));
            hit->type = datum_to_hash128(SPI_getbinval(t, d, 3, &isnull));
        }
    }
    SPI_freetuptable(SPI_tuptable);
    long nhits = hash_get_num_entries(hits);
    if (nhits > MaxAllocSize / sizeof(BrowseHit)) elog(ERROR, "browse: matched set exceeds allocation capacity");
    BrowseHit *ordered = palloc(Max(nhits, 1) * sizeof(BrowseHit));
    HASH_SEQ_STATUS scan; BrowseHit *hit; long n = 0;
    hash_seq_init(&scan, hits);
    while ((hit = hash_seq_search(&scan)) != NULL) if (hit->exists) ordered[n++] = *hit;
    qsort(ordered, n, sizeof(BrowseHit), hit_compare);
    for (int64 i = offset; i < n && i < (int64)offset + limit; ++i) {
        hit = &ordered[i];
        bool structural = hit->match_kind != BROWSE_MATCH_NAME;
        Datum v[] = {hash128_to_datum(&hit->id), Int16GetDatum(hit->tier), hash128_to_datum(&hit->type),
            hash128_to_datum(&hit->name), CStringGetTextDatum(match_kind_text(hit->match_kind)),
            fp_display_numeric(hit->rating), fp_display_numeric(hit->rd), eff_mu_display_numeric(hit->rating, hit->rd),
            Int64GetDatum(hit->witnesses), Int64GetDatum(count), BoolGetDatum(truncated), Int64GetDatum(n)};
        bool nulls[12] = {false};
        nulls[5] = nulls[6] = nulls[7] = structural;
        tuplestore_putvalues(r->setResult, r->setDesc, v, nulls);
    }
    hash_destroy(hits);
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
