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

    public static async Task AnalyzeCoreWriteTablesAsync(
        NpgsqlDataSource ds, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = NpgsqlCatalog.Command(conn, null, "ingest.analyze_core_write_tables");
        await AnalyzeOrThrowAsync(command, "analyze_core_write_tables", ct).ConfigureAwait(false);
    }

    public static async Task AnalyzePostIngestValidationAsync(
        NpgsqlConnection conn, CancellationToken ct = default)
    {
        await using var command = NpgsqlCatalog.Command(conn, null, "ingest.analyze_post_ingest_validation");
        await AnalyzeOrThrowAsync(command, "analyze_post_ingest_validation", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs ANALYZE statements and fails if PostgreSQL skipped any relation.
    /// <para>
    /// ANALYZE by a role without MAINTAIN on a table, or on any leaf partition of it,
    /// does not fail: each such relation is skipped with a WARNING ("permission denied to
    /// analyze ..., skipping it") and the statement succeeds. A seed run that way left
    /// every leaf at <c>reltuples = -1</c> and reported success (Laplace#1879). Every
    /// WARNING the statements raise is collected here, and any one of them is a failure
    /// that names the relations not analyzed.
    /// </para>
    /// </summary>
    public static async Task AnalyzeOrThrowAsync(
        NpgsqlCommand command, string label, CancellationToken ct = default)
    {
        var conn = command.Connection ?? throw new ArgumentException("ANALYZE requires a connection.", nameof(command));
        var warnings = new List<string>();
        void OnNotice(object sender, NpgsqlNoticeEventArgs e)
        {
            if (string.Equals(e.Notice.InvariantSeverity, "WARNING", StringComparison.Ordinal))
                lock (warnings) warnings.Add(e.Notice.MessageText);
        }
        conn.Notice += OnNotice;
        try
        {
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            conn.Notice -= OnNotice;
        }
        if (AnalyzeSkipFailure(label, warnings) is { } failure)
            throw failure;
    }

    /// <summary>The failure for ANALYZE warnings, or null when there were none. Each
    /// relation PostgreSQL names in a warning is listed once, in order.</summary>
    internal static AnalyzeIncompleteException? AnalyzeSkipFailure(
        string label, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return null;
        var relations = new List<string>();
        foreach (string warning in warnings)
        {
            var quoted = System.Text.RegularExpressions.Regex.Match(warning, "\"([^\"]+)\"");
            string name = quoted.Success ? quoted.Groups[1].Value : warning;
            if (!relations.Contains(name)) relations.Add(name);
        }
        return new AnalyzeIncompleteException(label, relations, warnings);
    }

    public sealed record GinMaintenance(string Index, long? PagesCleaned);

    /// <summary>
    /// Flushes owned GIN pending lists after a bulk write burst; unowned indexes
    /// remain under PostgreSQL's autovacuum maintenance.
    /// <para>
    /// GIN with <c>fastupdate</c> buffers inserts in an unordered pending list that is
    /// merged only when it exceeds <c>gin_pending_list_limit</c>, and every search scans
    /// that list linearly until then. A large limit keeps bulk persistence cheap; flushing
    /// explicitly at the end of the burst means trajectory-constituent containment probes
    /// never scan a populated list, without waiting for autovacuum.
    /// </para>
    /// </summary>
    public static Task<IReadOnlyList<GinMaintenance>> CleanGinPendingListsAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ingest.clean_gin_pending_lists"),
            static row => new GinMaintenance(row.GetString(0), row.IsDBNull(1) ? null : row.GetInt64(1)),
            timeoutSeconds: 0, ct: ct, label: "gin_clean_pending_lists");

    public static Task<long> EvidenceCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, SqlCatalog.Get("ingest.evidence_count_for_source_name"), p => p.AddWithValue("s", sourceKey), ct, "evidence_count_source_name");

    public static Task<long> ContentCountForSourceNameAsync(
        NpgsqlConnection conn, string sourceKey, CancellationToken ct = default) =>
        ScalarLongAsync(conn, SqlCatalog.Get("ingest.content_count_for_source_name"), p => p.AddWithValue("s", sourceKey), ct, "content_count_source_name");

    public static Task<long> EvidenceCountForRelationAsync(
        NpgsqlConnection conn, string relationType, string? sourceKey = null,
        CancellationToken ct = default) =>
        sourceKey is null
            ? ScalarLongAsync(conn, SqlCatalog.Get("ingest.evidence_count_for_relation"), p => p.AddWithValue("rel", relationType), ct, "evidence_count_relation")
            : ScalarLongAsync(conn, SqlCatalog.Get("ingest.evidence_count_for_relation_source"), p =>
                {
                    p.AddWithValue("rel", relationType);
                    p.AddWithValue("src", sourceKey);
                }, ct, "evidence_count_relation_source");

    public static Task<long> EvidenceCountForRelationAndSourceIdAsync(
        NpgsqlConnection conn, string relationType, byte[] sourceId,
        CancellationToken ct = default) =>
        ScalarLongAsync(conn, SqlCatalog.Get("ingest.evidence_count_for_relation_source_id"),
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
        ScalarLongAsync(conn, SqlCatalog.Get("ingest.close_run_count"),
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
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(conn, SqlCatalog.Get("ingest.source_bootstrap_present"),
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
        await using var cmd = NpgsqlCatalog.Command(conn, null, "generation.probe", prompt, seeds, steps);
        cmd.CommandTimeout = 0;
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
            SqlCatalog.Get("ingest.source_layer_completed_operation"),
            p =>
            {
                p.AddWithValue("layer", layer);
                p.AddWithValue("src", sourceId.ToBytes());
            }, ct: ct, label: "layer_completed").ConfigureAwait(false);
        return v is true;
    }

    public static Task<long> ModelCircuitTrajectoryCountAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        ScalarLongAsync(conn, SqlCatalog.Get("ingest.model_circuit_trajectory_count"), null, ct, "model_circuit_trajectory_count");

    public readonly record struct SourceEvidenceRow(string? Source, long? Evidence, string? SourceId);

    public static Task<IReadOnlyList<SourceEvidenceRow>> AttestationCountsBySourceAsync(
        NpgsqlConnection conn, int timeoutSeconds = 120, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ingest.attestation_counts_by_source"),
            static r => new SourceEvidenceRow(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2)),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "attestation_counts_by_source");

    public readonly record struct UnicodeAtomProbeRow(
        string Render, short Tier, double X, double Y, double Z, double M);

    public static Task<IReadOnlyList<UnicodeAtomProbeRow>> UnicodeCapitalAContentProbeAsync(
        NpgsqlConnection conn, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ingest.unicode_capital_a_content_probe"),
            static r => new UnicodeAtomProbeRow(
                r.GetString(0), r.GetInt16(1),
                r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            ct: ct, label: "unicode_capital_a_probe");

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection conn, NativeSqlQuery query, Action<NpgsqlParameterCollection>? bind,
        CancellationToken ct, string label)
    {
        var v = await NpgsqlRead.ExecuteScalarAsync<object>(
            conn, query, bind, timeoutSeconds: 0, ct: ct, label: label).ConfigureAwait(false);
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

/// <summary>ANALYZE skipped at least one relation, so its planner statistics (and every
/// reltuples / pg_stats read over it) were not refreshed.</summary>
public sealed class AnalyzeIncompleteException(
    string label, IReadOnlyList<string> relations, IReadOnlyList<string> warnings)
    : InvalidOperationException(
        $"{label}: ANALYZE skipped {relations.Count} relation(s), so their statistics were not refreshed: "
        + string.Join(", ", relations)
        + $". First warning: {warnings[0]}"
        + (warnings[0].Contains("permission denied", StringComparison.Ordinal)
            ? ". The connecting role needs MAINTAIN on these tables and every partition of them."
            : ""))
{
    public IReadOnlyList<string> Relations { get; } = relations;
}
