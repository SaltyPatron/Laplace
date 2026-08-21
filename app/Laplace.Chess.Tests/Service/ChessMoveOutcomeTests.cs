using System.Linq;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Tests.Service;

/// <summary>
/// The fused move-outcome fold: recording a game deposits one aggregated OUTCOME
/// observation per ply onto the MOVE object, ctx = null so testimony merges per
/// (move, source) and consensus stays bounded by the ~7,797-move vocabulary. This is
/// what makes the learned table a consensus LOOKUP instead of the read-time fold that
/// measured 6.4s cold on the deployed API and re-ran after every recorded live game.
/// </summary>
public sealed class ChessMoveOutcomeTests
{
    private const string Game =
        "[Event \"t\"]\n[Site \"s\"]\n[Date \"2024.01.01\"]\n[Round \"1\"]\n"
        + "[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n1. e4 e5 2. Nf3 1-0\n";

    private static (ChessGameRecord Parsed, SubstrateChange Change) Record()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/move-outcomes");
        ChessPgnDecomposer.RecordGame(parsed, b);
        return (parsed, b.SetInputUnitsConsumed(1).Build());
    }

    [Fact]
    public void RecordGame_DepositsOneOutcomePerPly_OnTheMoveObjects()
    {
        var (parsed, change) = Record();
        var onMoves = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.OutcomeType
                        && parsed.MoveIds.Contains(a.SubjectId))
            .ToList();
        Assert.Equal(parsed.MoveIds.Length, onMoves.Count);

        // 1-0: White's plies (1st, 3rd) score Win, Black's (2nd) scores Loss —
        // mover-relative, decided by parity, no board consulted.
        long win = ChessGraph.ScoreFp1e9(PlyOutcome.Win);
        long loss = ChessGraph.ScoreFp1e9(PlyOutcome.Loss);
        for (int i = 0; i < parsed.MoveIds.Length; i++)
        {
            var a = Assert.Single(onMoves, x => x.SubjectId == parsed.MoveIds[i]);
            Assert.Equal(i % 2 == 0 ? win : loss, a.SumScoreFp1e9);
            Assert.Equal(ChessVocabulary.OutcomeObject, a.ObjectId);
            Assert.Null(a.ContextId);   // ctx-null merge: bounded by vocabulary, not games
        }
    }

    [Fact]
    public void RecordGame_WritesTheBackfillMarker_SoTheLaneTrueSkips()
    {
        var (parsed, change) = Record();
        Assert.Contains(change.Entities, e =>
            e.Id == ChessMoveOutcomes.MarkerId(parsed.LineId, ChessMoveOutcomes.Version));
    }
}
