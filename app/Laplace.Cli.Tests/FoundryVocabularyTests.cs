using Laplace.Decomposers.Model;
using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class FoundryVocabularyTests
{
    [Fact]
    public void SelectedPunctuationReusesItsByteSlotAndRetainsItsContentRole()
    {
        var pieces = FoundryCommands.DeduplicateNativePieces(
        [
            ("<unk>", 0f, 2), ("<0x21>", -20f, 6), ("<0x41>", -20f, 6),
            ("!", 3f, 1), ("▁king", 2f, 1), ("king", 2f, 1),
        ]);

        Assert.Equal(5, pieces.Count);
        Assert.Equal(("!", 3f, 1), pieces[1]);
        Assert.Equal(("<0x41>", -20f, 6), pieces[2]);
        Assert.Contains(pieces, p => p.piece == "▁king");
        Assert.Contains(pieces, p => p.piece == "king");
    }

    [Fact]
    public void TargetEncodingKeepsCaseAndWhitespaceDistinct()
    {
        var pieces = FoundryCommands.DeduplicateNativePieces(
        [
            ("king", 1f, 1), ("King", 1f, 1), ("▁king", 1f, 1),
            (" king", 1f, 1), ("▁King", 1f, 1), ("king", 2f, 1),
        ]);

        Assert.Equal(4, pieces.Count);
        Assert.Equal(new[] { "king", "King", "▁king", "▁King" }, pieces.Select(p => p.piece));
    }

    [Fact]
    public void UnicodeContentDoesNotAliasAnUnrelatedUtf8Byte()
    {
        var pieces = FoundryCommands.DeduplicateNativePieces([("<0xE9>", -20f, 6), ("é", 1f, 1)]);
        Assert.Equal(2, pieces.Count);
    }
}
