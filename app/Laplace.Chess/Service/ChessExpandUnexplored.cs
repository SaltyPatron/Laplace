using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// From a playable state, composes one-ply lines for legal moves whose successor position
/// is not yet among the explored targets, staged into the caller's change for the shared
/// ingest recipe.
/// </summary>
public static class ChessExpandUnexplored
{
    public const string SourceName = "ChessExpand";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);

    /// <summary>
    /// Composes a one-ply line (from→after: move trajectory and position projection) for
    /// each legal move whose successor id is not in <paramref name="exploredTargets"/>, adding
    /// each staged successor to that set. Returns the number of lines staged.
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
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType);
                ChessGraph.AppendLineTrajectory(
                    b, lineId, fromNode, [moveNode], SourceId, nowUs);
                ChessGraph.AppendPositionProjection(
                    b, lineId, new[] { fromNode, toNode }, SourceId, nowUs);
                n++;
            }
        }
        return n;
    }
}
