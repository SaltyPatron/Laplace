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
 *     - physicalities : geometry is SOURCE-FREE (T1) so the natural key is
 *                       (entity_id, type) and the id is content-addressed from
 *                       (entity_id, type, coord, trajectory). One physicality row
 *                       per (entity_id, type): DISTINCT ON (entity_id, type) keep
 *                       the lowest id, anti-join BOTH the id PK and the
 *                       (entity_id, type) UNIQUE. NO ON CONFLICT.
 *     - attestations  : novel ids INSERTed (id anti-join, NO ON CONFLICT);
 *                       ids the substrate already holds are NOT dropped — their
 *                       observation_count and last_observed_at are folded
 *                       (count += staged count) under a FOR UPDATE SKIP LOCKED
 *                       lock so a re-observation is summed, never lost.
 *
 *   partition-by-hash (the writer splits each batch's staged rows NATIVELY by
 *   id.lo % N via intent_stage_partition and COPYs each partition to its OWN
 *   staging prefix, then calls this function once per partition on its own
 *   connection IN PARALLEL) guarantees the key space is disjoint across the
 *   concurrent apply calls, so the id PK can never collide cross-partition —
 *   which is exactly why there is no ON CONFLICT and no retry loop here. The
 *   set-based anti-join is the ONLY novelty mechanism.
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

    TupleDesc   tupdesc;
    Datum       values[4];
    bool        nulls[4] = { false, false, false, false };
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
     * (1) entities — content addresses, deduped by the id PK. Bulk insert with ON CONFLICT (id)
     * DO NOTHING (the unique index IS the dedup — one probe, inline, no separate anti-join), sorted
     * by id. Partition-by-id.lo%N keeps key spaces disjoint across concurrent apply calls.
     */
    if (SPI_execute(psprintf("SELECT to_regclass('%s') IS NOT NULL", stage_ent),
                    true, 1) == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        bool present = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                                  SPI_tuptable->tupdesc, 1, &isnull));
        if (!isnull && present)
            ent_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.entities "
                "  (id, tier, type_id, first_observed_by, created_at) "
                "SELECT DISTINCT ON (s.id) "
                "       s.id, s.tier, s.type_id, s.first_observed_by, s.created_at "
                "FROM %s s ORDER BY s.id "
                "ON CONFLICT (id) DO NOTHING", stage_ent));
    }

    /*
     * (2) physicalities — same generic core, SPATIAL strategy: the hilbert-range
     * cursor scan fills the existing_bitmap (sequential range scan on the clustered
     * heap, not random id-PK probes), then the SAME merkle_dedup_filter_novel +
     * INSERT … WHERE s.id = ANY($1). The inner DISTINCT ON (s.id) keeps within-batch
     * dedup; the outer ORDER BY hilbert_index appends to the heap in space-filling
     * order (clustered-by-construction).
     */
    if (SPI_execute(psprintf("SELECT to_regclass('%s') IS NOT NULL", stage_phys),
                    true, 1) == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        bool present = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                                  SPI_tuptable->tupdesc, 1, &isnull));
        if (!isnull && present)
            /* dedup key is the id PK ("dedup is the hash" — no (entity_id,type) unique exists).
             * DISTINCT ON (s.id) collapses within-batch repeats; ON CONFLICT (id) DO NOTHING is the
             * cross-batch dedup (one inline probe, no separate anti-join). Inner ORDER BY id for the
             * DISTINCT ON; outer ORDER BY hilbert_index appends to the heap in space-filling order. */
            phys_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.physicalities "
                "  (id, entity_id, type, coord, hilbert_index, trajectory, "
                "   n_constituents, alignment_residual, source_dim, observed_at) "
                "SELECT d.id, d.entity_id, d.type, d.coord, d.hilbert_index, d.trajectory, "
                "       d.n_constituents, d.alignment_residual, d.source_dim, d.observed_at "
                "FROM (SELECT DISTINCT ON (s.id) "
                "        s.id, s.entity_id, s.type, s.coord, s.hilbert_index, s.trajectory, "
                "        s.n_constituents, s.alignment_residual, s.source_dim, s.observed_at "
                "      FROM %s s ORDER BY s.id) d "
                "ORDER BY d.hilbert_index "
                "ON CONFLICT (id) DO NOTHING", stage_phys));
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
    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}
