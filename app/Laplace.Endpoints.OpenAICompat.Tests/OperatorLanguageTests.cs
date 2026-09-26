using Laplace.Decomposers.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class OperatorLanguageTests
{
    [Fact]
    public void ExplicitLanguageOverridesHeaderAsWritten()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "en-US,en;q=0.9";

        var language = OperatorLanguage.Resolve(request, " Japanese ");

        Assert.Equal("Japanese", language?.Code);
        Assert.Equal("request", language?.Source);
        Assert.Equal(LanguageReference.IdForResolvedCode("Japanese").ToBytes(), language?.Id);
    }

    [Fact]
    public void AcceptLanguageUsesQualityAndKeepsTheTagAsWritten()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "en-US;q=0.2, ja-JP;q=0.9";

        var language = OperatorLanguage.Resolve(request, null);

        Assert.Equal("ja-JP", language?.Code);
        Assert.Equal("accept-language", language?.Source);
        Assert.Equal(LanguageReference.IdForResolvedCode("ja-JP").ToBytes(), language?.Id);
    }

    [Fact]
    public void BlankExplicitLanguageFallsThroughToTheHeader()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "fr-CA";

        var language = OperatorLanguage.Resolve(request, "   ");

        Assert.Equal("fr-CA", language?.Code);
        Assert.Equal("accept-language", language?.Source);
    }

    [Fact]
    public void UnrecognizedExplicitLanguageIsCarriedAsContentNotMappedToUndetermined()
    {
        var request = new DefaultHttpContext().Request;

        var language = OperatorLanguage.Resolve(request, "zz-not-a-language");

        Assert.Equal("zz-not-a-language", language?.Code);
        Assert.Equal("request", language?.Source);
        Assert.NotEqual(LanguageReference.IdForResolvedCode("und").ToBytes(), language?.Id);
    }
}
