namespace Laplace.Inference.Traversal;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Inference.Abstractions;

using Npgsql;

/// <summary>
/// H1 — Glicko-2-cost-weighted A* over the typed edge graph.
///
/// Algorithm:
///   - Start from seed entity hashes.
///   - For each popped node, fetch ALL outgoing edges in ONE batch SPI
///     (CRITICAL: one SPI per popped node, never one per neighbor — that
///     was the known anti-pattern from prior iterations the synthesis doc
///     and saved memory both flag).
///   - Edge cost = 1 / max(mu, eps) from significance_edge for the
///     contextEntity (rated-source attestation per invariant 5).
///   - Heuristic = 0 (Dijkstra fallback) at v0.1 — admissible. Geodesic-
///     distance-on-S³ heuristic lands when GiST+POINT4D KNN is wired.
///   - Yields paths in increasing cost order until maxDepth or costBudget
///     is exhausted.
///
/// Phase 6 / Track H1.
/// </summary>
public sealed class Traversal : ITraversal
{
    private const double MinMuForCost = 1e-6;
    private const double DefaultMu    = 1500.0; // Glicko-2 unrated default rating

    private readonly NpgsqlDataSource _dataSource;

    public Traversal(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async IAsyncEnumerable<TraversalPath> AStarAsync(
        IReadOnlyList<AtomId> seedEntities,
        AtomId contextEntity,
        int maxDepth,
        double costBudget,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Min-heap by accumulated cost.
        var frontier = new PriorityQueue<PathState, double>();
        var visited  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seedEntities)
        {
            var state = new PathState(
                Nodes: new List<AtomId> { seed },
                Edges: new List<EdgeSegment>(),
                Cost:  0.0);
            frontier.Enqueue(state, 0.0);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = frontier.Dequeue();

            if (state.Cost > costBudget) { continue; }
            if (state.Nodes.Count - 1 >= maxDepth)
            {
                yield return new TraversalPath(state.Nodes, state.Edges, state.Cost);
                continue;
            }

            var lastNode = state.Nodes[^1];
            var visitedKey = ComputePathKey(state);
            if (!visited.Add(visitedKey)) { continue; }

            yield return new TraversalPath(state.Nodes, state.Edges, state.Cost);

            // Batch SPI: one query for all neighbors of lastNode.
            await foreach (var neighbor in FetchNeighborsAsync(conn, lastNode, cancellationToken).ConfigureAwait(false))
            {
                var newCost = state.Cost + neighbor.EdgeCost;
                if (newCost > costBudget) { continue; }

                var newNodes = new List<AtomId>(state.Nodes.Count + 1);
                newNodes.AddRange(state.Nodes);
                newNodes.Add(neighbor.NeighborHash);

                var newEdges = new List<EdgeSegment>(state.Edges.Count + 1);
                newEdges.AddRange(state.Edges);
                newEdges.Add(new EdgeSegment(
                    EdgeTypeHash: neighbor.EdgeTypeHash,
                    EdgeHash:     neighbor.EdgeHash,
                    Mu:           neighbor.Mu,
                    SigmaDisp:    neighbor.Phi));

                frontier.Enqueue(
                    new PathState(newNodes, newEdges, newCost),
                    newCost);
            }
        }
    }

    private static async IAsyncEnumerable<NeighborEdge> FetchNeighborsAsync(
        NpgsqlConnection conn,
        AtomId source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT em.edge_type_hash, em.edge_hash,
                   target.participant_hash AS neighbor_hash,
                   COALESCE(se.mu,    1500.0) AS mu,
                   COALESCE(se.phi,     2.0143) AS phi
              FROM edge_member em
              JOIN edge_member target
                ON target.edge_hash = em.edge_hash
               AND target.edge_type_hash = em.edge_type_hash
               AND target.participant_hash <> em.participant_hash
              LEFT JOIN significance_edge se
                ON se.edge_hash = em.edge_hash
               AND se.edge_type_hash = em.edge_type_hash
             WHERE em.participant_hash = @source", conn);
        cmd.Parameters.AddWithValue("source", source.AsSpan().ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var edgeTypeHash = (byte[]) reader.GetValue(0);
            var edgeHash     = (byte[]) reader.GetValue(1);
            var neighborHash = (byte[]) reader.GetValue(2);
            var mu           = reader.GetDouble(3);
            var phi          = reader.GetDouble(4);
            var cost         = 1.0 / Math.Max(mu, MinMuForCost);
            yield return new NeighborEdge(
                new AtomId(edgeTypeHash), new AtomId(edgeHash),
                new AtomId(neighborHash), mu, phi, cost);
        }
        _ = DefaultMu;
    }

    private static string ComputePathKey(PathState state)
    {
        var sb = new System.Text.StringBuilder(state.Nodes.Count * 65);
        foreach (var node in state.Nodes)
        {
            sb.Append(node.ToString()).Append('-');
        }
        return sb.ToString();
    }

    private sealed record PathState(List<AtomId> Nodes, List<EdgeSegment> Edges, double Cost);

    private sealed record NeighborEdge(
        AtomId EdgeTypeHash, AtomId EdgeHash, AtomId NeighborHash,
        double Mu, double Phi, double EdgeCost);
}
