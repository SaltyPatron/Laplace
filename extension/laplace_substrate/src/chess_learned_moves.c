#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "spi_common.h"
#include "spi_nested.h"

/*
 * chess.learned_moves(p_games)
 *
 * The learned table this substrate can actually key on. A piece-square cell is a
 * DOT PRODUCT -- "a pawn on e4" must be projected over every way of arriving there
 * (e2e4, e3e4, d3xe4, ...) at query time. A move is a LOOKUP: pe2e4 is a stored
 * tier-2 object with an id, O(1) perfcache geometry, and a containment set. So the
 * statistic is keyed on the move, and the classical piece-square shape falls out as
 * a projection over arrival squares whenever a caller wants that shape.
 *
 * MEASURED 2026-08-21 on the live corpus: 1,622,897 games carry HAS_RESULT and the
 * whole move vocabulary is 7,797 distinct tier-2 move entities, so this folds an
 * unbounded testimony set into a small bounded one. No board is reconstructed and no
 * legal move is generated: the game already happened, the ordered move ids are the
 * record, and ply parity gives the mover. The previous managed implementation
 * replayed every line through MoveGen + a hash of all ~35 legal actions per ply to
 * map an opaque id back to a move -- 73 games/s -- which this does not need at all.
 *
 * Split of labour is the same one trajectory_continuations.c states: SQL owns
 * candidate reduction (one index scan on HAS_RESULT joined to the game's Content
 * trajectory, unnested by the LATERAL), C owns the ordinal work -- orient each ply
 * by parity, accumulate per move id, rank.
 */

/* MEASURED 2026-08-21, and the reason both CTEs are MATERIALIZED.
 *
 * The first cut wrote `WITH res AS (SELECT DISTINCT object_id, realize.realize(object_id) ...)`
 * and assumed the DISTINCT meant realize ran three times -- there are exactly three result
 * tokens in the corpus. It does not: without the barrier the planner inlines the CTE and
 * evaluates realize.realize across the HAS_RESULT scan, so the call lands in
 * realize.constituents_closure per row over 1.6M rows and the query does not return. That is
 * a function in a subquery doing RBAR work, and it was mine.
 *
 * With MATERIALIZED the token resolution is 309ms for 3 rows, and the whole fold over 20,000
 * games -- 2,412,665 plies, 7,723 distinct moves -- runs in 18s.
 */
static const char *FOLD_QUERY =
    "WITH ids AS MATERIALIZED ("
    "  SELECT DISTINCT c.object_id AS id"
    "  FROM laplace.consensus c"
    "  WHERE c.type_id = laplace.relation_type_id('HAS_RESULT')"
    "), scored AS MATERIALIZED ("
    "  SELECT id, CASE realize.realize(id) WHEN '1-0' THEN 1.0::float8"
    "                                      WHEN '0-1' THEN 0.0::float8"
    "                                      WHEN '1/2-1/2' THEN 0.5::float8 END AS white_score"
    "  FROM ids"
    "), g AS ("
    "  SELECT s.white_score, p.trajectory"
    "  FROM laplace.consensus c"
    "  JOIN scored s ON s.id = c.object_id AND s.white_score IS NOT NULL"
    "  JOIN laplace.physicalities p"
    "    ON p.entity_id = c.subject_id AND p.type = 1 AND p.trajectory IS NOT NULL"
    "  WHERE c.type_id = laplace.relation_type_id('HAS_RESULT')"
    "  LIMIT CASE WHEN $1 <= 0 THEN NULL ELSE $1 END"
    ") "
    "SELECT t.entity_id, t.ordinal, g.white_score "
    "FROM g CROSS JOIN LATERAL public.laplace_trajectory_constituents(g.trajectory) t";

static SPIPlanPtr fold_plan = NULL;

typedef struct MoveEntry
{
    char    key[16];
    int64   plays;
    double  score_sum;
} MoveEntry;

static int
move_cmp(const void *a, const void *b)
{
    const MoveEntry *x = (const MoveEntry *) a;
    const MoveEntry *y = (const MoveEntry *) b;
    if (x->plays > y->plays) return -1;
    if (x->plays < y->plays) return 1;
    return memcmp(x->key, y->key, 16);
}

static void
ensure_plan(void)
{
    if (fold_plan != NULL)
        return;
    {
        Oid argtypes[1] = { INT4OID };
        SPIPlanPtr plan = SPI_prepare(FOLD_QUERY, 1, argtypes);
        if (plan == NULL)
            elog(ERROR, "chess_learned_moves: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "chess_learned_moves: SPI_keepplan failed");
        fold_plan = plan;
    }
}

PG_FUNCTION_INFO_V1(pg_laplace_chess_learned_moves);

Datum
pg_laplace_chess_learned_moves(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    int32          games = PG_ARGISNULL(0) ? 0 : PG_GETARG_INT32(0);
    bool           spi_top = false;
    HTAB          *moves;
    HASHCTL        ctl;

    InitMaterializedSRF(fcinfo, 0);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "chess_learned_moves: SPI_connect failed");
    ensure_plan();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(MoveEntry);
    ctl.hcxt = CurrentMemoryContext;
    moves = hash_create("chess learned moves", 8192, &ctl,
                        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    {
        Datum args[1] = { Int32GetDatum(games) };
        char  nulls[1] = { ' ' };
        int   rc = SPI_execute_plan(fold_plan, args, nulls, true, 0);

        if (rc != SPI_OK_SELECT)
            elog(ERROR, "chess_learned_moves: fold query failed: %s",
                 SPI_result_code_string(rc));

        for (uint64 row = 0; row < SPI_processed; row++)
        {
            HeapTuple  tuple = SPI_tuptable->vals[row];
            TupleDesc  desc = SPI_tuptable->tupdesc;
            bool       isnull;
            Datum      d_id, d_ord, d_score;
            bytea     *id;
            int32      ordinal;
            double     white_score;
            double     mover_score;
            MoveEntry *entry;
            bool       found;

            d_id = SPI_getbinval(tuple, desc, 1, &isnull);
            if (isnull) continue;
            id = DatumGetByteaPP(d_id);
            if (VARSIZE_ANY_EXHDR(id) != 16) continue;

            d_ord = SPI_getbinval(tuple, desc, 2, &isnull);
            if (isnull) continue;
            ordinal = DatumGetInt32(d_ord);

            d_score = SPI_getbinval(tuple, desc, 3, &isnull);
            if (isnull) continue;
            white_score = DatumGetFloat8(d_score);

            /* Ply parity is the mover: constituent ordinals are 1-based, so an odd
             * ordinal is White's move. The record already fixes who moved; nothing
             * here needs the board to work it out. */
            mover_score = (ordinal % 2 == 1) ? white_score : 1.0 - white_score;

            entry = (MoveEntry *) hash_search(moves, VARDATA_ANY(id),
                                              HASH_ENTER, &found);
            if (!found)
            {
                entry->plays = 0;
                entry->score_sum = 0.0;
            }
            entry->plays += 1;
            entry->score_sum += mover_score;
        }
    }

    {
        HASH_SEQ_STATUS seq;
        MoveEntry      *me;
        MoveEntry      *ordered;
        long            entries = hash_get_num_entries(moves);
        int             n, m = 0;

        if ((uint64) entries > (uint64) (MaxAllocSize / sizeof(MoveEntry)))
            ereport(ERROR,
                    (errmsg("chess_learned_moves: move set exceeds allocation capacity")));
        n = (int) entries;
        ordered = (MoveEntry *) palloc(sizeof(MoveEntry) * (n > 0 ? n : 1));

        hash_seq_init(&seq, moves);
        while ((me = (MoveEntry *) hash_seq_search(&seq)) != NULL)
            ordered[m++] = *me;
        qsort(ordered, (size_t) m, sizeof(MoveEntry), move_cmp);

        for (int i = 0; i < m; i++)
        {
            bytea *move_id = (bytea *) palloc(VARHDRSZ + 16);
            Datum  values[3];
            bool   isnulls[3] = { false, false, false };

            SET_VARSIZE(move_id, VARHDRSZ + 16);
            memcpy(VARDATA(move_id), ordered[i].key, 16);

            values[0] = PointerGetDatum(move_id);
            values[1] = Int64GetDatum(ordered[i].plays);
            values[2] = Float8GetDatum(ordered[i].plays > 0
                                       ? ordered[i].score_sum / (double) ordered[i].plays
                                       : 0.0);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, isnulls);
        }
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
