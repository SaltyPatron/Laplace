using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// CLI ingest ops: evidence/content counts, index-cycle journal, post-COPY ANALYZE,
/// validation probes. Hosts print; SQL stays here.
/// </summary>
public static class NpgsqlIngestOps
{
    public static async Task<bool> EvidenceExistsForTypeAndSourceAsync(
        NpgsqlDataSource ds, byte[] sourceId, byte[] typeId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await EvidenceExistsForTypeAndSourceAsync(conn, sourceId, typeId, ct).ConfigureAwait(false);
    }

    public static async Task<bool> EvidenceExistsForTypeAndSourceAsync(
        NpgsqlConnection conn, byte[] sourceId, byte[] typeId, CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn, """
            SELECT laplace.evidence_count(p_type => @type, p_source => @source) > 0
            """,
            p =>
            {
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
                p.Add("source", NpgsqlDbType.Bytea).Value = sourceId;
            }, ct: ct, label: "evidence_exists_type_source").ConfigureAwait(false);
        return v is true;
    }

    public static async Task<long> IndexCycleJournalCountAsync(
        NpgsqlDataSource ds, CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(ds,
            "SELECT count(*)::bigint FROM laplace.index_cycle_journal",
            ct: ct, label: "index_cycle_journal_count").ConfigureAwait(false);
        return AsLong(v);
    }

    public static Task AnalyzeCoreWriteTablesAsync(
        NpgsqlDataSource ds, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(ds, """
            ANALYZE laplace.attestations;
            ANALYZE laplace.consensus;
            ANALYZE laplace.entities
            """, timeoutSeconds: 0, ct: ct, label: "analyze_core_write_tables");

    public static Task AnalyzePostIngestValidationAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(conn, """
            ANALYZE laplace.attestations (subject_id, source_id, type_id, object_id);
            ANALYZE laplace.physicalities (entity_id, type);
            ANALYZE laplace.entities (id, tier, type_id);
            ANALYZE laplace.consensus (subject_id, type_id, object_id, rating, rd)
            """, timeoutSeconds: 0, ct: ct, label: "analyze_post_ingest_validation");

    /// <summary>
    /// Drain every GIN pending list once the write burst is over.
    /// <para>
    /// GIN with <c>fastupdate</c> buffers inserts into an unordered pending list and
    /// merges it into the main structure only when it exceeds
    /// <c>gin_pending_list_limit</c> — in the FOREGROUND of whichever backend crosses
    /// the threshold. A large limit is what makes a bulk seed cheap, because merges
    /// batch and same-key entries combine into one posting list update. But the
    /// pending list is scanned LINEARLY on every search until it is flushed, so
    /// whatever is left sitting there after an ingest is a tax on exactly the probes
    /// these indexes exist to serve — and the containment probe on
    /// physicalities_constituents_gin is the read model's hot path.
    /// </para>
    /// <para>
    /// Flushing here removes the trade-off instead of splitting it: the limit can be
    /// sized for write batching, and readers never scan a populated list, because the
    /// burst always ends with this. Explicit rather than left to autovacuum, which
    /// also cleans pending lists but on its own schedule — the first query after a
    /// seed should not be the thing that pays.
    /// </para>
    /// </summary>
    public static Task CleanGinPendingListsAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(conn, """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT i.indexrelid::regclass AS idx
                    FROM pg_index i
                    JOIN pg_class c  ON c.oid = i.indexrelid
                    JOIN pg_am    am ON am.oid = c.relam
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE am.amname = 'gin' AND n.nspname = 'laplace'
                LOOP
                    -- Per-index and tolerant: a partition dropped concurrently, or an
                    -- index built without fastupdate, must not abort the sweep.
                    BEGIN
                        PERFORM gin_clean_pending_list(r.idx);
                    EXCEPTION WHEN OTHERS THEN
                        NULL;
                    END;
                END LOOP;
            END $$;
            """, timeoutSeconds: 0, ct: ct, label: "gin_clean_pending_lists");

    public static Task<long> EvidenceCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT laplace.evidence_count(p_source => laplace.source_id(@s))
            """, p => p.AddWithValue("s", sourceKey), ct, "evidence_count_source_name");

    public static Task<long> ContentCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT laplace.content_count(p_source => laplace.source_id(@s))
            """, p => p.AddWithValue("s", sourceKey), ct, "content_count_source_name");

    public static Task<long> EvidenceCountForRelationAsync(
        NpgsqlConnection conn, string relationType, string? sourceKey = null,
        CancellationToken ct = default) =>
        sourceKey is null
            ? ScalarLongAsync(conn, """
                SELECT laplace.evidence_count(p_type => laplace.relation_type_id(@rel))
                """, p => p.AddWithValue("rel", relationType), ct, "evidence_count_relation")
            : ScalarLongAsync(conn, """
                SELECT laplace.evidence_count(
                    p_type => laplace.relation_type_id(@rel),
                    p_source => laplace.source_id(@src))
                """, p =>
                {
                    p.AddWithValue("rel", relationType);
                    p.AddWithValue("src", sourceKey);
                }, ct, "evidence_count_relation_source");

    public static Task<long> EvidenceCountForRelationAndSourceIdAsync(
        NpgsqlConnection conn, string relationType, byte[] sourceId,
        CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT laplace.evidence_count(
                p_type => laplace.relation_type_id(@rel), p_source => @src)
            """,
            p =>
            {
                p.AddWithValue("rel", relationType);
                p.Add("src", NpgsqlDbType.Bytea).Value = sourceId;
            }, ct, "evidence_count_relation_source_id");

    // Close an orphaned journal row through the installed op. The op refuses
    // rows not in status='running'; the CALLER owes the liveness proof — the
    // sanctioned moment is while holding the global ingest mutex, when no other
    // ingest can be alive by construction.
    public static Task<long> CloseIngestRunAsync(
        NpgsqlConnection conn, Guid runId, string status,
        CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT count(*) FROM laplace.ingest_run_close(@run, @status)
            """,
            p =>
            {
                p.AddWithValue("run", runId);
                p.AddWithValue("status", status);
            }, ct, "ingest_run_close");

    // Positive control for the roster's bootstrap filter (#760): the relation
    // vocabulary is declared here, at the caller, and resolved to an id before
    // it reaches SQL — the installed op takes ids only.
    public static async Task<bool> SourceBootstrapPresentAsync(
        NpgsqlConnection conn, string sourceKey, string lawRelation,
        CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn, """
            SELECT laplace.source_bootstrap_present(
                laplace.source_id(@src), laplace.relation_type_id(@rel))
            """,
            p =>
            {
                p.AddWithValue("src", sourceKey);
                p.AddWithValue("rel", lawRelation);
            }, timeoutSeconds: 0, ct: ct, label: "source_bootstrap_present").ConfigureAwait(false);
        return v is bool b && b;
    }

    // W5 seed-variance probe through the installed op (laplace.generation_probe):
    // both generation lanes over one prompt and a seed set, one row per
    // (lane, seed). Replay — the failure converse_compose's header gates wiring
    // on — is distinct-reply-count == 1 for a lane across multiple seeds.
    public static async Task<List<(string Lane, long Seed, string? Reply)>> GenerationProbeAsync(
        NpgsqlConnection conn, string prompt, long[] seeds, int steps,
        CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT lane, seed, reply FROM generation.probe(@p, @s, @n)";
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("p", prompt);
        cmd.Parameters.Add("s", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = seeds;
        cmd.Parameters.AddWithValue("n", steps);
        var rows = new List<(string, long, string?)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add((r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return rows;
    }

    public static async Task<bool> LayerMarkedCompleteAsync(
        NpgsqlConnection conn, int layer, string sourceKey, CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn, """
            SELECT laplace.evidence_count(
                p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/' || @layer::text || '/v1'),
                p_source => laplace.source_id(@src)) > 0
            """,
            p =>
            {
                p.AddWithValue("layer", layer);
                p.AddWithValue("src", sourceKey);
            }, ct: ct, label: "layer_marked_complete").ConfigureAwait(false);
        return v is true;
    }

    public static Task<long> ModelCircuitTrajectoryCountAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT consensus.entity_physicality_count(
                       laplace.canonical_id('Model_Circuit'), 3)
            """, null, ct, "model_circuit_trajectory_count");

    public readonly record struct SourceEvidenceRow(string Source, long Evidence);

    public static Task<IReadOnlyList<SourceEvidenceRow>> AttestationCountsBySourceAsync(
        NpgsqlConnection conn, int timeoutSeconds = 120, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT s.source, s.evidence
            FROM laplace.source_counts() s
            ORDER BY s.evidence DESC
            """,
            static r => new SourceEvidenceRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "attestation_counts_by_source");

    public readonly record struct UnicodeAtomProbeRow(
        string Render, short Tier, double X, double Y, double Z, double M);

    public static Task<IReadOnlyList<UnicodeAtomProbeRow>> UnicodeCapitalAContentProbeAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT laplace.render(laplace.canonical_id('A')), f.tier,
                   p.x, p.y, p.z, p.m
            FROM laplace.entity_facets(laplace.canonical_id('A')) f
            CROSS JOIN laplace.entity_physicalities(laplace.canonical_id('A')) p
            WHERE p.type = 1
            """,
            static r => new UnicodeAtomProbeRow(
                r.GetString(0), r.GetInt16(1),
                r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            ct: ct, label: "unicode_capital_a_probe");

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection conn, string sql, Action<NpgsqlParameterCollection>? bind,
        CancellationToken ct, string label)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(
            conn, sql, bind, timeoutSeconds: 0, ct: ct, label: label).ConfigureAwait(false);
        return AsLong(v);
    }

    private static long AsLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        null => 0L,
        _ => Convert.ToInt64(v),
    };
}
