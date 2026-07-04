using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The recorder (ChessPgnDecomposer.RecordGame) must transcribe the WITNESSED layer only — no board
// replay, so no positions/substructures and no geometry (physicalities). All derivation is the
// analyzer's job. See .scratchpad/08_Record_vs_Calculate.
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

    [Fact]
    public void RecordGame_RecordsGameMovetextAndPlyNodes()
    {
        var change = Record(GameWithComment);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.PlyType);

        var movetextId = ContentEmitter.RootId("e4 e5 Qh5 Nc6 Bc4 Nf6 Qxf7#");
        Assert.NotNull(movetextId);
        Assert.Contains(change.Attestations, a => a.ObjectId == movetextId);
    }

    [Fact]
    public void RecordGame_CapturesFreeTextComment()
    {
        var change = Record(GameWithComment);
        var commentId = ContentEmitter.RootId("sharp");
        Assert.NotNull(commentId);
        Assert.Contains(change.Attestations, a => a.ObjectId == commentId);
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
