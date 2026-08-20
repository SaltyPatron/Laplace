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
            var fromNode = ChessGraph.ComposePositionPoint(from.Board);
            foreach (var mv in legal)
            {
                var next = modality.Apply(from, mv);
                var toId = ChessCompose.PositionId(next.Board);
                if (!seen.Add(toId)) continue;

                var toNode = ChessGraph.ComposePositionPoint(next.Board);
                Piece moving = from.Board.Squares[mv.From];
                var moveNode = ChessGraph.EmitMove(b, moving, mv, SourceId, nowUs);
                var lineId = ChessCompose.LineId(fromNode.Id, [moveNode.Id]);
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, SourceId);
                ChessGraph.AppendLineTrajectory(b, lineId, [moveNode], SourceId, nowUs);
                ChessGraph.AppendPositionProjection(
                    b, lineId, new[] { fromNode, toNode }, SourceId, nowUs);
                n++;
            }
        }
        return n;
    }
}
