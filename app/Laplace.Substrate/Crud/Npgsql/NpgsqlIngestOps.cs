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
            SELECT count(*)::bigint
            FROM laplace.physicalities p
            JOIN laplace.entities e ON e.id = p.entity_id
            WHERE p.type = 3 AND e.type_id = laplace.canonical_id('Model_Circuit')
            """, null, ct, "model_circuit_trajectory_count");

    public readonly record struct SourceEvidenceRow(string Source, long Evidence);

    public static Task<IReadOnlyList<SourceEvidenceRow>> AttestationCountsBySourceAsync(
        NpgsqlConnection conn, int timeoutSeconds = 120, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            SELECT laplace.render(a.source_id) AS source, count(*)::bigint AS evidence
            FROM laplace.attestations a
            GROUP BY a.source_id
            ORDER BY evidence DESC
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
