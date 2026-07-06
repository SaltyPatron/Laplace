using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class EtlManifestRecordFramingTests
{
    [Theory]
    [InlineData("omw", GrammarRecordFraming.Line)]
    [InlineData("tatoeba", GrammarRecordFraming.Line)]
    [InlineData("wiktionary", GrammarRecordFraming.Line)]
    [InlineData("tabular", GrammarRecordFraming.Line)]
    public void LineOrientedSources_UseLineFraming(string cliName, GrammarRecordFraming expected)
    {
        var src = EtlManifest.Get(cliName);
        Assert.Equal(expected, src.Modality.RecordFraming);
    }

    [Fact]
    public void GrammarFileRecordStream_ForSource_UsesManifestFraming()
    {
        var omw = EtlManifest.Get("omw");
        var stream = GrammarFileRecordStream.ForSource("dummy.tab", omw);
        Assert.NotNull(stream);
    }
}
