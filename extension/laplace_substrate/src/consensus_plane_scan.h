/* Included by consensus_scan.c after the shared index/identity helpers.
 * This is a physical access plan for typed response planes, not a new graph.
 * An endpoint's next relation is discovered by a B-tree seek. After one plane
 * reaches its cutoff, seek past that exact relation rather than skipping the
 * remainder of the endpoint, which may contain entirely different evidence.
 */
#ifndef LAPLACE_CONSENSUS_PLANE_SCAN_H
#define LAPLACE_CONSENSUS_PLANE_SCAN_H

static Relation
scan_plane_index(Relation relation, AttrNumber endpoint, AttrNumber type,
                 AttrNumber object, AttrNumber rating, AttrNumber rd)
{
    List *indexes = RelationGetIndexList(relation);
    ListCell *cell;
    Relation selected = NULL;

    foreach(cell, indexes)
    {
        Relation index = index_open(lfirst_oid(cell), AccessShareLock);
        if (index->rd_rel->relam == BTREE_AM_OID &&
            index->rd_index->indisvalid && index->rd_index->indisready &&
            index->rd_index->indnkeyatts >= 3 &&
            index->rd_index->indkey.values[0] == endpoint &&
            index->rd_index->indkey.values[1] == type &&
            index->rd_index->indkey.values[2] == 0 &&
            index->rd_opfamily[0] == BYTEA_BTREE_FAM_OID &&
            index->rd_opfamily[1] == BYTEA_BTREE_FAM_OID &&
            index->rd_opfamily[2] == INTEGER_BTREE_FAM_OID &&
            (index->rd_indoption[0] & INDOPTION_DESC) == 0 &&
            (index->rd_indoption[1] & INDOPTION_DESC) == 0 &&
            (index->rd_indoption[2] & INDOPTION_DESC) != 0 &&
            scan_index_predicate(index, object, true))
        {
            List *expressions = RelationGetIndexExpressions(index);
            if (expressions != NIL &&
                scan_eff_mu_expression(linitial(expressions), rating, rd))
            {
                selected = index;
                break;
            }
        }
        index_close(index, AccessShareLock);
    }
    list_free(indexes);
    return selected;
}

static void
scan_plane_ranges(Relation relation, Relation index,
                  AttrNumber subject, AttrNumber object, AttrNumber type,
                  AttrNumber rating, AttrNumber rd, AttrNumber witnesses,
                  const ScanSet *subjects, const ScanSet *objects,
                  const ScanSet *types, LaplaceConsensusConsumer consume,
                  LaplaceConsensusCutoff cutoff, void *context,
                  LaplaceConsensusScanStats *stats)
{
    const ScanSet *probe = subjects->array != NULL ? subjects : objects;
    TupleTableSlot *slot = table_slot_create(relation, NULL);
    IndexScanDesc discovery = index_beginscan(relation, index,
        GetActiveSnapshot(), NULL, 2, 0);
    IndexScanDesc ranked = index_beginscan(relation, index,
        GetActiveSnapshot(), NULL, 2, 0);
    ScanKeyData seek_keys[2], plane_keys[2];
    /* The empty byte string is a B-tree lower bound, not a minted identity.
     * Every canonical 16-byte relation sorts after it, including all-zero. */
    bytea *minimum = palloc(VARHDRSZ);
    SET_VARSIZE(minimum, VARHDRSZ);

    for (int p = 0; p < probe->count; ++p)
    {
        bytea *after = NULL;
        ScanKeyEntryInitialize(&seek_keys[0], 0, 1, BTEqualStrategyNumber,
            BYTEAOID, InvalidOid, F_BYTEAEQ, probe->values[p]);
        ScanKeyEntryInitialize(&plane_keys[0], 0, 1, BTEqualStrategyNumber,
            BYTEAOID, InvalidOid, F_BYTEAEQ, probe->values[p]);

        for (;;)
        {
            bool isnull;
            Datum value;
            bytea *next;
            CHECK_FOR_INTERRUPTS();
            ScanKeyEntryInitialize(&seek_keys[1], 0, 2,
                after ? BTGreaterStrategyNumber : BTGreaterEqualStrategyNumber,
                BYTEAOID, InvalidOid, after ? F_BYTEAGT : F_BYTEAGE,
                PointerGetDatum(after ? after : minimum));
            index_rescan(discovery, seek_keys, 2, NULL, 0);
            ++stats->index_scans;
            if (!index_getnext_slot(discovery, ForwardScanDirection, slot))
                break;
            ++stats->rows_read;
            value = slot_getattr(slot, type, &isnull);
            if (isnull || VARSIZE_ANY_EXHDR(DatumGetByteaPP(value)) != 16)
                ereport(ERROR, (errmsg("consensus plane scan encountered a malformed relation identity")));
            next = DatumGetByteaPCopy(value);
            ExecClearTuple(slot);
            if (after) pfree(after);
            after = next;

            /* The hard relation scope is applied before opening a ranked
             * range. A skipped type cannot consume another plane's fanout. */
            if (!scan_set_contains(types, PointerGetDatum(after), false))
                continue;
            ScanKeyEntryInitialize(&plane_keys[1], 0, 2, BTEqualStrategyNumber,
                BYTEAOID, InvalidOid, F_BYTEAEQ, PointerGetDatum(after));
            index_rescan(ranked, plane_keys, 2, NULL, 0);
            ++stats->index_scans;
            while (index_getnext_slot(ranked, ForwardScanDirection, slot))
            {
                bool snull, onull, tnull, rnull, dnull, wnull;
                Datum s = slot_getattr(slot, subject, &snull);
                Datum o = slot_getattr(slot, object, &onull);
                Datum t = slot_getattr(slot, type, &tnull);
                LaplaceConsensusRow row;
                ++stats->rows_read;
                if ((stats->rows_read & 4095) == 0) CHECK_FOR_INTERRUPTS();
                if (snull || tnull || onull ||
                    !scan_set_contains(subjects, s, snull) ||
                    !scan_set_contains(objects, o, onull) ||
                    !scan_set_contains(types, t, tnull))
                {
                    ExecClearTuple(slot);
                    continue;
                }
                if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(s)) != 16 ||
                    VARSIZE_ANY_EXHDR(DatumGetByteaPP(t)) != 16 ||
                    VARSIZE_ANY_EXHDR(DatumGetByteaPP(o)) != 16)
                    ereport(ERROR, (errmsg("consensus plane scan encountered a malformed stored identity")));
                memcpy(&row.subject, VARDATA_ANY(DatumGetByteaPP(s)), 16);
                memcpy(&row.type, VARDATA_ANY(DatumGetByteaPP(t)), 16);
                memcpy(&row.object, VARDATA_ANY(DatumGetByteaPP(o)), 16);
                row.object_is_null = false;
                row.rating = DatumGetInt64(slot_getattr(slot, rating, &rnull));
                row.rd = DatumGetInt64(slot_getattr(slot, rd, &dnull));
                row.witnesses = DatumGetInt64(slot_getattr(slot, witnesses, &wnull));
                if (rnull || dnull || wnull)
                    ereport(ERROR, (errmsg("consensus plane scan encountered incomplete standing")));
                /* The cutoff is valid only for this exact endpoint/type.
                 * Ties continue; the consumer owns deterministic tie selection. */
                if (cutoff != NULL && cutoff(&row, context))
                {
                    ExecClearTuple(slot);
                    break;
                }
                ++stats->rows_matched;
                consume(&row, context);
                ExecClearTuple(slot);
            }
        }
        if (after) pfree(after);
        ExecClearTuple(slot);
    }
    pfree(minimum);
    index_endscan(ranked);
    index_endscan(discovery);
    ExecDropSingleTupleTableSlot(slot);
}

#endif
