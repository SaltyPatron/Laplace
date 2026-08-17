using Laplace.Decomposers.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class OperatorLanguageTests
{
    [Fact]
    public void ExplicitLanguageOverridesHeaderAndCanonicalizes()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "en-US,en;q=0.9";

        Assert.True(OperatorLanguage.TryResolve(
            request, "Japanese", out var language, out var invalid));

        Assert.Null(invalid);
        Assert.Equal("jpn", language?.Code);
        Assert.Equal("request", language?.Source);
        Assert.Equal(LanguageReference.IdForResolvedCode("jpn").ToBytes(), language?.Id);
    }

    [Fact]
    public void AcceptLanguageUsesQualityThenCanonicalizes()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "en-US;q=0.2, ja-JP;q=0.9";

        Assert.True(OperatorLanguage.TryResolve(
            request, null, out var language, out var invalid));

        Assert.Null(invalid);
        Assert.Equal("jpn", language?.Code);
        Assert.Equal("accept-language", language?.Source);
    }

    [Fact]
    public void UnknownExplicitLanguageIsRejectedRatherThanMappedToUndetermined()
    {
        var request = new DefaultHttpContext().Request;

        Assert.False(OperatorLanguage.TryResolve(
            request, "zz-not-a-language", out var language, out var invalid));

        Assert.Null(language);
        Assert.Equal("zz-not-a-language", invalid);
    }
}
