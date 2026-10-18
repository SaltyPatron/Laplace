using System.Reflection;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessBookDecomposerTests
{
    private static readonly string BooksDir = ChessCorpusPaths.Books;

    [Fact]
    public void ProseLine_InlineDescriptive()
    {
        var lines = ChessBookDecomposer.ExtractProseLines(
            "For instance, 1. P-K4, P-K4; 2. Kt-KB3, Kt-QB3; 3. B-Kt5 is the Ruy Lopez.").ToList();
        var sans = Assert.Single(lines);
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" }, sans);
    }

    [Fact]
    public void ProseLine_TabularDescriptive()
    {
        var lines = ChessBookDecomposer.ExtractProseLines(
            "          1. P-K4           P-K4\n           2. Kt-KB3         Kt-QB3").ToList();
        var sans = Assert.Single(lines);
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6" }, sans);
    }

    [Fact]
    public void ProseLine_SpacedDescriptive()
    {
        var lines = ChessBookDecomposer.ExtractProseLines(
            "The game began 1 P - K 4, P - K 4; 2 Kt - K B 3, Kt - Q B 3.").ToList();
        var sans = Assert.Single(lines);
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6" }, sans);
    }

    [Fact]
    public void ProseLine_Algebraic()
    {
        var lines = ChessBookDecomposer.ExtractProseLines(
            "Consider 1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 with a solid game.").ToList();
        var sans = Assert.Single(lines);
        Assert.Equal(new[] { "e4", "e5", "Nf3", "Nc6", "Bb5", "a6" }, sans);
    }

    [Fact]
    public void ProseLine_FusedMoveNumbers()
    {
        var lines = ChessBookDecomposer.ExtractProseLines(
            "After 1.e4 c5 2.Nf3 d6 3.d4 cxd4 the Sicilian is open.").ToList();
        var sans = Assert.Single(lines);
        Assert.Equal(new[] { "e4", "c5", "Nf3", "d6", "d4", "cxd4" }, sans);
    }

    [Fact]
    public void DiagramFragment_IsRejected()
    {
        // A sequence quoted from a diagrammed position cannot replay from the standard start.
        Assert.Empty(ChessBookDecomposer.ExtractProseLines(
            "the mate is quickly accomplished by: 1 R - R 7, K - Kt 1; 2 K - Kt 2."));
    }

    [Fact]
    public void ShortLine_IsRejected()
        => Assert.Empty(ChessBookDecomposer.ExtractProseLines("A. White 1. P-K4."));

    [Fact]
    public void SplitEmbeddedPgn_LiftsGameAndKeepsProse()
    {
        const string text = "Some prose about Morphy's finest hour.\n\n"
            + "[Event \"Casual\"]\n[White \"Morphy\"]\n[Black \"Amateur\"]\n"
            + "[Date \"1858.??.??\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 1-0\n\n"
            + "And the prose continues afterwards.";

        var (blocks, remainder) = ChessBookDecomposer.SplitEmbeddedPgn(text);

        var (gameText, context) = Assert.Single(blocks);
        Assert.Contains("[White \"Morphy\"]", gameText);
        Assert.Contains("1. e4 e5", gameText);
        Assert.Contains("finest hour", context);
        Assert.DoesNotContain("[Event", remainder);
        Assert.Contains("continues afterwards", remainder);

        Assert.NotNull(ChessPgnDecomposer.TryParseGame(gameText));
    }

    [Fact]
    public void ResultlessPgnBlock_DoesNotSwallowTheRestOfTheBook()
    {
        // An [Event block whose movetext never reaches a result token must be returned to the
        // prose stream, not consume everything after it.
        const string text = "[Event \"Broken\"]\n[White \"X\"]\n\n1. e4 e5 2. Nf3\n\n"
            + "Later prose: 1. e4 e5 2. Nf3 Nc6 3. Bb5 is still the Ruy Lopez.\n";

        var (blocks, remainder) = ChessBookDecomposer.SplitEmbeddedPgn(text);

        Assert.Empty(blocks);
        Assert.Contains("still the Ruy Lopez", remainder);

        var records = ChessBookDecomposer.ExtractFromText(text, "t").ToList();
        Assert.Contains(records, r => r.GameText is null && r.Sans.Count == 5);
    }

    [Fact]
    public void ExtractFromText_YieldsBothKinds()
    {
        const string text = "Title: A Tiny Chess Book\n\n"
            + "The Ruy Lopez arises after 1. P-K4, P-K4; 2. Kt-KB3, Kt-QB3; 3. B-Kt5 as follows.\n\n"
            + "[Event \"Casual\"]\n[White \"Morphy\"]\n[Black \"Amateur\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 1-0\n";

        var records = ChessBookDecomposer.ExtractFromText(text, "fallback").ToList();

        Assert.Contains(records, r => r.GameText is not null);
        Assert.Contains(records, r => r.GameText is null && r.Sans.Count == 5);
        Assert.All(records, r => Assert.Equal("A Tiny Chess Book", r.BookTitle));
    }

    [SkippableFact]
    public void RealBooks_ExtractionSmoke()
    {
        string blueBook = Path.Combine(BooksDir, "the-blue-book-of-chess.txt");
        Skip.IfNot(File.Exists(blueBook), "Blue Book corpus not present");

        var text = File.ReadAllText(blueBook);
        var (blocks, _) = ChessBookDecomposer.SplitEmbeddedPgn(text);
        Assert.True(blocks.Count > 20, $"expected embedded PGN games in the Blue Book, got {blocks.Count}");

        int parsable = blocks.Count(b => ChessPgnDecomposer.TryParseGame(b.GameText) is not null);
        Assert.True(parsable > blocks.Count / 2,
            $"only {parsable}/{blocks.Count} embedded games parsed");
    }

    [SkippableFact]
    public void RealBooks_DescriptiveProseSmoke()
    {
        string strategy = Path.Combine(BooksDir, "chess-strategy.txt");
        Skip.IfNot(File.Exists(strategy), "chess-strategy corpus not present");

        int grounded = ChessBookDecomposer
            .ExtractFromText(File.ReadAllText(strategy), "Chess Strategy")
            .Count(r => r.GameText is null);
        Assert.True(grounded > 10,
            $"expected descriptive prose lines to ground from Chess Strategy, got {grounded}");
    }

    [Fact]
    public void DiagramFen_ReplaysTheLineFromThatPosition()
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        foreach (var san in new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" })
        {
            var mv = San.Resolve(state.Board, modality.LegalActions(state), san);
            Assert.NotNull(mv);
            state = modality.Apply(state, mv.Value);
        }
        string fen = state.Board.ToFen();
        string text = "The pin is the idea.\n\n" + fen + "\n3...a6 4. Ba4 Nf6\n";

        ChessDropLedger.Reset();
        var line = Assert.Single(
            ChessBookDecomposer.ExtractFromText(text, "Diagram book"),
            r => r.GameText is null);
        Assert.Equal(new[] { "a6", "Ba4", "Nf6" }, line.Sans);
        Assert.Equal(fen, line.StartFen);
        Assert.False(ChessDropLedger.SnapshotCounts().ContainsKey(ChessBookDecomposer.DiagramAnchoredLine));
    }

    [Fact]
    public void MoveNumberContinuation_ReplaysFromTheLineAlreadyReached()
    {
        const string text = "1. e4 e5 2. Nf3 Nc6 3. Bb5 and the pin holds. 3...a6 4. Ba4 Nf6\n";
        var records = ChessBookDecomposer.ExtractFromText(text, "Continuation").ToList();
        Assert.Contains(records, r => r.StartFen is null
            && r.Sans.SequenceEqual(new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" }));
        var cont = Assert.Single(records, r => r.StartFen is not null);
        Assert.Equal(new[] { "a6", "Ba4", "Nf6" }, cont.Sans);
    }

    [Fact]
    public void AnchorAvailableButIllegal_IsNotCalledDiagramAnchored()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3";
        ChessDropLedger.Reset();
        var records = ChessBookDecomposer.ExtractFromText(
            fen + "\n3...Ke9 4. Qz5 5. a1\n", "Broken diagram").ToList();
        Assert.DoesNotContain(records, r => r.GameText is null);
        var counts = ChessDropLedger.SnapshotCounts();
        Assert.False(counts.ContainsKey(ChessBookDecomposer.DiagramAnchoredLine));
        Assert.True(counts.ContainsKey(ChessBookDecomposer.AnchoredLineUnreplayable));
    }

    [Fact]
    public void DiagramFragmentWithoutAnAnchor_IsStillDiagramAnchored()
    {
        ChessDropLedger.Reset();
        var records = ChessBookDecomposer.ExtractFromText(
            "the mate is quickly accomplished by: 1 R - R 7, K - Kt 1; 2 K - Kt 2.", "t").ToList();
        Assert.Empty(records);
        Assert.True(ChessDropLedger.SnapshotCounts().ContainsKey(ChessBookDecomposer.DiagramAnchoredLine));
    }

    [Fact]
    public void Prose_ExplainsTheLineAndTheConceptsItNames()
    {
        const string text = "The knight fork and the pin decide it. 1. e4 e5 2. Nf3 Nc6 3. Bb5 a6\n";
        var record = Assert.Single(ChessBookDecomposer.ExtractFromText(text, "Concepts"));
        var compose = typeof(ChessBookDecomposer).GetMethod(
            "Compose", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(compose);
        using var builder = new SubstrateChangeBuilder(ChessVocabulary.BookSourceId, "test/book-concepts")
            .DeclareSourcePrior(SourceTrust.AcademicCurated);
        compose.Invoke(new ChessBookDecomposer(), [record, builder]);
        var change = builder.Build();
        try
        {
            Assert.Contains(change.Attestations,
                a => a.TypeId == ChessVocabulary.ExplainsType && a.ObjectId == record.LineId);
            Assert.True(ChessConcepts.TryResolve("fork", chessDomain: true, out var fork));
            Assert.True(ChessConcepts.TryResolve("pin", chessDomain: true, out var pin));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.ExplainsType
                && a.ObjectId == fork.Id
                && a.ContextId == fork.Member(ChessConcepts.Domain.Book));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.ExplainsType
                && a.ObjectId == pin.Id
                && a.ContextId == pin.Member(ChessConcepts.Domain.Book));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.IsExampleOfType
                && a.SubjectId == fork.Member(ChessConcepts.Domain.Book)
                && a.ObjectId == fork.Id);
            var declared = ChessSeedManifest.Relations
                .Select(RelationTypeRegistry.RelationTypeId)
                .ToHashSet();
            Assert.All(
                change.Attestations.Select(a => a.TypeId).Distinct(),
                typeId => Assert.Contains(typeId, declared));
        }
        finally
        {
            foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }
}
