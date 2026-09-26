using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Ingest verification and maintenance reads over the installed <c>ops.*</c> surface:
/// file-trunk carrier readback, receipt-journal verification, post-bulk ANALYZE and GIN
/// flush, evidence/content counts, and layer-completion probes.
/// </summary>
public static class NpgsqlIngestOps
{
    public sealed record FileCarrierVertex(long Ordinal, byte[] ChildId, long RunLength, long Flags);

    public static Task<IReadOnlyList<FileCarrierVertex>> VerifiedFileCarrierAsync(
        NpgsqlDataSource ds, Hash128 fileId, CancellationToken ct = default)
        => NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("ingest.verified_file_carrier"),
            static row => new FileCarrierVertex(Convert.ToInt64(row.GetValue(0)), row.GetFieldValue<byte[]>(1),
                Convert.ToInt64(row.GetValue(2)), Convert.ToInt64(row.GetValue(3))),
            p => {
                p.AddWithValue(NpgsqlDbType.Bytea, PhysicalityId.Compute(fileId, PhysicalityType.Content).ToBytes());
                p.AddWithValue(NpgsqlDbType.Bytea, fileId.ToBytes());
            }, ct: ct, label: "verified_file_carrier");

    public sealed record ArtifactJournalRow(string FileLabel, string Status, string Disposition,
        string RelativePath, byte[]? FileId);

    public static async Task<IReadOnlyList<ArtifactJournalRow>> VerifiedArtifactJournalAsync(
        NpgsqlDataSource ds, Guid runId, string sourceName, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        var statuses = await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ingest.verified_artifact_run_status"), static row => row.GetString(0),
            p => { p.AddWithValue(NpgsqlDbType.Uuid, runId); p.AddWithValue(NpgsqlDbType.Text, sourceName); },
            ct: ct, label: "verified_artifact_run_status").ConfigureAwait(false);
        if (statuses.Count != 1 || statuses[0] != "ok") throw new InvalidDataException("Actual ingest run journal does not confirm successful repository admission.");
        return await NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("ingest.verified_artifact_journal"),
            static row => new ArtifactJournalRow(row.GetString(0), row.GetString(1), row.GetString(2),
                row.GetString(3), row.IsDBNull(4) ? null : row.GetFieldValue<byte[]>(4)),
            p => p.AddWithValue(NpgsqlDbType.Uuid, runId), ct: ct, label: "verified_artifact_journal").ConfigureAwait(false);
    }

    public static Task AnalyzeCoreWriteTablesAsync(
        NpgsqlDataSource ds, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(ds, """
            ANALYZE laplace.attestations;
            ANALYZE laplace.consensus;
            ANALYZE laplace.entities;
            ANALYZE laplace.physicalities (entity_id, type)
            """, timeoutSeconds: 0, ct: ct, label: "analyze_core_write_tables");

    public static Task AnalyzePostIngestValidationAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(conn, """
            ANALYZE laplace.attestations (subject_id, source_id, type_id, object_id);
            ANALYZE laplace.physicalities (entity_id, type);
            ANALYZE laplace.entities (id);
            ANALYZE laplace.consensus (subject_id, type_id, object_id, rating, rd)
            """, timeoutSeconds: 0, ct: ct, label: "analyze_post_ingest_validation");

    /// <summary>
    /// Flushes every GIN pending list after a bulk write burst.
    /// <para>
    /// GIN with <c>fastupdate</c> buffers inserts in an unordered pending list that is
    /// merged only when it exceeds <c>gin_pending_list_limit</c>, and every search scans
    /// that list linearly until then. A large limit keeps bulk persistence cheap; flushing
    /// explicitly at the end of the burst means trajectory-constituent containment probes
    /// never scan a populated list, without waiting for autovacuum.
    /// </para>
    /// </summary>
    public static Task CleanGinPendingListsAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ExecuteNonQueryAsync(conn, SqlCatalog.Get("ingest.clean_gin_pending_lists").Text,
            timeoutSeconds: 0, ct: ct, label: "gin_clean_pending_lists");

    public static Task<long> EvidenceCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT ops.evidence_count(p_source => laplace.source_id(@s))
            """, p => p.AddWithValue("s", sourceKey), ct, "evidence_count_source_name");

    public static Task<long> ContentCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT ops.content_count(p_source => laplace.source_id(@s))
            """, p => p.AddWithValue("s", sourceKey), ct, "content_count_source_name");

    public static Task<long> EvidenceCountForRelationAsync(
        NpgsqlConnection conn, string relationType, string? sourceKey = null,
        CancellationToken ct = default) =>
        sourceKey is null
            ? ScalarLongAsync(conn, """
                SELECT ops.evidence_count(p_type => laplace.relation_type_id(@rel))
                """, p => p.AddWithValue("rel", relationType), ct, "evidence_count_relation")
            : ScalarLongAsync(conn, """
                SELECT ops.evidence_count(
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
            SELECT ops.evidence_count(
                p_type => laplace.relation_type_id(@rel), p_source => @src)
            """,
            p =>
            {
                p.AddWithValue("rel", relationType);
                p.Add("src", NpgsqlDbType.Bytea).Value = sourceId;
            }, ct, "evidence_count_relation_source_id");

    // Closes a journal row through ops.ingest_run_close, which refuses rows not in
    // status = 'running'. The op does not check liveness; the caller must know the run
    // is dead, e.g. by holding the global ingest mutex.
    public static Task<long> CloseIngestRunAsync(
        NpgsqlConnection conn, Guid runId, string status,
        CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT count(*) FROM ops.ingest_run_close(@run, @status)
            """,
            p =>
            {
                p.AddWithValue("run", runId);
                p.AddWithValue("status", status);
            }, ct, "ingest_run_close");

    // Whether the source has testimony under the given relation. Both names resolve to
    // ids before ops.source_bootstrap_present, which takes ids only.
    public static async Task<bool> SourceBootstrapPresentAsync(
        NpgsqlConnection conn, string sourceKey, string lawRelation,
        CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn, """
            SELECT ops.source_bootstrap_present(
                laplace.source_id(@src), laplace.relation_type_id(@rel))
            """,
            p =>
            {
                p.AddWithValue("src", sourceKey);
                p.AddWithValue("rel", lawRelation);
            }, timeoutSeconds: 0, ct: ct, label: "source_bootstrap_present").ConfigureAwait(false);
        return v is bool b && b;
    }

    // generation.probe: one reply row per (lane, seed) for one prompt, so a caller
    // can see whether a lane's reply varies with the seed.
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

    /// <summary>Whether the source has a completion receipt for the layer (laplace.ingest_layer_completion).</summary>
    public static async Task<bool> LayerCompletedAsync(
        NpgsqlConnection conn, int layer, Hash128 sourceId, CancellationToken ct = default)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn,
            "SELECT ops.layer_completed(@src, @layer)",
            p =>
            {
                p.AddWithValue("layer", layer);
                p.AddWithValue("src", sourceId.ToBytes());
            }, ct: ct, label: "layer_completed").ConfigureAwait(false);
        return v is true;
    }

    public static Task<long> ModelCircuitTrajectoryCountAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        ScalarLongAsync(conn, """
            SELECT consensus.entity_physicality_count(
                       realize.canonical_id('Model_Circuit'), 3)
            """, null, ct, "model_circuit_trajectory_count");

    public readonly record struct SourceEvidenceRow(string Source, long Evidence);

    public static Task<IReadOnlyList<SourceEvidenceRow>> AttestationCountsBySourceAsync(
        NpgsqlConnection conn, int timeoutSeconds = 120, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT s.source, s.evidence
            FROM ops.source_counts() s
            ORDER BY s.evidence DESC
            """,
            static r => new SourceEvidenceRow(r.GetString(0), r.GetInt64(1)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "attestation_counts_by_source");

    public readonly record struct UnicodeAtomProbeRow(
        string Render, short Tier, double X, double Y, double Z, double M);

    public static Task<IReadOnlyList<UnicodeAtomProbeRow>> UnicodeCapitalAContentProbeAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT realize.render(realize.canonical_id('A')), f.tier,
                   p.x, p.y, p.z, p.m
            FROM ops.entity_facets(realize.canonical_id('A')) f
            CROSS JOIN ops.entity_physicalities(realize.canonical_id('A')) p
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
