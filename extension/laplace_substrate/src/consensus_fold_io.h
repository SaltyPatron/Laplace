

















#ifndef LAPLACE_CONSENSUS_FOLD_IO_H
#define LAPLACE_CONSENSUS_FOLD_IO_H

#define FOLD_IDENT_LEN     48      
#define FOLD_GAMES_BOUND   (INT64CONST(1) << 27)
#define FOLD_OUT_SLOTS     1024    



static inline void
fold_read_bytea16(Datum d, uint8 *out)
{
    bytea *v = DatumGetByteaPP(d);

    if (VARSIZE_ANY_EXHDR(v) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("consensus fold: expected 16-byte id, got %d bytes",
                        (int) VARSIZE_ANY_EXHDR(v))));
    memcpy(out, VARDATA_ANY(v), 16);
}

static inline uint64
fold_hash_lo(const uint8 *id16)
{
    uint64 lo;

    memcpy(&lo, id16 + 8, 8);      
    return lo;
}

static inline int
fold_attno(Relation rel, const char *name)
{
    int attno = (int) get_attnum(RelationGetRelid(rel), name);

    if (attno <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_UNDEFINED_COLUMN),
                 errmsg("consensus fold: relation \"%s\" has no column \"%s\"",
                        RelationGetRelationName(rel), name)));
    return attno;
}









static inline int32
fold_route_identity(const uint8 *ident48, int32 nparts)
{
    return (int32) ((fold_hash_lo(ident48)
                     ^ fold_hash_lo(ident48 + 16)
                     ^ fold_hash_lo(ident48 + 32)) % (uint64) nparts);
}






static inline int32
fold_route_subject(const uint8 *subject16, int32 nparts)
{
    return (int32) (fold_hash_lo(subject16) % (uint64) nparts);
}



typedef struct FoldScratch
{
    MemoryContext          cxt;
    glicko2_observation_t *obs;
    int64                  cap;
} FoldScratch;


static inline void
fold_scratch_reserve(FoldScratch *s, int64 games)
{
    if (games > s->cap)
    {
        s->cap = games * 2;
        s->obs = s->obs
            ? (glicko2_observation_t *)
              repalloc(s->obs, sizeof(*s->obs) * s->cap)
            : (glicko2_observation_t *)
              MemoryContextAlloc(s->cxt, sizeof(*s->obs) * s->cap);
    }
}



typedef struct FoldOut
{
    Relation         rel;
    TupleTableSlot **slots;
    int              n_filled;
    CommandId        cid;
    BulkInsertState  bistate;
    MemoryContext    batch_cxt;
} FoldOut;

static inline void
fold_out_flush(FoldOut *o)
{
    if (o->n_filled == 0)
        return;
    table_multi_insert(o->rel, o->slots, o->n_filled, o->cid,
                       TABLE_INSERT_SKIP_FSM, o->bistate);
    o->n_filled = 0;
    MemoryContextReset(o->batch_cxt);
}

static inline void
fold_out_emit(FoldOut *o,
              const uint8 *ident, bool has_object,
              const glicko2_state_t *st, int64 witness_count, int64 last_ts)
{
    TupleTableSlot *slot;
    MemoryContext   old;
    hash128_t       cid;

    if (o->n_filled == FOLD_OUT_SLOTS)
        fold_out_flush(o);

    slot = o->slots[o->n_filled];
    ExecClearTuple(slot);

    old = MemoryContextSwitchTo(o->batch_cxt);
    hash128_blake3(ident, FOLD_IDENT_LEN, &cid);

    {
        bytea *idv = (bytea *) palloc(VARHDRSZ + 16);
        bytea *sv  = (bytea *) palloc(VARHDRSZ + 16);
        bytea *tv  = (bytea *) palloc(VARHDRSZ + 16);

        SET_VARSIZE(idv, VARHDRSZ + 16);
        memcpy(VARDATA(idv), &cid, 16);
        SET_VARSIZE(sv, VARHDRSZ + 16);
        memcpy(VARDATA(sv), ident, 16);
        SET_VARSIZE(tv, VARHDRSZ + 16);
        memcpy(VARDATA(tv), ident + 16, 16);

        slot->tts_values[0] = PointerGetDatum(idv);
        slot->tts_values[1] = PointerGetDatum(sv);
        slot->tts_values[2] = PointerGetDatum(tv);
        slot->tts_isnull[0] = slot->tts_isnull[1] = slot->tts_isnull[2] = false;
        if (has_object)
        {
            bytea *ov = (bytea *) palloc(VARHDRSZ + 16);

            SET_VARSIZE(ov, VARHDRSZ + 16);
            memcpy(VARDATA(ov), ident + 32, 16);
            slot->tts_values[3] = PointerGetDatum(ov);
            slot->tts_isnull[3] = false;
        }
        else
        {
            slot->tts_values[3] = (Datum) 0;
            slot->tts_isnull[3] = true;
        }
    }
    MemoryContextSwitchTo(old);

    slot->tts_values[4] = Int64GetDatum(st->rating);
    slot->tts_values[5] = Int64GetDatum(st->rd);
    slot->tts_values[6] = Int64GetDatum(st->volatility);
    slot->tts_values[7] = Int64GetDatum(witness_count);
    slot->tts_values[8] = TimestampTzGetDatum((TimestampTz) last_ts);
    slot->tts_isnull[4] = slot->tts_isnull[5] = slot->tts_isnull[6] = false;
    slot->tts_isnull[7] = slot->tts_isnull[8] = false;

    ExecStoreVirtualTuple(slot);
    o->n_filled++;
}

#endif                          
