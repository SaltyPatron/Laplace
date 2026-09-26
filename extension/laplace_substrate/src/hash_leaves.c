#include "hash_leaves.h"

#include "access/table.h"
#include "catalog/namespace.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/lsyscache.h"
#include "utils/inval.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/rel.h"

#define LAPLACE_HASH_LEAF_TABLES 4

static LaplaceHashLeaves resolved[LAPLACE_HASH_LEAF_TABLES];
static int resolved_count = 0;
static bool invalidation_registered = false;

static void
hash_leaves_invalidate(Datum arg, Oid relid)
{
    (void) arg;
    for (int i = 0; i < resolved_count; ++i)
        if (relid == InvalidOid || relid == resolved[i].parent_oid)
            resolved[i].stale = true;
}

static void
hash_leaves_resolve(LaplaceHashLeaves *entry, const char *relname, const char *label)
{
    Relation parent = table_open(entry->parent_oid, AccessShareLock);
    PartitionKey key = RelationGetPartitionKey(parent);
    PartitionDesc desc = RelationGetPartitionDesc(parent, false);
    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
        key->partnatts != 1 || desc == NULL || desc->nparts <= 0 ||
        desc->boundinfo->nindexes != desc->nparts)
        ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                        errmsg("%s: laplace.%s must be HASH partitioned on one key with one leaf per remainder",
                               label, relname)));

    if (entry->leaf_oids == NULL || entry->count != desc->nparts)
    {
        if (entry->leaf_oids != NULL)
            pfree(entry->leaf_oids);
        entry->leaf_oids = MemoryContextAlloc(TopMemoryContext, sizeof(Oid) * desc->nparts);
    }
    entry->count = desc->nparts;
    for (int remainder = 0; remainder < desc->nparts; ++remainder)
    {
        int part = desc->boundinfo->indexes[remainder];
        if (part < 0 || part >= desc->nparts)
            ereport(ERROR, (errcode(ERRCODE_CHECK_VIOLATION),
                            errmsg("%s: laplace.%s is missing HASH remainder %d",
                                   label, relname, remainder)));
        entry->leaf_oids[remainder] = desc->oids[part];
    }
    table_close(parent, AccessShareLock);
    entry->stale = false;
}

const LaplaceHashLeaves *
laplace_hash_leaves(const char *relname, const char *label)
{
    Oid parent_oid = get_relname_relid(relname, get_namespace_oid("laplace", false));

    if (!OidIsValid(parent_oid))
        ereport(ERROR, (errcode(ERRCODE_UNDEFINED_TABLE),
                        errmsg("%s: laplace.%s does not exist", label, relname)));
    if (!invalidation_registered)
    {
        CacheRegisterRelcacheCallback(hash_leaves_invalidate, (Datum) 0);
        invalidation_registered = true;
    }
    for (int i = 0; i < resolved_count; ++i)
        if (resolved[i].parent_oid == parent_oid)
        {
            if (resolved[i].stale)
                hash_leaves_resolve(&resolved[i], relname, label);
            return &resolved[i];
        }
    if (resolved_count == LAPLACE_HASH_LEAF_TABLES)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                        errmsg("%s: too many routed tables", label)));

    LaplaceHashLeaves *entry = &resolved[resolved_count];
    entry->parent_oid = parent_oid;
    entry->count = 0;
    entry->leaf_oids = NULL;
    hash_leaves_resolve(entry, relname, label);
    resolved_count++;
    return entry;
}

void
laplace_hash_leaf_route(const LaplaceHashLeaves *leaves, const Datum *keys,
                        int n, int *remainders, const char *label)
{
    Relation parent = table_open(leaves->parent_oid, AccessShareLock);
    PartitionKey key = RelationGetPartitionKey(parent);
    bool nulls[1] = {false};

    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH || key->partnatts != 1)
        ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                        errmsg("%s: routed table is no longer HASH partitioned", label)));
    for (int i = 0; i < n; ++i)
    {
        Datum value[1] = {keys[i]};
        uint64 hash = compute_partition_hash_value(1, key->partsupfunc,
                                                   key->partcollation, value, nulls);
        remainders[i] = (int) (hash % (uint64) leaves->count);
    }
    table_close(parent, AccessShareLock);
}
