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
    BROWSE_MATCH_EXACT = 0,
    BROWSE_MATCH_MEMBER = 1,
    BROWSE_MATCH_CONTAINER = 2,
    BROWSE_MATCH_NAME = 3
} BrowseMatchKind;

typedef struct {
    hash128_t id;
    hash128_t name;
    hash128_t type;
    int64 rating, rd, witnesses;
    int16 tier;
    uint8 match_kind;
    bool exists;
} BrowseHit;

static const char *
match_kind_name(uint8 kind)
{
    switch ((BrowseMatchKind) kind) {
        case BROWSE_MATCH_EXACT: return "exact";
        case BROWSE_MATCH_MEMBER: return "member";
        case BROWSE_MATCH_CONTAINER: return "container";
        case BROWSE_MATCH_NAME: return "name";
    }
    return "unknown";
}

static void
promote_direct_hit(HTAB *hits, const hash128_t *id, BrowseMatchKind kind)
{
    bool found;
    BrowseHit *hit = hash_search(hits, id, HASH_ENTER, &found);
    if (!found) {
        memset(hit, 0, sizeof(*hit));
        hit->id = *id;
        hit->name = *id;
        hit->match_kind = (uint8) kind;
        return;
    }
    if ((uint8) kind < hit->match_kind) {
        hit->match_kind = (uint8) kind;
        hit->name = *id;
    }
}

static int
hit_compare(const void *a, const void *b)
{
    const BrowseHit *x = a, *y = b;
    if (x->match_kind != y->match_kind)
        return x->match_kind < y->match_kind ? -1 : 1;
    if (x->match_kind == BROWSE_MATCH_NAME) {
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
    ArrayType *member_array = PG_ARGISNULL(0) ? NULL : PG_GETARG_ARRAYTYPE_P(0);
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "browse: SPI connect failed");
    hash128_t *names = member_array == NULL ? NULL : candidate_names(
        member_array, capacity, &count, &truncated);
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
            if (!found || (old->match_kind == BROWSE_MATCH_NAME &&
                (mu > old_mu || (mu == old_mu && (hit.witnesses > old->witnesses ||
                (hit.witnesses == old->witnesses && id_compare(&hit.name, &old->name) < 0))))))
                *old = hit;
        }
        SPI_freetuptable(SPI_tuptable);
        if (n == 0) break;
    }
    SPI_cursor_close(cursor);

    /* Exact input and decomposed members are distinct result classes. Merely
     * calculating these ids still does not make them present; the facets batch
     * below is the presence gate. */
    if (!PG_ARGISNULL(1)) {
        hash128_t exact = datum_to_hash128(PG_GETARG_DATUM(1));
        promote_direct_hit(hits, &exact, BROWSE_MATCH_EXACT);
    }
    if (member_array != NULL) {
        Datum *member_datums;
        bool *member_nulls;
        int n_members;
        deconstruct_array(member_array, BYTEAOID, -1, false, TYPALIGN_INT,
                          &member_datums, &member_nulls, &n_members);
        for (int i = 0; i < n_members; ++i) {
            if (member_nulls[i]) continue;
            hash128_t member = datum_to_hash128(member_datums[i]);
            promote_direct_hit(hits, &member, BROWSE_MATCH_MEMBER);
        }
    }

    /* Containment is a product result in its own right. Attested names above
     * attach domain entities; they do not hide the matched content DAG. */
    for (int i = 0; i < count; ++i)
        promote_direct_hit(hits, &names[i], BROWSE_MATCH_CONTAINER);

    /* One batch hydrates all matched identities, including exact/member/container
     * arms. Unwitnessed calculated ids disappear here instead of becoming empty
     * entity results. */
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
        bool scored = hit->match_kind == BROWSE_MATCH_NAME;
        Datum v[] = {hash128_to_datum(&hit->id), Int16GetDatum(hit->tier), hash128_to_datum(&hit->type),
            hash128_to_datum(&hit->name), CStringGetTextDatum(match_kind_name(hit->match_kind)),
            fp_display_numeric(hit->rating), fp_display_numeric(hit->rd), eff_mu_display_numeric(hit->rating, hit->rd),
            Int64GetDatum(hit->witnesses), Int64GetDatum(count), BoolGetDatum(truncated), Int64GetDatum(n)};
        bool nulls[12] = {false};
        nulls[5] = nulls[6] = nulls[7] = !scored;
        tuplestore_putvalues(r->setResult, r->setDesc, v, nulls);
    }
    hash_destroy(hits);
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
