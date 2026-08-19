using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Phase D campaign helper: from a playable state, emit self-play LINE products for
/// legal moves whose successor position id is not in <paramref name="exploredTargets"/>.
/// Deposits games/lines through the shared spine — not a fourth oracle.
/// </summary>
public static class ChessExpandUnexplored
{
    public const string SourceName = "ChessExpand";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);

    /// <summary>
    /// Compose one-ply lines (from→after) for each unexplored legal move. Returns how
    /// many lines were staged. Caller owns batching into the ingest spine and may pass
    /// a live set of already-known successor position ids for this from-position.
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
            var fromNode = ChessGraph.EmitComposed(b, fromKey, SourceId).Position;
            foreach (var mv in legal)
            {
                var next = modality.Apply(from, mv);
                string toKey = modality.StateKey(next);
                var toId = ChessCompose.PositionId(toKey);
                if (!seen.Add(toId)) continue;

                var toNode = ChessGraph.EmitComposed(b, toKey, SourceId).Position;
                var lineId = ChessCompose.LineId(new[] { fromNode.Id, toNode.Id });
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, SourceId);
                ChessGraph.AppendGameTrajectory(
                    b, lineId, new[] { fromNode, toNode }, SourceId, nowUs);
                n++;
            }
        }
        return n;
    }
}
