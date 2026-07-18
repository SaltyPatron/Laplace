using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The recorder (ChessPgnDecomposer.RecordGame) must transcribe the WITNESSED layer only — no board
// replay, so no positions/substructures and no geometry (physicalities). All derivation is the
// analyzer's job. See docs/specs/08_Record_vs_Calculate_Spec.txt.
public sealed class ChessRecorderTests
{
    private const string GameWithComment =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 { sharp } e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static SubstrateChange Record(string pgn)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.RecordGame(parsed, b);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void RecordGame_EmitsNoGeometry()
    {
        var change = Record(GameWithComment);
        // The single hard invariant of the split: recording never composes a position.
        Assert.True(change.Physicalities.IsDefaultOrEmpty || change.Physicalities.Length == 0,
            "recorder must emit no physicalities (no geometry)");
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.SubstructureType);
    }

    // GAME GRAIN: one verbatim HAS_MOVETEXT edge carries the whole record. Per-ply record
    // tokens (HAS_PLY/HAS_SAN/HAS_CLOCK/HAS_EVAL_TOKEN/HAS_COMMENT on per-game PlyId
    // subjects) are never attested — a PlyId cannot recur across games, so each such row
    // was a permanently single-witness consensus cell.
    [Fact]
    public void RecordGame_RecordsVerbatimMovetext_AtGameGrain()
    {
        var change = Record(GameWithComment);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PlyType);

        var movetext = ChessPgnDecomposer.MovetextSection(GameWithComment);
        Assert.Equal("1. e4 { sharp } e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0", movetext);
        var movetextId = ContentEmitter.RootId(movetext);
        Assert.NotNull(movetextId);
        Assert.Contains(change.Attestations, a =>
            a.ObjectId == movetextId && a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT"));
    }

    [Fact]
    public void RecordGame_EmitsNoPerPlyRecordTokens()
    {
        var change = Record(GameWithComment);
        foreach (var rel in new[] { "HAS_PLY", "HAS_SAN", "HAS_CLOCK", "HAS_EVAL_TOKEN", "HAS_COMMENT", "MOVE_QUALITY" })
        {
            var typeId = RelationTypeRegistry.RelationTypeId(rel);
            Assert.DoesNotContain(change.Attestations, a => a.TypeId == typeId);
        }
    }

    // Lossless law: the free-text comment survives inside the verbatim movetext content and
    // the per-ply tokens are reconstructible from that one edge (readback re-parses it).
    [Fact]
    public void RecordGame_CommentAndPlyTokens_ReconstructibleFromMovetext()
    {
        var movetext = ChessPgnDecomposer.MovetextSection(GameWithComment);
        Assert.Contains("{ sharp }", movetext);

        var (moves, _, _, _) = ChessWitnessHydrator.ParseMovetext(movetext);
        var parsed = ChessPgnDecomposer.TryParseGame(GameWithComment)!;
        Assert.Equal(parsed.Moves, moves);
    }

    [Fact]
    public void ParseMovetext_RecoversClockTokens_FromVerbatimMovetext()
    {
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 { [%clk 0:03:00] } e5 { [%clk 0:03:00] } 2. Nf3 { [%clk 0:02:58] } 1-0\n";
        var movetext = ChessPgnDecomposer.MovetextSection(pgn);
        var (moves, clocks, _, _) = ChessWitnessHydrator.ParseMovetext(movetext);
        Assert.Equal(new[] { "e4", "e5", "Nf3" }, moves);
        Assert.NotNull(clocks);
        Assert.Equal(PgnClocks.ClockTokens(pgn, 3), clocks);
    }

    // Legacy readback: movetext recorded before the verbatim change was SAN-joined; the
    // parser must still yield the bare moves (no annotations) for those rows.
    [Fact]
    public void ParseMovetext_LegacySanJoined_YieldsBareMoves()
    {
        var (moves, clocks, evals, quality) = ChessWitnessHydrator.ParseMovetext("e4 e5 Qh5 Nc6");
        Assert.Equal(new[] { "e4", "e5", "Qh5", "Nc6" }, moves);
        Assert.Null(clocks);
        Assert.Null(evals);
        Assert.Null(quality);
    }

    [Fact]
    public void RecordGame_RecordsSetUpFenVerbatim()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var pgn = $"[Event \"T\"]\n[SetUp \"1\"]\n[FEN \"{fen}\"]\n\n1. Bb5 a6 1-0\n";
        var change = Record(pgn);
        var fenId = ContentEmitter.RootId(fen);
        Assert.NotNull(fenId);
        Assert.Contains(change.Attestations, a => a.ObjectId == fenId);
    }
}
