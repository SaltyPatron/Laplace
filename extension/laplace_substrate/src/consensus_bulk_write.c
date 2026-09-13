#include "postgres.h"
#include "access/table.h"
#include "access/xact.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "commands/copy.h"
#include "executor/executor.h"
#include "libpq/pqformat.h"
#include "miscadmin.h"
#include "nodes/makefuncs.h"
#include "parser/parse_relation.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "tcop/utility.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"

#include "consensus_bulk_write.h"

#define CONSENSUS_HASH_LEAVES 8

typedef struct ConsensusCopyInput
{
    Datum *columns[9];
    bool *nulls[9];
    Datum type;
    int count;
    int row;
    bool trailer;
    StringInfoData buffer;
} ConsensusCopyInput;

typedef struct ConsensusMatchedRoute
{
    char type_id[16];
    Oid hash_parent_oid;
    Oid leaf_oids[CONSENSUS_HASH_LEAVES];
} ConsensusMatchedRoute;

typedef struct ConsensusMatchedPlan
{
    Oid leaf_oid;
    SPIPlanPtr plan;
} ConsensusMatchedPlan;

static ConsensusCopyInput *active_input;
static HTAB *matched_routes;
static HTAB *matched_plans;

static const uint8_t *
consensus_id16(Datum value)
{
    bytea *id = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(id) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("consensus bulk write: expected 16-byte identity")));
    return (const uint8_t *) VARDATA_ANY(id);
}

static HTAB *
matched_route_cache(void)
{
    if (matched_routes == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = sizeof(ConsensusMatchedRoute);
        ctl.hcxt = TopMemoryContext;
        matched_routes = hash_create("consensus matched routes", 256,
                                     &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    return matched_routes;
}

static HTAB *
matched_plan_cache(void)
{
    if (matched_plans == NULL)
    {
        HASHCTL ctl;
        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(Oid);
        ctl.entrysize = sizeof(ConsensusMatchedPlan);
        ctl.hcxt = TopMemoryContext;
        matched_plans = hash_create("consensus matched leaf plans", 1024,
                                    &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }
    return matched_plans;
}

static ConsensusMatchedRoute *
matched_route(Datum type, const char *label)
{
    const uint8_t *type16 = consensus_id16(type);
    ConsensusMatchedRoute *route;
    bool found;

    route = (ConsensusMatchedRoute *) hash_search(
        matched_route_cache(), type16, HASH_ENTER, &found);
    if (!found)
    {
        Oid namespace_oid = get_namespace_oid("laplace", false);
        Oid root_oid = get_relname_relid("consensus", namespace_oid);
        Relation root;
        Relation hash_parent;
        PartitionKey key;
        PartitionDesc desc;
        PartitionBoundInfo bounds;
        bool equal;
        int datum_index;
        int part_index;

        memset(((char *) route) + sizeof(route->type_id), 0,
               sizeof(*route) - sizeof(route->type_id));
        if (!OidIsValid(root_oid))
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: laplace.consensus does not exist", label)));

        root = table_open(root_oid, AccessShareLock);
        key = RelationGetPartitionKey(root);
        desc = RelationGetPartitionDesc(root, false);
        if (key == NULL || key->strategy != PARTITION_STRATEGY_LIST ||
            key->partnatts != 1 || desc == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                     errmsg("%s: laplace.consensus must be LIST(type_id) partitioned",
                            label)));
        bounds = desc->boundinfo;
        datum_index = partition_list_bsearch(key->partsupfunc,
                                             key->partcollation,
                                             bounds, type, &equal);
        part_index = equal ? bounds->indexes[datum_index] : bounds->default_index;
        if (part_index < 0 || part_index >= desc->nparts)
            ereport(ERROR,
                    (errcode(ERRCODE_CHECK_VIOLATION),
                     errmsg("%s: relation type has no consensus partition", label)));
        route->hash_parent_oid = desc->oids[part_index];
        table_close(root, AccessShareLock);

        hash_parent = table_open(route->hash_parent_oid, AccessShareLock);
        key = RelationGetPartitionKey(hash_parent);
        desc = RelationGetPartitionDesc(hash_parent, false);
        if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
            key->partnatts != 1 || desc == NULL ||
            desc->nparts != CONSENSUS_HASH_LEAVES ||
            desc->boundinfo->nindexes != CONSENSUS_HASH_LEAVES)
            ereport(ERROR,
                    (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                     errmsg("%s: consensus relation partition must be HASH(subject_id, %d)",
                            label, CONSENSUS_HASH_LEAVES)));
        for (int remainder = 0; remainder < CONSENSUS_HASH_LEAVES; remainder++)
        {
            part_index = desc->boundinfo->indexes[remainder];
            if (part_index < 0 || part_index >= desc->nparts)
                ereport(ERROR,
                        (errcode(ERRCODE_CHECK_VIOLATION),
                         errmsg("%s: consensus HASH partition is missing remainder %d",
                                label, remainder)));
            route->leaf_oids[remainder] = desc->oids[part_index];
        }
        table_close(hash_parent, AccessShareLock);
    }
    return route;
}

static SPIPlanPtr
matched_leaf_plan(Oid leaf_oid, const char *label)
{
    ConsensusMatchedPlan *entry;
    bool found;

    entry = (ConsensusMatchedPlan *) hash_search(
        matched_plan_cache(), &leaf_oid, HASH_ENTER, &found);
    if (!found)
    {
        static const Oid argtypes[7] = {
            BYTEAARRAYOID, BYTEAARRAYOID, INT8ARRAYOID, 1185,
            INT8ARRAYOID, INT8ARRAYOID, INT8ARRAYOID};
        char *namespace_name = get_namespace_name(get_rel_namespace(leaf_oid));
        char *relation_name = get_rel_name(leaf_oid);
        char *qualified_name;
        StringInfoData sql;
        SPIPlanPtr plan;

        if (namespace_name == NULL || relation_name == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_UNDEFINED_TABLE),
                     errmsg("%s: consensus HASH leaf disappeared", label)));
        qualified_name = quote_qualified_identifier(namespace_name, relation_name);
        initStringInfo(&sql);
        appendStringInfo(&sql,
            "UPDATE ONLY %s AS c SET "
            "rating = b.rating, rd = b.rd, volatility = b.volatility, "
            "witness_count = c.witness_count + b.games, "
            "last_observed_at = GREATEST(c.last_observed_at, b.ts) "
            "FROM unnest($1::bytea[], $2::bytea[], $3::int8[], "
            "$4::timestamptz[], $5::int8[], $6::int8[], $7::int8[]) "
            "AS b(id, subject_id, games, ts, rating, rd, volatility) "
            "WHERE c.id = b.id AND c.subject_id = b.subject_id",
            qualified_name);
        plan = SPI_prepare(sql.data, 7, (Oid *) argtypes);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: matched leaf SPI_prepare failed: %s",
                            label, SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: matched leaf SPI_keepplan failed", label)));
        entry->plan = plan;
        pfree(sql.data);
        pfree(qualified_name);
    }
    return entry->plan;
}

static bool
relation_allows_direct_update(Oid oid, bool require_insert)
{
    Relation relation = table_open(oid, AccessShareLock);
    bool allowed = relation->rd_rules == NULL
        && relation->trigdesc == NULL
        && check_enable_rls(oid, InvalidOid, true) != RLS_ENABLED;
    table_close(relation, AccessShareLock);
    if (!allowed) return false;

    AclMode mode = ACL_SELECT | ACL_UPDATE | (require_insert ? ACL_INSERT : 0);
    return pg_class_aclcheck(oid, GetUserId(), mode) == ACLCHECK_OK;
}

static void
deconstruct_fold_values(Datum *values, int count,
                        Datum *columns[9], bool *nulls[9], const char *label)
{
    for (int column = 0; column < 9; ++column)
    {
        ArrayType *array = DatumGetArrayTypeP(values[column]);
        Oid element = ARR_ELEMTYPE(array);
        int16 length;
        bool byval;
        char align;
        int n;

        get_typlenbyvalalign(element, &length, &byval, &align);
        deconstruct_array(array, element, length, byval, align,
                          &columns[column], &nulls[column], &n);
        if (n != count)
            ereport(ERROR,
                    (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                     errmsg("%s: folded column length mismatch", label)));
    }
}

static void
free_fold_values(Datum *columns[9], bool *nulls[9])
{
    for (int column = 0; column < 9; ++column)
    {
        pfree(columns[column]);
        pfree(nulls[column]);
    }
}

bool
laplace_consensus_update_matched(Datum type, Datum *values, int count,
                                 uint64 expected, uint64 *processed)
{
    const char *label = "consensus matched leaf update";
    Oid root_oid = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    ConsensusMatchedRoute *route;
    Datum *columns[9];
    bool *nulls[9];
    int counts[CONSENSUS_HASH_LEAVES] = {0};
    int fills[CONSENSUS_HASH_LEAVES] = {0};
    int *positions[CONSENSUS_HASH_LEAVES] = {0};
    Relation hash_parent;
    PartitionKey key;
    bool partition_nulls[1] = {false};
    uint64 total = 0;

    *processed = 0;
    if (expected == 0) return true;
    if (!OidIsValid(root_oid) ||
        !relation_allows_direct_update(root_oid, true))
        return false;

    deconstruct_fold_values(values, count, columns, nulls, label);
    route = matched_route(type, label);
    hash_parent = table_open(route->hash_parent_oid, AccessShareLock);
    key = RelationGetPartitionKey(hash_parent);
    if (key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
        key->partnatts != 1)
        ereport(ERROR,
                (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                 errmsg("%s: cached consensus route is no longer HASH partitioned",
                        label)));

    for (int row = 0; row < count; ++row)
    {
        if (nulls[5][row] || !DatumGetBool(columns[5][row]))
            continue;
        if (nulls[0][row] || nulls[1][row] || nulls[3][row] ||
            nulls[4][row] || nulls[6][row] || nulls[7][row] || nulls[8][row])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("%s: matched folded row contains NULL", label)));
        Datum partition_values[1] = {columns[1][row]};
        uint64 hash = compute_partition_hash_value(
            1, key->partsupfunc, key->partcollation,
            partition_values, partition_nulls);
        int remainder = (int) (hash % CONSENSUS_HASH_LEAVES);
        counts[remainder]++;
        total++;
    }
    table_close(hash_parent, AccessShareLock);

    if (total != expected)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("%s: matched bitmap contained %lu of %lu expected rows",
                        label, (unsigned long) total, (unsigned long) expected)));

    /* Decide the policy boundary before the first direct write. If one leaf
     * would change parent-table trigger/RLS/ACL behavior, use the existing SQL
     * parent path for the entire matched set. */
    for (int remainder = 0; remainder < CONSENSUS_HASH_LEAVES; ++remainder)
    {
        if (counts[remainder] == 0) continue;
        if (!relation_allows_direct_update(route->leaf_oids[remainder], false))
        {
            free_fold_values(columns, nulls);
            return false;
        }
        positions[remainder] = (int *) palloc(sizeof(int) * counts[remainder]);
    }

    hash_parent = table_open(route->hash_parent_oid, AccessShareLock);
    key = RelationGetPartitionKey(hash_parent);
    for (int row = 0; row < count; ++row)
    {
        if (nulls[5][row] || !DatumGetBool(columns[5][row])) continue;
        Datum partition_values[1] = {columns[1][row]};
        uint64 hash = compute_partition_hash_value(
            1, key->partsupfunc, key->partcollation,
            partition_values, partition_nulls);
        int remainder = (int) (hash % CONSENSUS_HASH_LEAVES);
        positions[remainder][fills[remainder]++] = row;
    }
    table_close(hash_parent, AccessShareLock);

    for (int remainder = 0; remainder < CONSENSUS_HASH_LEAVES; ++remainder)
    {
        int n = counts[remainder];
        Datum args[7];
        Datum *ids;
        Datum *subjects;
        Datum *games;
        Datum *timestamps;
        Datum *ratings;
        Datum *rds;
        Datum *volatilities;
        int rc;

        if (n == 0) continue;
        ids = (Datum *) palloc(sizeof(Datum) * n);
        subjects = (Datum *) palloc(sizeof(Datum) * n);
        games = (Datum *) palloc(sizeof(Datum) * n);
        timestamps = (Datum *) palloc(sizeof(Datum) * n);
        ratings = (Datum *) palloc(sizeof(Datum) * n);
        rds = (Datum *) palloc(sizeof(Datum) * n);
        volatilities = (Datum *) palloc(sizeof(Datum) * n);
        for (int i = 0; i < n; ++i)
        {
            int row = positions[remainder][i];
            ids[i] = columns[0][row];
            subjects[i] = columns[1][row];
            games[i] = columns[3][row];
            timestamps[i] = columns[4][row];
            ratings[i] = columns[6][row];
            rds[i] = columns[7][row];
            volatilities[i] = columns[8][row];
        }
        args[0] = PointerGetDatum(construct_array(ids, n, BYTEAOID, -1, false, 'i'));
        args[1] = PointerGetDatum(construct_array(subjects, n, BYTEAOID, -1, false, 'i'));
        args[2] = PointerGetDatum(construct_array(games, n, INT8OID, 8, true, 'd'));
        args[3] = PointerGetDatum(construct_array(timestamps, n, TIMESTAMPTZOID, 8, true, 'd'));
        args[4] = PointerGetDatum(construct_array(ratings, n, INT8OID, 8, true, 'd'));
        args[5] = PointerGetDatum(construct_array(rds, n, INT8OID, 8, true, 'd'));
        args[6] = PointerGetDatum(construct_array(volatilities, n, INT8OID, 8, true, 'd'));

        rc = SPI_execute_plan(
            matched_leaf_plan(route->leaf_oids[remainder], label),
            args, NULL, false, 0);
        if (rc != SPI_OK_UPDATE || SPI_processed != (uint64) n)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: leaf update affected %lu of %d rows (%s)",
                            label, (unsigned long) SPI_processed, n,
                            SPI_result_code_string(rc))));
        *processed += SPI_processed;
        pfree(ids);
        pfree(subjects);
        pfree(games);
        pfree(timestamps);
        pfree(ratings);
        pfree(rds);
        pfree(volatilities);
        pfree(positions[remainder]);
    }

    free_fold_values(columns, nulls);
    return true;
}

/* COPY's callback has no context argument. Save/restore this pointer around
 * each invocation, including errors and nested inserts from triggers. */
static void
send_identity(StringInfo buffer, Datum value, bool isnull)
{
    if (isnull)
        pq_sendint32(buffer, -1);
    else
    {
        bytea *id = DatumGetByteaPP(value);
        int length = VARSIZE_ANY_EXHDR(id);
        pq_sendint32(buffer, length);
        pq_sendbytes(buffer, VARDATA_ANY(id), length);
    }
}

static bool
next_copy_row(ConsensusCopyInput *input)
{
    StringInfo buffer = &input->buffer;
    resetStringInfo(buffer);
    while (input->row < input->count)
    {
        int row = input->row++;
        if (DatumGetBool(input->columns[5][row])) continue;
        pq_sendint16(buffer, 9);
        send_identity(buffer, input->columns[0][row], false);
        send_identity(buffer, input->columns[1][row], false);
        send_identity(buffer, input->type, false);
        send_identity(buffer, input->columns[2][row], input->nulls[2][row]);
        /* rating, deviation, volatility, witness_count, PostgreSQL timestamp */
        static const int integer_columns[] = {6, 7, 8, 3, 4};
        for (int column = 0; column < lengthof(integer_columns); ++column)
        {
            pq_sendint32(buffer, sizeof(int64));
            pq_sendint64(buffer, DatumGetInt64(input->columns[integer_columns[column]][row]));
        }
        return true;
    }
    if (!input->trailer)
    {
        pq_sendint16(buffer, -1);
        input->trailer = true;
        return true;
    }
    return false;
}

static int
read_copy_input(void *destination, int minread, int maxread)
{
    ConsensusCopyInput *input = active_input;
    int copied = 0;
    (void) minread;
    CHECK_FOR_INTERRUPTS();
    while (copied < maxread)
    {
        StringInfo buffer = &input->buffer;
        if (buffer->cursor == buffer->len && !next_copy_row(input)) break;
        int bytes = Min(maxread - copied, buffer->len - buffer->cursor);
        memcpy((char *) destination + copied, buffer->data + buffer->cursor, bytes);
        buffer->cursor += bytes;
        copied += bytes;
    }
    return copied;
}

bool
laplace_consensus_copy_novel(Datum type, Datum *values, int count,
                            uint64 *processed)
{
    Oid oid = get_relname_relid("consensus", get_namespace_oid("laplace", false));
    Relation relation = table_open(oid, RowExclusiveLock);
    if (relation->rd_rules || check_enable_rls(oid, InvalidOid, true) == RLS_ENABLED)
    {
        table_close(relation, NoLock);
        return false;
    }
    if (XactReadOnly && !relation->rd_islocaltemp)
        PreventCommandIfReadOnly("consensus bulk insert");
    PreventCommandIfParallelMode("consensus bulk insert");

    static const char *names[] = {"id", "subject_id", "type_id", "object_id",
        "rating", "rd", "volatility", "witness_count", "last_observed_at"};
    List *columns = NIL;
    ParseState *parse = make_parsestate(NULL);
    ParseNamespaceItem *ns = addRangeTableEntryForRelation(
        parse, relation, RowExclusiveLock, NULL, false, false);
    ns->p_perminfo->requiredPerms = ACL_INSERT;
    for (int column = 0; column < lengthof(names); ++column)
        columns = lappend(columns, makeString(pstrdup(names[column])));
    List *attnums = CopyGetAttnums(RelationGetDescr(relation), relation, columns);
    ListCell *cell;
    foreach(cell, attnums)
        ns->p_perminfo->insertedCols = bms_add_member(
            ns->p_perminfo->insertedCols, lfirst_int(cell) - FirstLowInvalidHeapAttributeNumber);
    ExecCheckPermissions(parse->p_rtable, parse->p_rteperminfos, true);

    ConsensusCopyInput input = {0};
    input.type = type;
    input.count = count;
    for (int column = 0; column < 9; ++column)
    {
        ArrayType *array = DatumGetArrayTypeP(values[column]);
        Oid element = ARR_ELEMTYPE(array);
        int16 length;
        bool byval;
        char align;
        int n;
        get_typlenbyvalalign(element, &length, &byval, &align);
        deconstruct_array(array, element, length, byval, align,
            &input.columns[column], &input.nulls[column], &n);
        if (n != count)
            elog(ERROR, "consensus bulk insert: folded column length mismatch");
    }
    initStringInfo(&input.buffer);
    pq_sendbytes(&input.buffer, "PGCOPY\n\377\r\n\0", 11);
    pq_sendint32(&input.buffer, 0);
    pq_sendint32(&input.buffer, 0);
    ConsensusCopyInput *previous = active_input;
    List *options = list_make1(makeDefElem("format", (Node *) makeString("binary"), -1));
    CommandCounterIncrement();
    PushActiveSnapshot(GetTransactionSnapshot());
    active_input = &input;
    PG_TRY();
    {
        CopyFromState copy = BeginCopyFrom(parse, relation, NULL, NULL, false,
            read_copy_input, columns, options);
        *processed = CopyFrom(copy);
        EndCopyFrom(copy);
    }
    PG_FINALLY();
    {
        active_input = previous;
        PopActiveSnapshot();
    }
    PG_END_TRY();
    CommandCounterIncrement();
    pfree(input.buffer.data);
    for (int column = 0; column < 9; ++column)
    {
        pfree(input.columns[column]);
        pfree(input.nulls[column]);
    }
    free_parsestate(parse);
    table_close(relation, NoLock);
    return true;
}
