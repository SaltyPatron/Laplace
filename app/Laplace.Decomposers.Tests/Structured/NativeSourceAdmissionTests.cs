using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class NativeSourceAdmissionTests
{
    [Fact]
    public async Task PlanReportsAnUnsupportedCorpusFileAlongsideItsAdmittedFiles()
    {
        using var fixture = new NativeCorpus();
        fixture.Write("NamedSequences.txt", "LATIN PAIR;0041 0042\n");
        fixture.Write("FutureProperty.txt", "0041 ; Future_Value\n");
        var result = await NativeCorpus.Run(fixture.Execution, NativeRecipeMode.Plan,
            "unicode", fixture.Root);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Contains("unicode-named-sequences", result.Output, StringComparison.Ordinal);
        Assert.Contains(result.Lines("uncovered"), line => line.EndsWith("/FutureProperty.txt", StringComparison.Ordinal));
        Assert.Contains("recipe coverage is partial", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanDistinguishesDeclaredPackagingExclusionsFromUnsupportedFiles()
    {
        using var fixture = new NativeCorpus();
        fixture.Write("WordFrameNet", "Frame: Activity\nwork v 02413480 gloss\n\n");
        fixture.Write("WordFrameNet.tar.gz", "excluded archive; the plan must not open it");
        var result = await NativeCorpus.Run(fixture.Execution, NativeRecipeMode.Plan,
            "wordframenet", fixture.Root);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Contains("wordframenet-wfn", result.Output, StringComparison.Ordinal);
        Assert.Contains(result.Lines("omitted"), line => line.EndsWith("/WordFrameNet.tar.gz", StringComparison.Ordinal));
        Assert.Empty(result.Lines("uncovered"));
        Assert.DoesNotContain("recipe coverage is partial", result.Output, StringComparison.Ordinal);
    }
}
