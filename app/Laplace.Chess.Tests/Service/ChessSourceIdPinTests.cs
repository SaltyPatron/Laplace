using Xunit;
using Laplace.Chess.Service;
using Laplace.Engine.Core;

namespace Laplace.Chess.Tests.Service;

// Source metadata-label IDs verified independently with Engine `laplace text`
// over canonical tier0.bin. Actual corpus witness trunks are a separate contract.
public class ChessSourceIdPinTests
{
    [Theory]
    [InlineData("4e664966bc368ac667b9cf4f1f1c1507", nameof(ChessVocabulary.SourceId))]
    [InlineData("e1d0736ea122d2b06be32a8e08d02825", nameof(ChessVocabulary.PgnSourceId))]
    [InlineData("ef687a7357d809531a9315ab26711a4c", nameof(ChessVocabulary.EvalPgnSourceId))]
    [InlineData("49498a084de4e845ec64a328f43e1559", nameof(ChessVocabulary.ReviewSourceId))]
    [InlineData("eb9427ca70b647946d70cf866bab3f80", nameof(ChessVocabulary.UserPromptSourceId))]
    [InlineData("2bf6e8539cbeb7c281ae2a44d2222695", nameof(ChessVocabulary.OpeningsSourceId))]
    [InlineData("edfbd3ff22c93aa141dbbf819e6bc0c1", nameof(ChessVocabulary.BookSourceId))]
    [InlineData("75c44b4f7ebbc07f4a2d7f0732981670", nameof(ChessVocabulary.AnalysisSourceId))]
    public void ChessSourceIds_Match_NativeText(string hex, string fieldName)
    {
        var field = typeof(ChessVocabulary).GetField(fieldName)
            ?? throw new InvalidOperationException($"{fieldName} not found on ChessVocabulary");
        var id = (Hash128)field.GetValue(null)!;
        Assert.Equal(hex, Convert.ToHexStringLower(id.ToBytes()));
    }
}
