using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecorderTests
{
    private const string GameWithComment =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 { sharp } e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static (ChessGameRecord Parsed, SubstrateChange Change) Record(string pgn)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.RecordGame(parsed, b);
        return (parsed, b.SetInputUnitsConsumed(1).Build());
    }

    [Fact]
    public void RecordGame_StoresTypedMovesAndNoBoardProjection()
    {
        var (parsed, change) = Record(GameWithComment);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.MoveType);

        var line = Assert.Single(change.Physicalities,
            p => p.EntityId == parsed.LineId && p.Type == PhysicalityType.Content);
        Assert.Equal(parsed.ResolvedMoves.Length, line.NConstituents);
        var actual = Trajectory.Constituents(line.TrajectoryXyzm!);
        var expected = parsed.ResolvedMoves.Select((move, i) =>
            ChessCompose.MoveId(parsed.MovingPieces[i], move)).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RecordGame_HasNoSerializedPgnOrPerPlyTestimony()
    {
        var (_, change) = Record(GameWithComment);
        foreach (var relation in new[]
                 { "HAS_MOVETEXT", "HAS_PLY", "HAS_SAN", "HAS_CLOCK", "HAS_EVAL_TOKEN", "HAS_COMMENT" })
        {
            var type = RelationTypeRegistry.RelationTypeId(relation);
            Assert.DoesNotContain(change.Attestations, a => a.TypeId == type);
        }
        Assert.DoesNotContain(change.Entities, e => e.TypeId == EntityTypeRegistry.Id("Chess_Movetext"));
        Assert.DoesNotContain(change.Entities, e => e.TypeId == EntityTypeRegistry.Id("Chess_Ply"));
    }

    [Fact]
    public void RecordGame_AlignsCommentsWithoutPlyRows()
    {
        var (parsed, change) = Record(GameWithComment);
        var comments = Assert.Single(change.Physicalities,
            p => p.EntityId == parsed.PlayingId && p.Type == PhysicalityType.ChessComment);
        Assert.Equal(parsed.Moves.Count, comments.NConstituents);
        var ids = Trajectory.Constituents(comments.TrajectoryXyzm!);
        Assert.Equal(ContentEmitter.RootId("sharp"), ids[0]);
        Assert.All(ids.Skip(1), id => Assert.Equal(ChessCompose.AnnotationMissing().Id, id));
    }

    [Fact]
    public void RecordGame_AlignsClockCommentsAsSourceContent()
    {
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 { [%clk 0:03:00] } e5 { [%clk 0:03:00] } "
            + "2. Nf3 { [%clk 0:02:58] } 1-0\n";
        var (parsed, change) = Record(pgn);
        var comments = Assert.Single(change.Physicalities,
            p => p.EntityId == parsed.PlayingId && p.Type == PhysicalityType.ChessComment);
        var ids = Trajectory.Constituents(comments.TrajectoryXyzm!);
        Assert.Equal(parsed.Moves.Count, ids.Length);
        Assert.Equal(ContentEmitter.RootId("[%clk 0:03:00]"), ids[0]);
        Assert.Equal(ContentEmitter.RootId("[%clk 0:02:58]"), ids[2]);
    }

    [Fact]
    public void RecordGame_SetupIsTypedBoardNotFenContent()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var pgn = $"[Event \"T\"]\n[SetUp \"1\"]\n[FEN \"{fen}\"]\n\n1. Bb5 a6 1-0\n";
        var (_, change) = Record(pgn);
        var boardId = ChessCompose.PositionId(Board.FromFen(fen));
        Assert.Contains(change.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_SETUP") && a.ObjectId == boardId);
        Assert.Contains(change.Physicalities, p => p.EntityId == boardId);
        var fenId = ContentEmitter.RootId(fen);
        Assert.DoesNotContain(change.Entities, e => e.Id == fenId);
    }
}
