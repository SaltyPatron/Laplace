using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class CrazyhouseRecordingTests
{
    // These are the thirteen nonstandard-rule games in Hikaru's May 2016 source archive.
    // Final states were independently replayed with the pinned chess 1.11.2 provider.
    [Fact]
    public void SourceGamesComposeAnalyzeAndReplayEveryRecordedMove()
    {
        CodepointPerfcache.LoadDefault();
        string folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "chess-variant-reference.json")));
        foreach (var item in reference.RootElement.EnumerateArray())
        {
            string source = File.ReadAllText(Path.Combine(folder, item.GetProperty("file").GetString()!));
            var game = ChessPgnDecomposer.TryParseGame(source, requireCompleteSource: true)!;
            Assert.Equal(item.GetProperty("plies").GetInt32(), game.ResolvedMoves.Length);
            var rules = ChessVariants.ByName(item.GetProperty("variant").GetString())!;
            if (rules.Drops) Assert.Contains(game.ResolvedMoves, m => m.IsDrop);
            var replay = ChessPgnDecomposer.MaterializeParsedReplay(game);
            Assert.True(replay.IsCompleteFor(game));
            var expected = Board.FromFen(item.GetProperty("finalFen").GetString()!, rules);
            Assert.Equal(expected.ToFen(), replay.Boards[^1].ToFen());
            Assert.Equal(game.PositionIds, replay.Positions.Select(p => p.Position.Id));

            for (int i = 0; i < replay.Boards.Length; i++)
            {
                var constituents = replay.Positions[i].Substructures
                    .Select(n => (IReadOnlyList<Hash128>)Trajectory.Constituents(n.Trajectory)).ToArray();
                Assert.True(ChessPositionIdentity.TryBoardFromAtomConstituents(constituents, out var reconstructed));
                Assert.Equal(game.PositionIds[i], ChessCompose.PositionId(reconstructed));
                Assert.Equal(replay.Boards[i].Pockets, reconstructed.Pockets);
                Assert.Equal(replay.Boards[i].Promoted, reconstructed.Promoted);
                Assert.Equal(rules, reconstructed.Rules);
                Assert.Equal(replay.Boards[i].Checks, reconstructed.Checks);
            }

            var typed = ChessReplay.Replay(game.MoveIds, game.StartFen, rules: game.Rules);
            Assert.Null(typed.Truncated);
            Assert.Equal(game.ResolvedMoves.Length, typed.Plies.Count);
            Assert.Equal(expected.ToFen(), typed.Plies[^1].Fen);
            var builder = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "crazyhouse/source");
            ChessPgnDecomposer.ComposeGame(game, builder, analyzeInline: true);
            var change = builder.SetInputUnitsConsumed(1).Build();
            Assert.NotEmpty(change.Entities);
            Assert.NotEmpty(change.Attestations);
        }
    }

    [Fact]
    public void CompleteStateIncludesRulesPocketsAndPromotedProvenance()
    {
        var normal = Board.FromFen("4k3/8/8/3Q4/8/8/8/4K3 w - - 0 1");
        var crazy = Board.FromFen("4k3/8/8/3Q4/8/8/8/4K3[] w - - 0 1");
        var pocket = Board.FromFen("4k3/8/8/3Q4/8/8/8/4K3[P] w - - 0 1");
        var promoted = Board.FromFen("4k3/8/8/3Q~4/8/8/8/4K3[P] w - - 0 1");
        Assert.Equal(4, new[] { normal, crazy, pocket, promoted }.Select(b => ChessCompose.PositionId(b)).Distinct().Count());
        var surface = PositionContent.Surface(promoted, "-");
        Assert.True(PositionContent.TryFenFromSurface(surface, out var fen));
        Assert.Equal(promoted.ToFen(), fen);
    }

    [Fact]
    public void PromotedCaptureReturnsPawnAndUndoRestoresBothLocations()
    {
        var b = Board.FromFen("4k3/8/3r4/3Q~4/8/8/8/4K3[] b - - 0 1");
        string before = b.ToFen();
        var move = San.Resolve(b, MoveGen.Legal(b), "Rxd5")!.Value;
        var undo = MoveApply.MakeWithUndo(b, move);
        Assert.Equal(1, b.Pockets[5]);
        Assert.Equal(0UL, b.Promoted);
        MoveApply.Unmake(b, move, undo);
        Assert.Equal(before, b.ToFen());
    }

    [Fact]
    public void PinnedEnPassantDoesNotPreventStateComposition()
    {
        var board = Board.FromFen("4r1k1/8/8/3pP3/8/8/8/4K3[] w - d6 0 1");
        Assert.Equal(-1, ChessModality.CapturableEpSquare(board));
        var withoutEp = Board.FromFen("4r1k1/8/8/3pP3/8/8/8/4K3[] w - - 0 1");
        Assert.Equal(ChessCompose.PositionId(withoutEp), ChessCompose.PositionId(board));
        Assert.DoesNotContain(MoveGen.Legal(board), move => (move.Flags & MoveFlags.EnPassant) != 0);
    }

    [Fact]
    public void DropsRequireReserveAndObeyPawnRanksAndCheckEvasion()
    {
        var b = Board.FromFen("4r1k1/8/8/8/8/8/8/4K3[PN] w - - 0 1");
        var moves = MoveGen.Legal(b);
        Assert.Contains(moves, m => m.ToUci() == "N@e2");
        Assert.DoesNotContain(moves, m => m.ToUci() == "N@d2" || m.ToUci() == "Q@e2");
        Assert.DoesNotContain(moves, m => m.IsDrop && m.Promotion == Piece.WPawn && Board.RankOf(m.To) is 0 or 7);
    }
}
