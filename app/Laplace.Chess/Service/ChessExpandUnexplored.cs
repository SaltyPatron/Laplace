using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// Phase D campaign helper: from a playable state, emit self-play LINE products for
/// legal moves whose successor position id is not in <paramref name="exploredTargets"/>.
/// Deposits games/lines through the shared spine — not a fourth oracle.
/// Trust weight <see cref="TC.Response"/> so the fold is not poisoned (#447/#449).
/// </summary>
public static class ChessExpandUnexplored
{
    public const string SourceName = "ChessExpand";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);

    /// <summary>Witness weight for self-play expansion (Response band).</summary>
    public const double Weight = TC.Response;

    /// <summary>
    /// Compose one-ply lines (from→after) for each unexplored legal move. Returns how
    /// many lines were staged. Caller owns batching into the ingest spine and may pass
    /// a live set of already-known MOVE object ids for this from-position.
    /// </summary>
    public static int AppendUnexploredOnePly(
        SubstrateChangeBuilder b,
        ChessState from,
        ChessModality modality,
        ISet<Hash128>? exploredTargets = null)
    {
        var legal = modality.LegalActions(from);
        if (legal.Count == 0) return 0;

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var seen = exploredTargets ?? new HashSet<Hash128>();
        int n = 0;

        lock (ChessCompose.Gate)
        {
            string fromKey = modality.StateKey(from);
            var fromNode = ChessCompose.Position(fromKey).Position;
            foreach (var mv in legal)
            {
                var next = modality.Apply(from, mv);
                string toKey = modality.StateKey(next);
                var toId = ChessCompose.PositionId(toKey);
                if (!seen.Add(toId)) continue;

                var toNode = ChessCompose.Position(toKey).Position;
                var lineId = ChessCompose.LineId(new[] { fromNode.Id, toNode.Id });
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, SourceId);
                ChessGraph.AppendGameTrajectory(
                    b, lineId, new[] { fromNode, toNode }, SourceId, nowUs);
                ChessGraph.AppendMoveEdge(
                    b, fromKey, toKey, PlyOutcome.Draw, games: 1, Weight,
                    sourceId: SourceId);
                n++;
            }
        }
        return n;
    }
}
