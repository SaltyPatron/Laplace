#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "spi_common.h"
#include "laplace/core/sql_catalog.h"
#include "spi_nested.h"
#include "content_membership_read.h"
#include "spi_stream.h"

/* Resolves a query's constituent ids to ranked entities. Membership,
 * deduplication, ranking and paging run in C over retained typed set reads;
 * borrowed executor slots bound transfer memory. */
static SPIPlanPtr names_plan, facets_plan;

static SPIPlanPtr
plan(SPIPlanPtr *slot, const char *sql, int n, Oid *types)
{
    if (*slot == NULL) {
        *slot = SPI_prepare_cursor(sql, n, types, CURSOR_OPT_PARALLEL_OK);
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

/* Entities whose trajectories contain every member, from the trajectory
 * membership index. `capacity` does not bound the set, so `truncated` is
 * always false. */
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
    bool exists, has_type;
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

/* Structural distance from the query: the entities it names, the content it is,
 * its constituents, then the structures that contain all of them. */
static int
match_rank(BrowseMatchKind kind)
{
    switch (kind) {
        case BROWSE_MATCH_NAME: return 0;
        case BROWSE_MATCH_SURFACE: return 1;
        case BROWSE_MATCH_CONSTITUENT: return 2;
        case BROWSE_MATCH_CONTAINS_ALL: return 3;
    }
    return 4;
}

static int
hit_compare(const void *a, const void *b)
{
    const BrowseHit *x = a, *y = b;
    int xr = match_rank(x->match_kind), yr = match_rank(y->match_kind);
    if (xr != yr) return xr < yr ? -1 : 1;
    if (x->match_kind == BROWSE_MATCH_NAME) {
        int64 xm = eff_mu_display_fp(x->rating, x->rd), ym = eff_mu_display_fp(y->rating, y->rd);
        if (xm != ym) return xm > ym ? -1 : 1;
        if (x->witnesses != y->witnesses) return x->witnesses > y->witnesses ? -1 : 1;
    } else if (x->tier != y->tier) {
        /* Among structural hits, a lower tier sits closer to the query. */
        return x->tier < y->tier ? -1 : 1;
    }
    return id_compare(&x->id, &y->id);
}

PG_FUNCTION_INFO_V1(pg_laplace_browse_named_entities);
static void
browse_name(TupleTableSlot *row, void *context)
{
    HTAB *hits = context;
    bool isnull, found;
    Datum v[5];
    for (int j = 0; j < 5; ++j) v[j] = slot_getattr(row, j + 1, &isnull);
    BrowseHit hit = {0};
    hit.id = datum_to_hash128(v[0]); hit.name = datum_to_hash128(v[1]);
    hit.rating = DatumGetInt64(v[2]); hit.rd = DatumGetInt64(v[3]); hit.witnesses = DatumGetInt64(v[4]);
    hit.match_kind = BROWSE_MATCH_NAME;
    if (laplace_glicko2_refuted(hit.rating, hit.rd)) return;
    BrowseHit *old = hash_search(hits, &hit.id, HASH_ENTER, &found);
    int64 mu = laplace_effective_mu_fp(hit.rating, hit.rd);
    int64 old_mu = found ? laplace_effective_mu_fp(old->rating, old->rd) : 0;
    if (!found || mu > old_mu || (mu == old_mu && (hit.witnesses > old->witnesses ||
        (hit.witnesses == old->witnesses && id_compare(&hit.name, &old->name) < 0)))) *old = hit;
}
static void
browse_facet(TupleTableSlot *row, void *context)
{
    bool isnull;
    hash128_t id = datum_to_hash128(slot_getattr(row, 1, &isnull));
    BrowseHit *hit = hash_search((HTAB *)context, &id, HASH_FIND, NULL);
    if (hit) {
        hit->exists = true; hit->tier = DatumGetInt16(slot_getattr(row, 2, &isnull));
        Datum type = slot_getattr(row, 3, &isnull);
        hit->has_type = !isnull;
        if (hit->has_type) hit->type = datum_to_hash128(type);
    }
}
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
    laplace_spi_stream(p, 1, array_type, args, NULL, browse_name, hits);
    if (!PG_ARGISNULL(1)) {
        hash128_t exact = datum_to_hash128(PG_GETARG_DATUM(1));
        add_structural_hit(hits, &exact, BROWSE_MATCH_SURFACE);
    }
    /* Containers of all members are hits beside the named entities. An id
     * already present keeps its earlier match kind. */
    for (int i = 0; i < count; ++i)
        add_structural_hit(hits, &names[i], BROWSE_MATCH_CONTAINS_ALL);
    /* The members themselves are hits, so the query's constituents surface even
     * when no container holds them all. */
    add_member_hits(hits, members);
    /* One facet read hydrates tier and type for every hit; ids with no entity
     * row are dropped. */
    args[0] = PointerGetDatum(id_array(hits));
    p = plan(&facets_plan, laplace_sql_query_text("entity.facets"), 1, array_type);
    laplace_spi_stream(p, 1, array_type, args, NULL, browse_facet, hits);
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
        Datum v[] = {hash128_to_datum(&hit->id), Int16GetDatum(hit->tier), hit->has_type ? hash128_to_datum(&hit->type) : (Datum)0,
            hash128_to_datum(&hit->name), CStringGetTextDatum(match_kind_text(hit->match_kind)),
            fp_display_numeric(hit->rating), fp_display_numeric(hit->rd), eff_mu_display_numeric(hit->rating, hit->rd),
            Int64GetDatum(hit->witnesses), Int64GetDatum(count), BoolGetDatum(truncated), Int64GetDatum(n)};
        bool nulls[12] = {false};
        nulls[2] = !hit->has_type;
        nulls[5] = nulls[6] = nulls[7] = structural;
        tuplestore_putvalues(r->setResult, r->setDesc, v, nulls);
    }
    hash_destroy(hits);
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
