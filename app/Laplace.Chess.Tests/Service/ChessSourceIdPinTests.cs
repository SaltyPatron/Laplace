using Xunit;
using Laplace.Chess.Service;
using Laplace.Engine.Core;

namespace Laplace.Chess.Tests.Service;

// Golden-id pins for the chess source ids, minted from the DB side (laplace.canonical_id(),
// the native blake3 the extension binds). Refactors of vocabulary bootstrap / id minting must
// never change these — a failure here means substrate identity drifted, not a stale test.
public class ChessSourceIdPinTests
{
    [Theory]
    [InlineData("a25f8494fbd60682946b94277cd4714b", nameof(ChessVocabulary.SourceId))]
    [InlineData("28b6b80ba59c0e67c750d7eaee070504", nameof(ChessVocabulary.PgnSourceId))]
    [InlineData("3fcfc3337aa4ae3ff95d3ba97b8c6301", nameof(ChessVocabulary.EvalPgnSourceId))]
    [InlineData("fe7d0936a34159bfa73ed41dea73eaf5", nameof(ChessVocabulary.ReviewSourceId))]
    [InlineData("097292469424727720ebca0623036381", nameof(ChessVocabulary.UserPromptSourceId))]
    [InlineData("460f51c7ca893d734470df6e941f9bf7", nameof(ChessVocabulary.OpeningsSourceId))]
    [InlineData("2b41e81a3b5132072ad4b5c14bb3b9b4", nameof(ChessVocabulary.BookSourceId))]
    [InlineData("ae0ca709668a12ae36e9a3183da265fd", nameof(ChessVocabulary.AnalysisSourceId))]
    public void ChessSourceIds_Match_SubstrateGolden(string hex, string fieldName)
    {
        var field = typeof(ChessVocabulary).GetField(fieldName)
            ?? throw new InvalidOperationException($"{fieldName} not found on ChessVocabulary");
        var id = (Hash128)field.GetValue(null)!;
        Assert.Equal(hex, Convert.ToHexStringLower(id.ToBytes()));
    }
}
