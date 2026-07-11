using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessBookDecomposerTests
{
    private static readonly string BooksDir = @"D:\Data\Ingest\test-data\text";

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

    [Fact]
    public void RealBooks_ExtractionSmoke()
    {
        string blueBook = Path.Combine(BooksDir, "the-blue-book-of-chess.txt");
        if (!File.Exists(blueBook)) return;

        var text = File.ReadAllText(blueBook);
        var (blocks, _) = ChessBookDecomposer.SplitEmbeddedPgn(text);
        Assert.True(blocks.Count > 20, $"expected embedded PGN games in the Blue Book, got {blocks.Count}");

        int parsable = blocks.Count(b => ChessPgnDecomposer.TryParseGame(b.GameText) is not null);
        Assert.True(parsable > blocks.Count / 2,
            $"only {parsable}/{blocks.Count} embedded games parsed");
    }

    [Fact]
    public void RealBooks_DescriptiveProseSmoke()
    {
        string strategy = Path.Combine(BooksDir, "chess-strategy.txt");
        if (!File.Exists(strategy)) return;

        int grounded = ChessBookDecomposer
            .ExtractFromText(File.ReadAllText(strategy), "Chess Strategy")
            .Count(r => r.GameText is null);
        Assert.True(grounded > 10,
            $"expected descriptive prose lines to ground from Chess Strategy, got {grounded}");
    }
}
