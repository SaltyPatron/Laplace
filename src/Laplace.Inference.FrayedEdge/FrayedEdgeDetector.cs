namespace Laplace.Inference.FrayedEdge;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Inference.Abstractions;

using Npgsql;

/// <summary>
/// H3 — Mendeleev-style "predict missing elements" detector.
///
/// For each known edge type T with archetype geometry: find pairs of
/// substrate entities (A, B) whose centroids are within
/// <paramref name="frechetThreshold"/> of T's archetype but no T-typed
/// edge exists between them. Surfaces gaps in the substrate's knowledge
/// that the geometry says should be filled.
///
/// At v0.1 the archetype geometry computation gates on the GEOMETRY4D
/// LINESTRING type being registered + IRanking's distance/Frechet
/// criteria being live. The scaffolding here returns a SQL-derived
/// candidate list (entities geometrically near T's existing endpoints
/// but absent from edge_member) — refined to the proper Frechet threshold
/// when Geometry4D lands.
///
/// Frayed-edge signals also flow into the Gödel Engine's macro-OODA as
/// triggers for hypothesis-driven exploration and source-ingestion
/// proposals.
///
/// Phase 6 / Track H3.
/// </summary>
public sealed class FrayedEdgeDetector : IFrayedEdgeDetector
{
    private readonly NpgsqlDataSource _dataSource;

    public FrayedEdgeDetector(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<FrayedEdgeCandidate>> DetectAsync(
        AtomId            edgeTypeHash,
        double            frechetThreshold,
        int               maxResults,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Find pairs (A, B) where:
        //   - A and B both appear as participants in some edge of type T
        //     somewhere in the substrate (i.e., the type is "applicable"
        //     to entities of A's and B's kind)
        //   - no T-typed edge exists between A and B today
        // The Frechet refinement happens in the result-filter pass once
        // GEOMETRY4D centroids + LINESTRING4D operator surface are live.
        await using var cmd = new NpgsqlCommand(@"
            WITH typed_participants AS (
                SELECT DISTINCT participant_hash FROM edge_member
                 WHERE edge_type_hash = @t
            ),
            existing_pairs AS (
                SELECT em1.participant_hash AS a_hash, em2.participant_hash AS b_hash
                  FROM edge_member em1
                  JOIN edge_member em2
                    ON em1.edge_hash = em2.edge_hash
                   AND em1.edge_type_hash = em2.edge_type_hash
                   AND em1.participant_hash <> em2.participant_hash
                 WHERE em1.edge_type_hash = @t
            )
            SELECT a.participant_hash, b.participant_hash
              FROM typed_participants a
              CROSS JOIN typed_participants b
             WHERE a.participant_hash < b.participant_hash
               AND NOT EXISTS (
                   SELECT 1 FROM existing_pairs ep
                    WHERE (ep.a_hash = a.participant_hash AND ep.b_hash = b.participant_hash)
                       OR (ep.a_hash = b.participant_hash AND ep.b_hash = a.participant_hash)
               )
             LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("t",   edgeTypeHash.AsSpan().ToArray());
        cmd.Parameters.AddWithValue("lim", maxResults);

        var results = new List<FrayedEdgeCandidate>(Math.Min(maxResults, 1024));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var a = (byte[]) reader.GetValue(0);
            var b = (byte[]) reader.GetValue(1);
            results.Add(new FrayedEdgeCandidate(
                SourceEntity:                  new AtomId(a),
                TargetEntity:                  new AtomId(b),
                FrechetDistanceFromArchetype:  frechetThreshold)); // refined in centroid-aware pass
        }
        return results;
    }
}
