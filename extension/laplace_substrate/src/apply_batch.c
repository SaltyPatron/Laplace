#include "postgres.h"

#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/guc.h"

#include "access/htup_details.h"
#include "catalog/pg_type.h"

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

static void
spi_staged_insert_pair(const char *sql, int64 *staged, int64 *inserted)
{
    int   rc = SPI_execute(sql, false, 0);
    bool  sn1;
    bool  sn2;

    if (rc != SPI_OK_SELECT || SPI_processed != 1)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_apply_batch: staged insert failed (rc=%d): %s",
                        rc, sql)));
    *staged = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &sn1));
    *inserted = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],
                                            SPI_tuptable->tupdesc, 2, &sn2));
    if (sn1) *staged = 0;
    if (sn2) *inserted = 0;
}


static bool
staged_att_has_overlap(const char *stage_att)
{
    int rc = SPI_execute(psprintf(
        "SELECT EXISTS ("
        "  SELECT 1 FROM %s s "
        "  INNER JOIN laplace.attestations a ON a.id = s.id"
        ")", stage_att), true, 1);

    if (rc != SPI_OK_SELECT || SPI_processed != 1)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_apply_batch: attestation overlap probe failed")));
    {
        bool isnull;
        return DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull))
               && !isnull;
    }
}

static void
probe_stage_tables(const char *stage_ent, const char *stage_phys, const char *stage_att,
                   bool *has_ent, bool *has_phys, bool *has_att)
{
    int rc = SPI_execute(psprintf(
        "SELECT to_regclass('%s') IS NOT NULL, "
        "       to_regclass('%s') IS NOT NULL, "
        "       to_regclass('%s') IS NOT NULL",
        stage_ent, stage_phys, stage_att), true, 1);

    *has_ent = *has_phys = *has_att = false;
    if (rc == SPI_OK_SELECT && SPI_processed == 1)
    {
        bool isnull;
        Datum d;

        d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        *has_ent = !isnull && DatumGetBool(d);
        d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
        *has_phys = !isnull && DatumGetBool(d);
        d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3, &isnull);
        *has_att = !isnull && DatumGetBool(d);
    }
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
     * Serialize concurrent apply_batch calls against each other. Entity/physicality ids are
     * content-addressed (deterministic hash of content), so two concurrent decomposers can
     * independently compute the same "novel" id for the same content and both pass the
     * anti-join dedup check (LEFT JOIN ... WHERE id IS NULL) before either has committed --
     * a classic check-then-act race that surfaces as a raw entities_pkey/physicalities_pkey
     * unique violation. Taking this lock makes apply_batch calls mutually exclusive so the
     * second caller's anti-join always sees the first caller's committed rows and correctly
     * dedupes instead of racing. This is prevention, not retry-after-collision: the lock is
     * transaction-scoped (auto-released at commit/rollback) and acquired once per (already
     * bulk) apply_batch call, not per row, so it does not serialize the CPU-bound decompose/
     * compose work upstream of this -- only the final bulk write.
     */
    if (SPI_execute("SELECT pg_advisory_xact_lock(hashtextextended('laplace_apply_batch', 0))",
                     true, 0) != SPI_OK_SELECT)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("laplace_apply_batch: failed to acquire write serialization lock")));

    {
        bool has_ent = false, has_phys = false, has_att = false;
        const char *bulk = GetConfigOption("laplace_substrate.ingest_bulk_novel", true, false);
        bool bulk_novel = bulk && (pg_strcasecmp(bulk, "on") == 0
                                   || pg_strcasecmp(bulk, "true") == 0);

        probe_stage_tables(stage_ent, stage_phys, stage_att, &has_ent, &has_phys, &has_att);

        if (bulk_novel)
        {
            if (has_ent)
                ent_ins = spi_exec_count(psprintf(
                    "INSERT INTO laplace.entities "
                    "  (id, tier, type_id, first_observed_by, created_at) "
                    "SELECT DISTINCT ON (s.id) s.id, s.tier, s.type_id, s.first_observed_by, s.created_at "
                    "FROM %s s", stage_ent));
            if (has_phys)
                phys_ins = spi_exec_count(psprintf(
                    "INSERT INTO laplace.physicalities "
                    "  (id, entity_id, type, coord, hilbert_index, trajectory, "
                    "   n_constituents, alignment_residual, source_dim, observed_at) "
                    "SELECT DISTINCT ON (s.id) s.id, s.entity_id, s.type, s.coord, s.hilbert_index, s.trajectory, "
                    "       s.n_constituents, s.alignment_residual, s.source_dim, s.observed_at "
                    "FROM %s s ORDER BY s.id, s.hilbert_index", stage_phys));
            if (has_att)
                att_ins = spi_exec_count(psprintf(
                    "INSERT INTO laplace.attestations "
                    "  (id, subject_id, type_id, object_id, source_id, context_id, "
                    "   outcome, last_observed_at, observation_count, highway_mask) "
                    "SELECT DISTINCT ON (s.id) s.id, s.subject_id, s.type_id, s.object_id, s.source_id, "
                    "       s.context_id, s.outcome, s.last_observed_at, s.observation_count, "
                    "       s.highway_mask FROM %s s", stage_att));
            SPI_finish();
            values[0] = Int64GetDatum(ent_ins);
            values[1] = Int64GetDatum(phys_ins);
            values[2] = Int64GetDatum(att_ins);
            values[3] = Int64GetDatum(0);
            values[4] = Int64GetDatum(0);
            values[5] = Int64GetDatum(0);
            tuple = heap_form_tuple(tupdesc, values, nulls);
            PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
        }

    if (has_ent)
    {
        int64 staged = 0;

        spi_staged_insert_pair(psprintf(
            "WITH ins AS ("
            "  INSERT INTO laplace.entities "
            "    (id, tier, type_id, first_observed_by, created_at) "
            "  SELECT DISTINCT ON (s.id) s.id, s.tier, s.type_id, s.first_observed_by, s.created_at "
            "  FROM %s s "
            "  LEFT JOIN laplace.entities e ON e.id = s.id "
            "  WHERE e.id IS NULL "
            "  RETURNING 1"
            ") "
            "SELECT (SELECT count(*)::bigint FROM %s), "
            "       (SELECT count(*)::bigint FROM ins)",
            stage_ent, stage_ent), &staged, &ent_ins);
        if (staged > ent_ins) ent_skip = staged - ent_ins;
    }

    if (has_phys)
    {
        int64 staged = 0;

        spi_staged_insert_pair(psprintf(
            "WITH ins AS ("
            "  INSERT INTO laplace.physicalities "
            "    (id, entity_id, type, coord, hilbert_index, trajectory, "
            "     n_constituents, alignment_residual, source_dim, observed_at) "
            "  SELECT DISTINCT ON (s.id) s.id, s.entity_id, s.type, s.coord, s.hilbert_index, s.trajectory, "
            "         s.n_constituents, s.alignment_residual, s.source_dim, s.observed_at "
            "  FROM %s s "
            "  LEFT JOIN laplace.physicalities p ON p.id = s.id "
            "  WHERE p.id IS NULL "
            "  ORDER BY s.id, s.hilbert_index "
            "  RETURNING 1"
            ") "
            "SELECT (SELECT count(*)::bigint FROM %s), "
            "       (SELECT count(*)::bigint FROM ins)",
            stage_phys, stage_phys), &staged, &phys_ins);
        if (staged > phys_ins) phys_skip = staged - phys_ins;
    }

    if (has_att)
    {
            if (staged_att_has_overlap(stage_att))
            {
                att_fold = spi_exec_count(psprintf(
                    "WITH d AS MATERIALIZED ("
                    "  SELECT s.id, "
                    "         sum(s.observation_count)::bigint AS games, "
                    "         max(s.last_observed_at)          AS ts "
                    "  FROM %s s "
                    "  INNER JOIN laplace.attestations a ON a.id = s.id "
                    "  GROUP BY s.id "
                    "), locked AS MATERIALIZED ("
                    "  SELECT a.id FROM laplace.attestations a "
                    "  INNER JOIN d ON d.id = a.id "
                    "  FOR UPDATE OF a SKIP LOCKED "
                    ") "
                    "UPDATE laplace.attestations a SET "
                    "  observation_count = a.observation_count + d.games, "
                    "  last_observed_at  = GREATEST(a.last_observed_at, d.ts) "
                    "FROM d "
                    "WHERE a.id = d.id AND a.id IN (SELECT id FROM locked)",
                    stage_att));
            }

            att_ins = spi_exec_count(psprintf(
                "INSERT INTO laplace.attestations "
                "  (id, subject_id, type_id, object_id, source_id, context_id, "
                "   outcome, last_observed_at, observation_count, highway_mask) "
                "SELECT s.id, s.subject_id, s.type_id, s.object_id, s.source_id, "
                "       s.context_id, s.outcome, s.last_observed_at, s.observation_count, "
                "       s.highway_mask "
                "FROM ("
                "  SELECT s.id, "
                "         (array_agg(s.subject_id ORDER BY s.last_observed_at DESC))[1] AS subject_id, "
                "         (array_agg(s.type_id ORDER BY s.last_observed_at DESC))[1] AS type_id, "
                "         (array_agg(s.object_id ORDER BY s.last_observed_at DESC))[1] AS object_id, "
                "         (array_agg(s.source_id ORDER BY s.last_observed_at DESC))[1] AS source_id, "
                "         (array_agg(s.context_id ORDER BY s.last_observed_at DESC))[1] AS context_id, "
                "         (array_agg(s.outcome ORDER BY s.last_observed_at DESC))[1] AS outcome, "
                "         max(s.last_observed_at) AS last_observed_at, "
                "         sum(s.observation_count)::bigint AS observation_count, "
                "         (array_agg(s.highway_mask ORDER BY s.last_observed_at DESC))[1] AS highway_mask "
                "  FROM %s s GROUP BY s.id"
                ") s "
                "LEFT JOIN laplace.attestations a ON a.id = s.id "
                "WHERE a.id IS NULL",
                stage_att));
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
