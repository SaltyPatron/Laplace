/*
 * consensus_fold_io.h — staging/seed reads, partition routing, the consensus_next
 * writer, and Glicko scratch, shared by BOTH terminal-fold lanes:
 * consensus_fold_engine.c (the sort/merge partition lane) and
 * consensus_fold_walks.c (the no-sort trajectory-journal lane).
 *
 * These primitives used to live once in the engine lane while the walk lane was
 * crammed into the same file; splitting the walk lane out promoted the shared
 * surface here so neither lane re-implements it. In particular the partition
 * ROUTING LAW now has ONE spelling per shape (fold_route_identity / _subject),
 * each documented against its SQL twin (consensus_partition_of, 14_period_fold.sql.in)
 * and its C# twin (ConsensusAccumulatingWriter.PartitionOf); and the Glicko
 * observation-scratch realloc (previously copied three times) is fold_scratch_reserve.
 *
 * Include after postgres.h, access/tableam.h, executor/tuptable.h, utils/rel.h,
 * utils/lsyscache.h, utils/memutils.h, utils/timestamp.h, laplace/core/hash128.h
 * and laplace/core/glicko2.h.
 */
#ifndef LAPLACE_CONSENSUS_FOLD_IO_H
#define LAPLACE_CONSENSUS_FOLD_IO_H

#define FOLD_IDENT_LEN     48      /* subject(16) ‖ type(16) ‖ object|zero(16) */
#define FOLD_GAMES_BOUND   (INT64CONST(1) << 27)
#define FOLD_OUT_SLOTS     1024    /* multi-insert batch                      */

/* ---- leaf datum/catalog readers ---------------------------------------- */

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

    memcpy(&lo, id16 + 8, 8);      /* bytea image: [0..7]=Hi, [8..15]=Lo, LE */
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

/* ---- partition routing -------------------------------------------------- */

/*
 * Full-identity routing (subject ‖ type ‖ object|zero16): the partition lane's
 * staging is pre-routed this way by the writer (CopyPartitionAsync), so seeds
 * must re-route by the SAME law to land in the matching partition. Twin of the
 * SQL consensus_partition_of and the C# ConsensusAccumulatingWriter.PartitionOf.
 */
static inline int32
fold_route_identity(const uint8 *ident48, int32 nparts)
{
    return (int32) ((fold_hash_lo(ident48)
                     ^ fold_hash_lo(ident48 + 16)
                     ^ fold_hash_lo(ident48 + 32)) % (uint64) nparts);
}

/*
 * Subject-only routing: the walk lane gathers per subject, so the writer routes
 * a subject's whole row-set into one partition by subject.lo, and the walk seed
 * scan must match that (a weaker spelling than fold_route_identity, on purpose).
 */
static inline int32
fold_route_subject(const uint8 *subject16, int32 nparts)
{
    return (int32) (fold_hash_lo(subject16) % (uint64) nparts);
}

/* ---- Glicko observation scratch ---------------------------------------- */

typedef struct FoldScratch
{
    MemoryContext          cxt;
    glicko2_observation_t *obs;
    int64                  cap;
} FoldScratch;

/* Ensure the scratch can hold `games` observations (grows ×2, allocs in cxt). */
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

/* ---- consensus_next multi-insert writer -------------------------------- */

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

#endif                          /* LAPLACE_CONSENSUS_FOLD_IO_H */
