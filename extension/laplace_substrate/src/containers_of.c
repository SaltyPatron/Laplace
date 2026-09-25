#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "content_membership_read.h"
#include "consensus_scan.h"
#include "laplace/core/relation_law.h"

/* Native breadth-first containment. One indexed set probe per frontier, then
 * one hydration read for the selected ids. Labels are a subsequent operation. */
typedef struct {
    hash128_t id;
    int ordinal;
} ContainerSeen;
typedef struct {
    hash128_t id, type;
    int hop;
    int16 tier;
    bool exists;
} ContainerHit;
static SPIPlanPtr facets_plan;

/* An identifier (an ILI key) is contained by nothing: text contains the words
 * bound to it. Its containers are therefore read through its bindings, the same
 * way display realizes an identifier through them. */
typedef struct {
    HTAB *seen;
    Datum *ids;
    int count, capacity;
} BoundSurfaces;

static void
receive_bound_surface(const LaplaceConsensusRow *row, void *context)
{
    BoundSurfaces *bound = context;
    bool found;
    ContainerSeen *entry = hash_search(bound->seen, &row->subject, HASH_ENTER, &found);
    if (found) return;
    entry->ordinal = -1;
    if (bound->count == bound->capacity) {
        bound->capacity = bound->capacity ? bound->capacity * 2 : 16;
        bound->ids = bound->ids ? repalloc(bound->ids, bound->capacity * sizeof(Datum))
                                : palloc(bound->capacity * sizeof(Datum));
    }
    bound->ids[bound->count++] = hash128_to_datum(&row->subject);
}

static SPIPlanPtr
container_plan(SPIPlanPtr *slot, const char *key)
{
    if (*slot == NULL) {
        Oid types[] = {BYTEAARRAYOID};
        *slot = SPI_prepare_cursor(laplace_sql_query_text(key), 1, types, CURSOR_OPT_GENERIC_PLAN);
        if (*slot == NULL || SPI_keepplan(*slot) != 0)
            elog(ERROR, "containers_of: cannot prepare %s", key);
    }
    return *slot;
}

PG_FUNCTION_INFO_V1(pg_laplace_containers_of);
Datum
pg_laplace_containers_of(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0)) ereport(ERROR, (errmsg("containers_of: entity must not be NULL")));
    int hops = PG_ARGISNULL(1) ? 1 : PG_GETARG_INT32(1);
    int limit = PG_ARGISNULL(2) ? INT_MAX : PG_GETARG_INT32(2);
    if (hops < 0 || limit < 0) ereport(ERROR, (errmsg("containers_of: negative work boundary")));
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *result = (ReturnSetInfo *)fcinfo->resultinfo;
    if (hops == 0 || limit == 0) return (Datum)0;
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "containers_of: SPI connect failed");
    HASHCTL ctl = {0}; ctl.keysize = sizeof(hash128_t); ctl.entrysize = sizeof(ContainerSeen);
    HTAB *seen = hash_create("containers selected", 1024, &ctl, HASH_ELEM | HASH_BLOBS);
    hash128_t root = datum_to_hash128(PG_GETARG_DATUM(0));
    ContainerSeen *entry = hash_search(seen, &root, HASH_ENTER, NULL); entry->ordinal = -1;
    BoundSurfaces bound = {seen, NULL, 0, 0};
    hash128_t has_sense;
    bound.capacity = 16;
    bound.ids = palloc(bound.capacity * sizeof(Datum));
    bound.ids[bound.count++] = hash128_to_datum(&root);
    if (laplace_relation_type_id("HAS_SENSE", &has_sense) == 0) {
        Datum key = hash128_to_datum(&root), type = hash128_to_datum(&has_sense);
        ArrayType *objects = construct_array(&key, 1, BYTEAOID, -1, false, TYPALIGN_INT);
        ArrayType *types = construct_array(&type, 1, BYTEAOID, -1, false, TYPALIGN_INT);
        laplace_consensus_scan(NULL, objects, types, receive_bound_surface, &bound, NULL);
    }
    ArrayType *frontier = construct_array(bound.ids, bound.count, BYTEAOID, -1, false, TYPALIGN_INT);
    ContainerHit *hits = NULL; Size count = 0, capacity = 0;
    for (int hop = 1; hop <= hops && count < (Size)limit; ++hop) {
        CHECK_FOR_INTERRUPTS();
        Size start = count;
        int parent_count;
        hash128_t *parents = laplace_content_membership_entities(frontier, false, &parent_count);
        for (int i = 0; i < parent_count && count < (Size)limit; ++i) {
            CHECK_FOR_INTERRUPTS();
                bool found;
                hash128_t id = parents[i];
                entry = hash_search(seen, &id, HASH_ENTER, &found);
                if (found) continue;
                if (count == capacity) {
                    Size max = MaxAllocSize / sizeof(ContainerHit);
                    if (count == max) elog(ERROR, "containers_of: result exceeds allocation capacity");
                    capacity = Min(capacity ? capacity * 2 : 64, max);
                    hits = hits ? repalloc(hits, capacity * sizeof(ContainerHit)) : palloc(capacity * sizeof(ContainerHit));
                }
                entry->ordinal = (int)count;
                hits[count] = (ContainerHit){.id = id, .hop = hop}; ++count;
        }
        pfree(parents);
        if (count == start) break;
        Datum *next = palloc((count - start) * sizeof(Datum));
        for (Size i = start; i < count; ++i) next[i-start] = hash128_to_datum(&hits[i].id);
        frontier = construct_array(next, (int)(count - start), BYTEAOID, -1, false, TYPALIGN_INT);
    }
    if (count > 0) {
        Datum *ids = palloc(count * sizeof(Datum));
        for (Size i = 0; i < count; ++i) ids[i] = hash128_to_datum(&hits[i].id);
        Datum args[] = {PointerGetDatum(construct_array(ids, (int)count, BYTEAOID, -1, false, TYPALIGN_INT))};
        if (SPI_execute_plan(container_plan(&facets_plan,"entity.facets"),args,NULL,true,0) != SPI_OK_SELECT)
            elog(ERROR, "containers_of: selected entity read failed");
        for (uint64 i = 0; i < SPI_processed; ++i) {
            HeapTuple t = SPI_tuptable->vals[i]; TupleDesc d = SPI_tuptable->tupdesc; bool isnull;
            hash128_t id = datum_to_hash128(SPI_getbinval(t,d,1,&isnull));
            entry = hash_search(seen,&id,HASH_FIND,NULL);
            if (entry && entry->ordinal >= 0) {
                ContainerHit *hit = &hits[entry->ordinal];
                hit->tier = DatumGetInt16(SPI_getbinval(t,d,2,&isnull));
                hit->type = datum_to_hash128(SPI_getbinval(t,d,3,&isnull)); hit->exists = true;
            }
        }
        SPI_freetuptable(SPI_tuptable);
        for (Size i = 0; i < count; ++i) if (hits[i].exists) {
            Datum values[] = {hash128_to_datum(&hits[i].id),Int16GetDatum(hits[i].tier),
                hash128_to_datum(&hits[i].type),Int32GetDatum(hits[i].hop)};
            bool nulls[] = {false,false,false,false};
            tuplestore_putvalues(result->setResult,result->setDesc,values,nulls);
        }
    }
    hash_destroy(seen);laplace_spi_finish(spi_top);return (Datum)0;
}
