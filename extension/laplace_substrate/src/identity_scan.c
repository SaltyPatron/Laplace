#include "identity_scan.h"
#include "access/genam.h"
#include "access/nbtree.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/pg_am_d.h"
#include "catalog/pg_opfamily_d.h"
#include "catalog/pg_type_d.h"
#include "miscadmin.h"
#include "utils/fmgroids.h"
#include "utils/lsyscache.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"

bool
laplace_identity_scan(Oid leaf, ArrayType *ids,
    LaplaceIdentityConsumer consume, void *context)
{
    Relation relation = table_open(leaf, AccessShareLock);
    Oid index_oid = RelationGetPrimaryKeyIndex(relation, false);
    AttrNumber id = get_attnum(leaf, "id");
    if (!OidIsValid(index_oid) || id <= 0 || get_atttype(leaf, id) != BYTEAOID)
    {
        table_close(relation, NoLock);
        return false;
    }
    Relation index = index_open(index_oid, AccessShareLock);
    if (index->rd_rel->relam != BTREE_AM_OID || !index->rd_index->indisvalid ||
        !index->rd_index->indisready || index->rd_index->indnkeyatts != 1 ||
        index->rd_index->indkey.values[0] != id ||
        index->rd_opfamily[0] != BYTEA_BTREE_FAM_OID || RelationGetIndexPredicate(index) != NIL)
    {
        index_close(index, AccessShareLock);
        table_close(relation, NoLock);
        return false;
    }
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    ScanKeyData key;
    ScanKeyEntryInitialize(&key, SK_SEARCHARRAY, 1, BTEqualStrategyNumber,
        BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(ids));
    IndexScanDesc scan = index_beginscan(relation, index, GetActiveSnapshot(), NULL, 1, 0);
    index_rescan(scan, &key, 1, NULL, 0);
    while (index_getnext_slot(scan, ForwardScanDirection, slot))
    {
        consume(slot, id, context);
        ExecClearTuple(slot);
        CHECK_FOR_INTERRUPTS();
    }
    index_endscan(scan);
    ExecDropSingleTupleTableSlot(slot);
    index_close(index, AccessShareLock);
    table_close(relation, NoLock);
    return true;
}
