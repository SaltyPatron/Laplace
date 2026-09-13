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
#include "tcop/utility.h"
#include "utils/array.h"
#include "utils/lsyscache.h"
#include "utils/rel.h"
#include "utils/rls.h"
#include "utils/snapmgr.h"

#include "consensus_bulk_write.h"

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

/* COPY's callback has no context argument. Save/restore this pointer around
 * each invocation, including errors and nested inserts from triggers. */
static ConsensusCopyInput *active_input;

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
