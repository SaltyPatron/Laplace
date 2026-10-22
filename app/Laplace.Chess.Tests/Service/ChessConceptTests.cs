using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessConceptTests
{
    [Fact]
    public void Fork_StaysOutOfTheConceptUntilTheChessDomainAdmitsIt()
    {
        Assert.False(ChessConcepts.TryResolve("fork", chessDomain: false, out _));
        Assert.True(ChessConcepts.TryResolve("fork", chessDomain: true, out var concept));
        Assert.Equal("Fork", concept.Name);
        Assert.Equal("Fork", ChessMoveCommentary.MotifLabel("fork"));
    }

    [Theory]
    [InlineData("gambit")]
    [InlineData("fork")]
    [InlineData("pin")]
    [InlineData("skewer")]
    [InlineData("discovered attack")]
    [InlineData("sacrifice")]
    [InlineData("zugzwang")]
    [InlineData("fianchetto")]
    [InlineData("outpost")]
    [InlineData("minority attack")]
    [InlineData("opposition")]
    public void EachChessSense_IsOneConceptAcrossLexicalOpeningBookAndBoard(string surface)
    {
        Assert.True(ChessConcepts.TryResolve(surface, chessDomain: true, out var concept));
        Assert.Equal(Hash128.Text("chess/concept/" + concept.Key), concept.Id);
        Assert.NotEqual(concept.Member(ChessConcepts.Domain.Lexical), concept.Member(ChessConcepts.Domain.Opening));
        Assert.NotEqual(concept.Member(ChessConcepts.Domain.Book), concept.Member(ChessConcepts.Domain.Board));
        Assert.False(string.IsNullOrWhiteSpace(concept.LexicalSense));
        Assert.True(concept.MatchesOpening(concept.Name + " Accepted"));
        Assert.True(concept.MatchesBook("The line is a " + concept.Name + " in this position."));
        Assert.True(concept.MatchesBoard(concept.BoardTag));
        Assert.False(concept.MatchesBoard("hanging_piece_won"));
    }

    [Fact]
    public void DiscoveredCheck_UsesTheChessConceptRatherThanALabelSwitch()
    {
        Assert.Equal("Discovered attack", ChessMoveCommentary.MotifLabel("discovered_check"));
        Assert.Equal("hanging piece won", ChessMoveCommentary.MotifLabel("hanging_piece_won"));
    }
}
