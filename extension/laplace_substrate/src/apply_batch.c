/*
 * laplace_apply_batch — the ONE light set-based merge of a staged batch.
 *
 * ARCHITECTURE (do not reimplement merge logic in PL/pgSQL or C#):
 *   The client (C# via the laplace_core P/Invoke) does ALL the heavy lifting —
 *   parse / TreeSitter compose / BLAKE3 content ids / 4D geometry / tier build —
 *   then streams the finished batch in ONE pass by COPYing the native, already-
 *   computed entity / physicality / attestation tuples into three UNLOGGED
 *   staging tables shaped exactly like laplace.{entities,physicalities,
 *   attestations}. This function is the single server-side call that folds that
 *   staging into the live substrate, in-process and set-based, in ONE round trip.
 *
 *   Novelty is decided HERE, set-based, never per row:
 *     - entities      : INSERT … SELECT DISTINCT ON (id) … WHERE NOT EXISTS (id).
 *                       Entities are pure content addresses (same id ⇔ same bytes),
 *                       so the id anti-join is exact and idempotent. NO ON CONFLICT.
 *     - physicalities : dedup key is the BLAKE3 id PK only (schema forbids an
 *                       (entity_id,type) unique — "dedup is the hash"). DISTINCT
 *                       ON (id), id anti-join, ORDER BY hilbert_index for sequential
 *                       index maintenance. NO ON CONFLICT.
 *     - attestations  : novel ids INSERTed (id anti-join, NO ON CONFLICT);
 *                       ids the substrate already holds are NOT dropped — their
 *                       observation_count and last_observed_at are folded
 *                       (count += staged count) under a FOR UPDATE SKIP LOCKED
 *                       lock so a re-observation is summed, never lost.
 *
 *   The client stages a pre-filtered novel set (merkle descent before compose).
 *   Conflicts at merge time should be ≈0; entities_skipped / physicalities_skipped
 *   in the return tuple instrument any row that was staged but already present.
 *   NO ON CONFLICT — append-only of the novel set; re-observations fold via
 *   attestations only.
 *
 *   This mirrors the sanctioned binding pattern of consensus_fold_one_partition /
 *   pg_laplace_consensus_fold_partition: a C function reached from a thin SQL
 *   `CREATE FUNCTION … LANGUAGE C` that orchestrates SPI set operations and calls
 *   the native core kernels — NOT logic reimplemented in PL/pgSQL. The DB does
 *   only this light merge; all heavy compute already ran client-side in the core.
 */
#include "postgres.h"

#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/builtins.h"

#include "access/htup_details.h"
#include "catalog/pg_type.h"

/* A staging prefix is an unqualified identifier the C# writer chose; we only ever
 * embed it via quote_identifier so it cannot be an injection vector. */
static char *
quoted_stage(const char *prefix, const char *suffix)
{
    const char *q = quote_identifier(psprintf("%s%s", prefix, suffix));
    return pstrdup(q);
}

static int64
spi_exec_count(const char *sql)
{
    int rc = SPI_execute(sql, false, 0);

    if (rc != SPI_OK_INSERT && rc != SPI_OK_UPDATE && rc != SPI_OK_INSERT_RETURNING)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_apply_batch: statement failed (rc=%d): %s", rc, sql)));
    return (int64) SPI_processed;
}


PG_FUNCTION_INFO_V1(pg_laplace_apply_batch);

Datum
pg_laplace_apply_batch(PG_FUNCTION_ARGS)
{
    text       *prefix_t = PG_GETARG_TEXT_PP(0);
    char       *prefix   = text_to_cstring(prefix_t);

    char       *stage_ent  = quoted_stage(prefix, "entities");
    char       *stage_phys = quoted_stage(prefix, "physicalities");
    char       *stage_att  = quoted_stage(prefix, "attestations");

    int64       ent_ins  = 0;
    int64       phys_ins = 0;
    int64       att_ins  = 0;
    int64       att_fold = 0;
    int64       ent_skip = 0;
    int64       phys_skip = 0;

    TupleDesc   tupdesc;
    Datum       values[6];
    bool        nulls[6] = { false, false, false, false, false, false };
    HeapTuple   tuple;

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
                (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                 errmsg("laplace_apply_batch must return record")));
    BlessTupleDesc(tupdesc);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_apply_batch: SPI_connect failed")));

    /*
     * Bulk merge, matching the consensus fold (materialize_period_partition_fresh): triggers/FK OFF
     * for the duration. Content is content-addressed and the client stages entities with their
     * physicalities, so referential integrity holds by construction — the per-row FK RI trigger is
     * pure RBAR tax (measured ~1.3 s / 50k rows). Disabling triggers does NOT disable CHECK
     * constraints (octet_length, n_constituents) — those still fire. SET (session-scoped) on this
     * dedicated apply connection; restored when the connection is recycled.
     */
    SPI_execute("SET session_replication_role = replica", false, 0);

    /*
     * (1) entities — set-based staging merge (§3.4): dedupe staging once, subtract existing
     * ids in ONE hash anti-join into a temp novel set, then append without per-row lookup.
     * Client merkle descent should make conflicts ≈0; ent_skip instruments any slip-through.
     */
    if (SPI_execute(psprintf("SELECT to_regclass('%s') IS NOT NULL", stage_ent),
                    true, 1) == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        bool present = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                                  SPI_tuptable->tupdesc, 1, &isnull));
        if (!isnull && present)
        {
            char *stage_dedup = quoted_stage(prefix, "entities_dedup");
            char *stage_novel = quoted_stage(prefix, "entities_novel");
            int64 staged = 0;

            SPI_execute(psprintf(
                "CREATE TEMP TABLE %s ON COMMIT DROP AS "
                "SELECT DISTINCT ON (s.id) "
                "       s.id, s.tier, s.type_id, s.first_observed_by, s.created_at "
                "FROM %s s ORDER BY s.id",
                stage_dedup, stage_ent), false, 0);

            if (SPI_execute(psprintf("SELECT count(*) FROM %s", stage_dedup),
                            true, 1) == SPI_OK_SELECT && SPI_processed == 1)
            {
                bool sn;
                staged = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                                       SPI_tuptable->tupdesc, 1, &sn));
                if (sn) staged = 0;
            }

            SPI_execute(psprintf(
                "CREATE TEMP TABLE %s ON COMMIT DROP AS "
                "SELECT d.id, d.tier, d.type_id, d.first_observed_by, d.created_at "
                "FROM %s d "
                "LEFT JOIN laplace.entities e ON e.id = d.id "
                "WHERE e.id IS NULL",
                stage_novel, stage_dedup), false, 0);

            ent_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.entities "
                "  (id, tier, type_id, first_observed_by, created_at) "
                "SELECT id, tier, type_id, first_observed_by, created_at "
                "FROM %s ORDER BY id", stage_novel));
            if (staged > ent_ins) ent_skip = staged - ent_ins;
            pfree(stage_dedup);
            pfree(stage_novel);
        }
    }

    /*
     * (2) physicalities — id PK only (no phantom (entity_id,type) unique). Staging merge
     * subtracts existing ids set-based; final INSERT is sorted by hilbert_index for sequential
     * GiST/B-tree append (no per-row existence check in the INSERT itself).
     */
    if (SPI_execute(psprintf("SELECT to_regclass('%s') IS NOT NULL", stage_phys),
                    true, 1) == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        bool present = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                                  SPI_tuptable->tupdesc, 1, &isnull));
        if (!isnull && present)
        {
            char *stage_dedup = quoted_stage(prefix, "physicalities_dedup");
            char *stage_novel = quoted_stage(prefix, "physicalities_novel");
            int64 staged = 0;

            SPI_execute(psprintf(
                "CREATE TEMP TABLE %s ON COMMIT DROP AS "
                "SELECT DISTINCT ON (s.id) "
                "       s.id, s.entity_id, s.type, s.coord, s.hilbert_index, s.trajectory, "
                "       s.n_constituents, s.alignment_residual, s.source_dim, s.observed_at "
                "FROM %s s ORDER BY s.id",
                stage_dedup, stage_phys), false, 0);

            if (SPI_execute(psprintf("SELECT count(*) FROM %s", stage_dedup),
                            true, 1) == SPI_OK_SELECT && SPI_processed == 1)
            {
                bool sn;
                staged = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                                       SPI_tuptable->tupdesc, 1, &sn));
                if (sn) staged = 0;
            }

            SPI_execute(psprintf(
                "CREATE TEMP TABLE %s ON COMMIT DROP AS "
                "SELECT d.id, d.entity_id, d.type, d.coord, d.hilbert_index, d.trajectory, "
                "       d.n_constituents, d.alignment_residual, d.source_dim, d.observed_at "
                "FROM %s d "
                "LEFT JOIN laplace.physicalities p ON p.id = d.id "
                "WHERE p.id IS NULL",
                stage_novel, stage_dedup), false, 0);

            phys_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.physicalities "
                "  (id, entity_id, type, coord, hilbert_index, trajectory, "
                "   n_constituents, alignment_residual, source_dim, observed_at) "
                "SELECT id, entity_id, type, coord, hilbert_index, trajectory, "
                "       n_constituents, alignment_residual, source_dim, observed_at "
                "FROM %s ORDER BY hilbert_index", stage_novel));
            if (staged > phys_ins) phys_skip = staged - phys_ins;
            pfree(stage_dedup);
            pfree(stage_novel);
        }
    }

    /*
     * (3) attestations — novel ids INSERTed (id anti-join, no ON CONFLICT);
     * already-present ids fold their observation_count (sum) + last_observed_at
     * (greatest) under FOR UPDATE SKIP LOCKED so a re-observation is summed.
     * Both halves read the SAME staging once (de-duplicated to a per-id total).
     */
    if (SPI_execute(psprintf("SELECT to_regclass('%s') IS NOT NULL", stage_att),
                    true, 1) == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        bool present = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                                  SPI_tuptable->tupdesc, 1, &isnull));
        if (!isnull && present)
        {
            /*
             * Fold FIRST, then insert the novel ids. The fold's EXISTS predicate must
             * see only the ids the substrate held BEFORE this batch — running the novel
             * INSERT first would make freshly-inserted ids match EXISTS and double-count
             * their staged observation_count. present-set and novel-set are disjoint, so
             * folding before inserting is exact.
             */
            att_fold = spi_exec_count(psprintf(
                "WITH d AS MATERIALIZED ("
                "  SELECT s.id, "
                "         sum(s.observation_count)::bigint AS games, "
                "         max(s.last_observed_at)          AS ts "
                "  FROM %s s "
                "  WHERE EXISTS (SELECT 1 FROM laplace.attestations a WHERE a.id = s.id) "
                "  GROUP BY s.id "
                "), locked AS MATERIALIZED ("
                "  SELECT a.id FROM laplace.attestations a "
                "  WHERE a.id IN (SELECT id FROM d) "
                "  ORDER BY a.id FOR UPDATE SKIP LOCKED "
                ") "
                "UPDATE laplace.attestations a SET "
                "  observation_count = a.observation_count + d.games, "
                "  last_observed_at  = GREATEST(a.last_observed_at, d.ts) "
                "FROM d "
                "WHERE a.id = d.id AND a.id IN (SELECT id FROM locked)",
                stage_att));

            /* novel: INSERT the ids the substrate did not hold before this batch, as one
             * set-based id anti-join. The fold above ran first, so present-set and novel-set
             * are disjoint; this inserts only the genuinely new ids (DISTINCT ON collapses any
             * within-batch repeats). */
            att_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.attestations "
                "  (id, subject_id, type_id, object_id, source_id, context_id, "
                "   outcome, last_observed_at, observation_count) "
                "SELECT DISTINCT ON (s.id) "
                "       s.id, s.subject_id, s.type_id, s.object_id, s.source_id, "
                "       s.context_id, s.outcome, s.last_observed_at, s.observation_count "
                "FROM %s s "
                "WHERE NOT EXISTS (SELECT 1 FROM laplace.attestations a WHERE a.id = s.id) "
                "ORDER BY s.id", stage_att));
        }
    }

    SPI_finish();

    values[0] = Int64GetDatum(ent_ins);
    values[1] = Int64GetDatum(phys_ins);
    values[2] = Int64GetDatum(att_ins);
    values[3] = Int64GetDatum(att_fold);
    values[4] = Int64GetDatum(ent_skip);
    values[5] = Int64GetDatum(phys_skip);
    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}
